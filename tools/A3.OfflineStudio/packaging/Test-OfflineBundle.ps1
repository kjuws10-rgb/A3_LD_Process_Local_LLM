#requires -Version 5.1
[CmdletBinding()]
param([switch]$IncludeNetwork)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'OfflineBundle.Core.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('A3BundleCheck-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
function Assert-True { param([bool]$Condition, [string]$Message) if (-not $Condition) { throw $Message } }
function Assert-Throws {
    param([scriptblock]$Action, [string]$Message)
    $failed = $false
    try { & $Action } catch { $failed = $true }
    Assert-True $failed $Message
}
try {
    $stage = Join-Path $testRoot 'stage'
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'models'))
    [void][IO.Directory]::CreateDirectory((Join-Path $stage 'runtime'))
    [IO.File]::WriteAllText((Join-Path $stage 'models/model.gguf'), 'GGUF fixture for packaging checks')
    [IO.File]::WriteAllText((Join-Path $stage 'runtime/llama-server.exe'), 'runtime fixture')
    $modelHash = Get-BundleSha256 (Join-Path $stage 'models/model.gguf')
    $manifest = Write-BundleManifest $stage 'Fixture model' $modelHash @([ordered]@{ url = 'https://example.invalid/fixture' })
    $loaded = Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw | ConvertFrom-Json
    Assert-True ($loaded.schemaVersion -eq 1) 'Manifest schema differs from the importer contract.'
    Assert-True ($loaded.model.file -eq 'models/model.gguf') 'Model path is invalid.'
    Assert-True (@($loaded.files).Count -eq 2) 'Manifest must include every payload file exactly once.'
    foreach ($entry in $loaded.files) {
        Assert-True (Test-BundleFile (Join-Path $stage $entry.path) $entry.size $entry.sha256) 'Manifest digest/size is inconsistent.'
        Assert-True (-not $entry.path.Contains('\')) 'Manifest paths must use forward slashes.'
    }
    $archivePath = Join-Path $testRoot 'fixture.zip'
    $archiveHash = Compress-OfflineBundle $stage $archivePath
    Assert-True ($archiveHash -eq (Get-BundleSha256 $archivePath)) 'Archive digest is invalid.'
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        Assert-True ($null -ne $archive.GetEntry('manifest.json')) 'Archive has an unwanted parent folder.'
        Assert-True ($null -ne $archive.GetEntry('models/model.gguf')) 'Archive model layout is invalid.'
    } finally { $archive.Dispose() }
    Assert-Throws { Compress-OfflineBundle $stage $archivePath } 'Existing output must not be overwritten.'

    # The runtime extractor must reject traversal before creating any escaped file.
    $unsafePath = Join-Path $testRoot 'unsafe.zip'
    $unsafe = [IO.Compression.ZipFile]::Open($unsafePath, [IO.Compression.ZipArchiveMode]::Create)
    try { [void]$unsafe.CreateEntry('../escaped.txt') } finally { $unsafe.Dispose() }
    Assert-Throws { Expand-BundleRuntime $unsafePath (Join-Path $testRoot 'unsafe-output') } 'Traversal runtime archive was accepted.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $testRoot 'escaped.txt'))) 'Unsafe runtime archive escaped its root.'

    # A valid fixture proves DLL/runtime files survive extraction with exact bytes.
    $runtimeSource = Join-Path $testRoot 'runtime-source'
    [void][IO.Directory]::CreateDirectory($runtimeSource)
    [IO.File]::WriteAllText((Join-Path $runtimeSource 'llama-server.exe'), 'exe fixture')
    [IO.File]::WriteAllText((Join-Path $runtimeSource 'ggml.dll'), 'dll fixture')
    $runtimeArchive = Join-Path $testRoot 'runtime.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($runtimeSource, $runtimeArchive)
    $runtimeOutput = Join-Path $testRoot 'runtime-output'
    Expand-BundleRuntime $runtimeArchive $runtimeOutput
    Assert-True ((Get-BundleSha256 (Join-Path $runtimeOutput 'ggml.dll')) -eq (Get-BundleSha256 (Join-Path $runtimeSource 'ggml.dll'))) 'Runtime DLL bytes changed.'
    Assert-Throws { Receive-VerifiedBundleFile 'http://example.invalid/file' (Join-Path $testRoot 'denied') 1 ('0' * 64) 1 } 'Non-HTTPS source was accepted.'

    # Custom GGUF preparation uses exact local bytes and never infers a Qwen license.
    $customPath = Join-Path $testRoot 'private-original-model.gguf'
    [IO.File]::WriteAllBytes($customPath, ([Text.Encoding]::ASCII.GetBytes('GGUF') + (New-Object byte[] 28)))
    $custom = Get-CustomBundleModel $customPath 'A3 custom model'
    Assert-True ($custom.name -eq 'A3 custom model' -and $custom.size -eq 32 -and $custom.sha256 -eq (Get-BundleSha256 $customPath)) 'Custom model digest/size is invalid.'
    $badCustom = Join-Path $testRoot 'invalid.gguf'
    [IO.File]::WriteAllBytes($badCustom, (New-Object byte[] 32))
    Assert-Throws { Get-CustomBundleModel $badCustom } 'Invalid GGUF magic was accepted.'
    $shardPath = Join-Path $testRoot 'custom-00001-of-00002.gguf'
    Copy-Item -LiteralPath $customPath -Destination $shardPath
    Assert-Throws { Get-CustomBundleModel $shardPath } 'A conventional split GGUF filename was accepted.'
    Assert-Throws { Get-CustomBundleModel $customPath 'C:\private\model' } 'A source path was accepted as the custom display name.'
    $customLicense = Join-Path $testRoot 'custom-license.txt'
    $copiedLicense = Join-Path $testRoot 'copied-license.txt'
    [IO.File]::WriteAllText($customLicense, 'Custom fixture license; no publisher rights are implied.')
    Copy-CustomBundleLicense $customLicense $copiedLicense
    Assert-True ((Get-BundleSha256 $customLicense) -eq (Get-BundleSha256 $copiedLicense)) 'Custom license bytes changed.'
    $binaryLicense = Join-Path $testRoot 'binary-license.txt'
    [IO.File]::WriteAllBytes($binaryLicense, (New-Object byte[] 16))
    Assert-Throws { Copy-CustomBundleLicense $binaryLicense (Join-Path $testRoot 'denied-license.txt') } 'Binary custom license was accepted.'

    if ($IncludeNetwork) {
        $licenseUrl = 'https://raw.githubusercontent.com/ggml-org/llama.cpp/b11960/LICENSE'
        $licenseHash = '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d'
        $licenseFile = Join-Path $testRoot 'license.txt'
        Receive-VerifiedBundleFile $licenseUrl $licenseFile 1078 $licenseHash
        # Cache hit must not require a working download URL.
        Receive-VerifiedBundleFile 'https://example.invalid/unreachable' $licenseFile 1078 $licenseHash
        $partialBytes = [IO.File]::ReadAllBytes($licenseFile)[0..399]
        [IO.File]::WriteAllBytes(($licenseFile + '.partial'), $partialBytes)
        Remove-Item -LiteralPath $licenseFile
        Receive-VerifiedBundleFile $licenseUrl $licenseFile 1078 $licenseHash
        Assert-True (Test-BundleFile $licenseFile 1078 $licenseHash) 'Partial/resume download is invalid.'
        Assert-Throws { Receive-VerifiedBundleFile $licenseUrl (Join-Path $testRoot 'wrong-hash.txt') 1078 ('0' * 64) 1 } 'A wrong SHA-256 was accepted.'
    }
    Write-Host 'PASS: manifest, ZIP layout/digests, no overwrite, runtime extraction/traversal, source policy, custom GGUF/license, and optional download checks.'
} finally {
    # Delete only the unique directory created by this invocation.
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('A3BundleCheck-')) {
        Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
    }
}
