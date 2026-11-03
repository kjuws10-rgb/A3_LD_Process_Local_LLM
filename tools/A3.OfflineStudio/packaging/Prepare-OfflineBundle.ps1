#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('fast', 'quality')][string]$ModelProfile = 'fast',
    [string]$OutputDirectory,
    [string]$CacheDirectory,
    [string]$AppDirectory,
    [string]$DotNetPath = 'dotnet',
    [string]$CustomModelPath,
    [string]$CustomModelName = 'Custom local GGUF',
    [string]$CustomModelLicensePath,
    [switch]$ModelRuntimeOnly,
    [switch]$ValidateCatalogOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'OfflineBundle.Core.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'output' }
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $PSScriptRoot '.cache' }
if (-not $AppDirectory -and -not $ModelRuntimeOnly -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'PublishedApp/A3.OfflineStudio.exe') -PathType Leaf)) { $AppDirectory = Join-Path $PSScriptRoot 'PublishedApp' }

# Versions, byte counts and digests were checked against official publisher APIs.
# Changing a model/runtime requires reviewing and pinning all four fields together.
$runtime = [ordered]@{
    name = 'llama.cpp b11960 Windows CPU x64'
    url = 'https://github.com/ggml-org/llama.cpp/releases/download/b11960/llama-b11960-bin-win-cpu-x64.zip'
    size = 24407745L
    sha256 = '73f962f82850d244db47eaacb9d913637027b82399124324298576d58c5f4287'
    licenseUrl = 'https://raw.githubusercontent.com/ggml-org/llama.cpp/b11960/LICENSE'
    licenseSize = 1078L
    licenseSha256 = '94f29bbed6a22c35b992c5c6ebf0e7c92f13b836b90f36f461c9cf2f0f1d010d'
}
$isCustomModel = -not [string]::IsNullOrWhiteSpace($CustomModelPath)
if ($CustomModelLicensePath -and -not $isCustomModel) { throw 'CustomModelLicensePath requires CustomModelPath.' }
$packageProfile = if ($isCustomModel) { 'custom' } else { $ModelProfile }
$model = if ($isCustomModel) {
    Get-CustomBundleModel $CustomModelPath $CustomModelName
} elseif ($ModelProfile -eq 'quality') {
    [ordered]@{
        name = 'Qwen2.5-Coder-7B-Instruct Q4_K_M'
        repository = 'Qwen/Qwen2.5-Coder-7B-Instruct-GGUF'
        revision = '13fb94bfda8c8cf22497dc57b78f391a9acb426a'
        file = 'qwen2.5-coder-7b-instruct-q4_k_m.gguf'
        size = 4683073536L
        sha256 = '509287f78cb4d4cf6b3843734733b914b2c158e43e22a7f4bf5e963800894d3c'
    }
} else {
    [ordered]@{
        name = 'Qwen2.5-Coder-1.5B-Instruct Q4_K_M'
        repository = 'Qwen/Qwen2.5-Coder-1.5B-Instruct-GGUF'
        revision = 'f86cb2c1fa58255f8052cc32aeede1b7482d4361'
        file = 'qwen2.5-coder-1.5b-instruct-q4_k_m.gguf'
        size = 1117320768L
        sha256 = 'cc324af070c2ecbfd324a30884d2f951a7ff756aba85cb811a6ec436933bb046'
    }
}
$modelUrl = $null; $modelLicenseUrl = $null
if (-not $isCustomModel) {
    $modelUrl = 'https://huggingface.co/' + $model.repository + '/resolve/' + $model.revision + '/' + $model.file
    $modelLicenseUrl = 'https://huggingface.co/' + $model.repository + '/resolve/' + $model.revision + '/LICENSE'
}

