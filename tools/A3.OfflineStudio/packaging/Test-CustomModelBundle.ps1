#requires -Version 5.1
[CmdletBinding()]
param([string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'OfflineBundle.Core.ps1')
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $PSScriptRoot '.cache' }
# This end-to-end fixture intentionally cannot download. Populate the official
# runtime/license cache using normal preparation before running this check.
if (-not (Test-BundleFile (Join-Path $CacheDirectory 'llama-b11960-bin-win-cpu-x64.zip') 24407745L '73f962f82850d244db47eaacb9d913637027b82399124324298576d58c5f4287')) { throw 'Verified runtime cache is required; no fixture downloads are performed.' }
if (-not (Test-BundleFile (Join-Path $CacheDirectory 'llama-b11960-LICENSE.txt') 1078L '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d')) { throw 'Verified runtime license cache is required.' }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('A3CustomBundleCheck-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    $secretOriginal = Join-Path $testRoot 'private-training-filename.gguf'
    [IO.File]::WriteAllBytes($secretOriginal, ([Text.Encoding]::ASCII.GetBytes('GGUF') + (New-Object byte[] 28)))
    $license = Join-Path $testRoot 'private-license-name.txt'
    [IO.File]::WriteAllText($license, 'Custom model fixture license.')
    foreach ($withLicense in @($true, $false)) {
        $output = Join-Path $testRoot ('output-' + $withLicense)
        $parameters = @{ CustomModelPath = $secretOriginal; CustomModelName = 'A3 fixture custom'; ModelRuntimeOnly = $true; CacheDirectory = $CacheDirectory; OutputDirectory = $output }
        if ($withLicense) { $parameters.CustomModelLicensePath = $license }
        & (Join-Path $PSScriptRoot 'Prepare-OfflineBundle.ps1') @parameters
        $zip = @(Get-ChildItem -LiteralPath $output -Filter '*.zip' -File)
        if ($zip.Count -ne 1 -or -not $zip[0].Name.EndsWith('-custom.zip')) { throw 'Custom archive was not generated.' }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zip[0].FullName)
        try {
            $reader = New-Object IO.StreamReader ($archive.GetEntry('manifest.json').Open())
            try { $json = $reader.ReadToEnd() } finally { $reader.Dispose() }
            $manifest = $json | ConvertFrom-Json
            if ($archive.GetEntry('models/model.gguf').Length -ne 32 -or $manifest.model.sha256 -ne (Get-BundleSha256 $secretOriginal)) { throw 'Custom model payload length or manifest digest is invalid.' }
            $source = @($manifest.sources | Where-Object { $null -ne $_.PSObject.Properties['kind'] -and $_.kind -eq 'localmodel' })
            if ($source.Count -ne 1 -or $source[0].sha256 -ne (Get-BundleSha256 $secretOriginal) -or $source[0].license -ne 'administrator-supplied') { throw 'Custom source metadata is wrong.' }
            if ($json.Contains($testRoot) -or $json.Contains('private-training-filename') -or $json.Contains('private-license-name')) { throw 'Custom metadata leaked local paths or original filenames.' }
            if ($null -ne $archive.GetEntry('licenses/Qwen-Apache-2.0.txt')) { throw 'Custom model was incorrectly assigned a Qwen license.' }
            if (($null -ne $archive.GetEntry('licenses/custom-model-LICENSE.txt')) -ne $withLicense) { throw 'Optional custom license inclusion is wrong.' }
            $reader = New-Object IO.StreamReader ($archive.GetEntry('THIRD-PARTY-NOTICES.txt').Open())
            try { $notice = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if (-not $notice.Contains('must be checked by the administrator') -or $notice.Contains('Model: A3 fixture custom, Apache-2.0')) { throw 'Custom license responsibility is not clear.' }
        } finally { $archive.Dispose() }
    }
    Write-Host 'PASS: custom GGUF end-to-end bundles with/without optional license; SHA/size, private path omission and license attribution checked. No model inference or download.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('A3CustomBundleCheck-')) { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue }
}