if ($ValidateCatalogOnly) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b11960' -UseBasicParsing
    $asset = @($release.assets | Where-Object { $_.browser_download_url -eq $runtime.url })
    if ($asset.Count -ne 1 -or $asset[0].size -ne $runtime.size -or $asset[0].digest -ne ('sha256:' + $runtime.sha256)) { throw 'Pinned runtime metadata does not match GitHub.' }
    if (-not $isCustomModel) {
        $treeUrl = 'https://huggingface.co/api/models/' + $model.repository + '/tree/' + $model.revision
        $tree = Invoke-RestMethod -Uri $treeUrl -UseBasicParsing
        $file = @($tree | Where-Object { $_.path -eq $model.file })
        if ($file.Count -ne 1 -or $file[0].size -ne $model.size -or $file[0].lfs.oid -ne $model.sha256) { throw 'Pinned model metadata does not match Hugging Face.' }
    }
    Write-Host 'Source catalog verified against official publisher APIs. No large files downloaded.'
    exit 0
}

if ($AppDirectory -and $ModelRuntimeOnly) { throw 'Choose AppDirectory or ModelRuntimeOnly, not both.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($CacheDirectory)
$stage = Join-Path $OutputDirectory ('bundle-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)
Write-Host ('A3 Offline Studio: preparing ' + $packageProfile + ' package')
if ($isCustomModel) {
    Write-Host ('Using local GGUF: {0:N2} GiB. No model download. Allow staging + ZIP disk space (about 2x).' -f ($model.size / 1GB))
} else {
    Write-Host ('Model download: {0:N2} GiB. Allow cache + staging + ZIP disk space (about 3x).' -f ($model.size / 1GB))
}
Write-Host 'This preparation runs on the internet-connected PC. No project code is uploaded.'

if (-not $ModelRuntimeOnly) {
    $appTarget = Join-Path $stage 'app'
    if ($AppDirectory) {
        $AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
        if (-not (Test-Path -LiteralPath (Join-Path $AppDirectory 'A3.OfflineStudio.exe') -PathType Leaf)) { throw 'AppDirectory must contain a published A3.OfflineStudio.exe.' }
        if (((Get-Item -LiteralPath $AppDirectory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'AppDirectory cannot be a link or reparse point.' }
        if (($appTarget + '\').StartsWith(($AppDirectory.TrimEnd('\') + '\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputDirectory cannot be inside AppDirectory.' }
        $links = @(Get-ChildItem -LiteralPath $AppDirectory -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($links.Count -gt 0) { throw 'AppDirectory cannot contain links or reparse points.' }
        [void][IO.Directory]::CreateDirectory($appTarget)
        Get-ChildItem -LiteralPath $AppDirectory -Force | Copy-Item -Destination $appTarget -Recurse -Force
    } else {
        $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'A3.OfflineStudio.csproj'
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw 'Application project not found. Supply -AppDirectory or use -ModelRuntimeOnly.' }
        Write-Host 'Publishing the Windows app, including its .NET runtime...'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        & $DotNetPath publish $project --configuration Release --runtime win-x64 --self-contained true --output $appTarget -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
        if ($LASTEXITCODE -ne 0) { throw 'App publish failed. Install a .NET 10 SDK on this external PC or supply -AppDirectory.' }
    }
}

$runtimeCache = Join-Path $CacheDirectory 'llama-b11960-bin-win-cpu-x64.zip'
Receive-VerifiedBundleFile $runtime.url $runtimeCache $runtime.size $runtime.sha256
$modelCache = if ($isCustomModel) { $model.localPath } else { Join-Path $CacheDirectory $model.file }
if (-not $isCustomModel) { Receive-VerifiedBundleFile $modelUrl $modelCache $model.size $model.sha256 }
Expand-BundleRuntime $runtimeCache (Join-Path $stage 'runtime')
[void][IO.Directory]::CreateDirectory((Join-Path $stage 'models'))
Copy-Item -LiteralPath $modelCache -Destination (Join-Path $stage 'models/model.gguf')
if (-not (Test-BundleFile (Join-Path $stage 'models/model.gguf') $model.size $model.sha256)) { throw 'Model changed during packaging. Run preparation again after the source file is stable.' }
$licenseDirectory = Join-Path $stage 'licenses'
[void][IO.Directory]::CreateDirectory($licenseDirectory)
$runtimeLicenseCache = Join-Path $CacheDirectory 'llama-b11960-LICENSE.txt'
Receive-VerifiedBundleFile $runtime.licenseUrl $runtimeLicenseCache $runtime.licenseSize $runtime.licenseSha256
Copy-Item -LiteralPath $runtimeLicenseCache -Destination (Join-Path $licenseDirectory 'llama.cpp-MIT.txt')
$modelNotice = ''
if ($isCustomModel) {
    if ($CustomModelLicensePath) { Copy-CustomBundleLicense $CustomModelLicensePath (Join-Path $licenseDirectory 'custom-model-LICENSE.txt') }
    $modelNotice = @"
Model: $($model.name), custom local GGUF supplied by the administrator.
Model SHA-256: $($model.sha256)
The model's license, redistribution rights, training data rights and suitability
must be checked by the administrator. No Qwen or Apache-2.0 model license is implied.
A supplied license is copied to licenses/custom-model-LICENSE.txt when provided.
The local source path and original filename are intentionally not recorded.
GGUF header and integrity checks do not prove compatibility or model quality.
"@
} else {
    $modelLicenseCache = Join-Path $CacheDirectory 'Qwen2.5-Coder-Apache-2.0.txt'
    Receive-VerifiedBundleFile $modelLicenseUrl $modelLicenseCache 11343L '832dd9e00a68dd83b3c3fb9f5588dad7dcf337a0db50f7d9483f310cd292e92e'
    Copy-Item -LiteralPath $modelLicenseCache -Destination (Join-Path $licenseDirectory 'Qwen-Apache-2.0.txt')
    $modelNotice = "Model: $($model.name), Apache-2.0, https://huggingface.co/$($model.repository)`nModel revision: $($model.revision)"
}
$notice = @"
A3 Offline Studio third-party notices

Inference engine: llama.cpp b11960, MIT license, https://github.com/ggml-org/llama.cpp
$modelNotice
The corresponding publisher license texts are included under licenses/.
An included self-contained application also contains Microsoft .NET runtime components;
retain its published notices and review https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
and https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT when redistributing.

The package manifest and SHA-256 hashes detect corruption and unexpected file changes.
They do not prove publisher identity: this bundle is not digitally signed.
Transfer a bundle prepared by your trusted external workstation.
This bundle updates local model/runtime files; it does not retrain a model.
"@
[IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY-NOTICES.txt'), $notice, (New-Object Text.UTF8Encoding $false))
$sources = @([ordered]@{ name = $runtime.name; url = $runtime.url; sha256 = $runtime.sha256; license = 'MIT'; licenseUrl = $runtime.licenseUrl })
if ($isCustomModel) {
    $source = [ordered]@{ kind = 'localmodel'; name = $model.name; sha256 = $model.sha256; license = 'administrator-supplied' }
    if ($CustomModelLicensePath) { $source.licenseFile = 'licenses/custom-model-LICENSE.txt' }
    $sources += $source
} else {
    $sources += [ordered]@{ name = $model.name; url = $modelUrl; revision = $model.revision; sha256 = $model.sha256; license = 'Apache-2.0'; licenseUrl = $modelLicenseUrl }
}
$manifest = Write-BundleManifest $stage $model.name $model.sha256 $sources
$archivePath = Join-Path $OutputDirectory ($manifest.bundleId + '-' + $packageProfile + '.zip')
$archiveHash = Compress-OfflineBundle $stage $archivePath
Write-Host ''
Write-Host 'READY - copy this ZIP to USB:' -ForegroundColor Green
Write-Host $archivePath
Write-Host ('SHA-256: ' + $archiveHash)
Write-Host 'On the offline PC: open A3 Offline Studio, then choose the update/import ZIP button.'
Write-Host ('Staging directory retained for first install: ' + $stage)
