#requires -Version 5.1
[CmdletBinding()]
param([string]$RepositoryPath, [string]$OutputDirectory, [string]$CacheDirectory, [switch]$ValidateCatalogOnly)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'OfflineBundle.Core.ps1')
if (-not $RepositoryPath -and -not $ValidateCatalogOnly) {
    $appRepository = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
    $candidates = @((Join-Path (Split-Path -Parent $PSScriptRoot) 'Project'), (Join-Path $appRepository 'Project'), (Join-Path (Split-Path -Parent $appRepository) 'A3_LD_Process_SW_IPS'))
    $RepositoryPath = $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Drilling.sln') -PathType Leaf } | Select-Object -First 1
    if (-not $RepositoryPath) {
        Add-Type -AssemblyName System.Windows.Forms
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = 'Select A3_LD_Process_SW_IPS (Drilling.sln)'
        try { if ($dialog.ShowDialog() -eq [Windows.Forms.DialogResult]::OK) { $RepositoryPath = $dialog.SelectedPath } else { throw 'No target repository selected.' } } finally { $dialog.Dispose() }
    }
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'output' }
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $PSScriptRoot '.cache' }
if ($RepositoryPath) { $RepositoryPath = [IO.Path]::GetFullPath($RepositoryPath) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
if (-not $ValidateCatalogOnly -and -not (Test-Path -LiteralPath (Join-Path $RepositoryPath 'Drilling.sln') -PathType Leaf)) { throw 'RepositoryPath must contain Drilling.sln.' }

$metadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$sdkVersion = '10.0.400'
$sdkUrl = 'https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.400/dotnet-sdk-10.0.400-win-x64.zip'
$sdkSha512 = '9b8b88590e4da131bfd0da7aa089d0fc04d5418d5f8607ec13d55dc5a17b4399afd54d496c12657fa05c6c6546dc5eab930f26ac6c50f2d3a7712c0fb378c366'
$sdkSize = 300546129L
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$metadata = Invoke-RestMethod -Uri $metadataUrl -UseBasicParsing
$releases = @($metadata.releases | Where-Object { $_.sdk.version -eq $sdkVersion -or @($_.sdks | Where-Object { $_.version -eq $sdkVersion }).Count -gt 0 })
$official = @($releases | ForEach-Object { if ($_.sdk.version -eq $sdkVersion) { $_.sdk.files }; foreach ($entry in $_.sdks) { if ($entry.version -eq $sdkVersion) { $entry.files } } } | Where-Object { $_.url -eq $sdkUrl -and $_.rid -eq 'win-x64' })
if ($official.Count -eq 0 -or @($official | Where-Object { $_.hash -ne $sdkSha512 }).Count -gt 0) { throw 'Pinned SDK URL/SHA-512 no longer matches official .NET release metadata.' }
if ($ValidateCatalogOnly) { Write-Host 'PASS: official .NET SDK 10.0.400 Windows x64 ZIP URL/SHA-512 verified. No SDK downloaded.'; exit 0 }

[void][IO.Directory]::CreateDirectory($OutputDirectory)
[void][IO.Directory]::CreateDirectory($CacheDirectory)
$sdkCache = Join-Path $CacheDirectory 'dotnet-sdk-10.0.400-win-x64.zip'
Receive-VerifiedBundleFile $sdkUrl $sdkCache $sdkSize $sdkSha512 -HashAlgorithm SHA512
# A short temporary root avoids Windows PowerShell/.NET Framework MAX_PATH
# failures on SDK files with deep SdkResolvers paths.
$work = Join-Path ([IO.Path]::GetTempPath()) ('A3TC-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
$stage = Join-Path $work 'toolchain'
$snapshot = Join-Path $work 'source-snapshot'
$restorePackages = Join-Path $work 'restore-packages'
$offlinePackages = Join-Path $work 'offline-verify-packages'
[void][IO.Directory]::CreateDirectory($stage)
[void][IO.Directory]::CreateDirectory($snapshot)
try {
    Write-Host 'Extracting verified portable .NET SDK (no global installation)...'
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($sdkCache)
    $prefix = [IO.Path]::GetFullPath($stage).TrimEnd('\') + '\'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $expanded = 0L
    try {
        if ($archive.Entries.Count -gt 30000) { throw 'SDK archive contains too many files.' }
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or $name.Split('\') -contains '..' -or -not $seen.Add($name)) { throw 'Unsafe or duplicate SDK archive path.' }
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'SDK archive links are not allowed.' }
            $expanded += $entry.Length
            if ($expanded -gt 3GB -or $entry.Length -gt 512MB) { throw 'SDK extraction exceeds size limits.' }
            $target = [IO.Path]::GetFullPath([IO.Path]::Combine($prefix, $name))
            if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'SDK extraction escapes its root.' }
            if ($name.EndsWith('\')) { [void][IO.Directory]::CreateDirectory($target); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $false)
        }
    } finally { $archive.Dispose() }
    $dotnet = Join-Path $stage 'dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw 'SDK archive has no dotnet.exe.' }
    $env:DOTNET_ROOT = $stage
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    $env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    $env:MSBUILDDISABLENODEREUSE = '1'
    $env:DOTNET_CLI_HOME = Join-Path $work 'dotnet-home'
    $env:NUGET_PACKAGES = $restorePackages
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $work 'http-cache'
    & $dotnet --info
    if ($LASTEXITCODE -ne 0) { throw 'Portable SDK cannot run on this Windows PC.' }
    if ((& $dotnet --version).Trim() -ne $sdkVersion) { throw 'Unexpected portable SDK version.' }

    # Copy current tracked bytes. No checkout, obj/bin output or project mutation
    # occurs in the original repository; no remote Git operation is performed.
    Write-Host 'Copying current Git-tracked source to a disposable restore snapshot...'
    $tracked = @(& git -C $RepositoryPath -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'Cannot enumerate tracked repository files. Git for Windows is required.' }
    $commit = (& git -C $RepositoryPath rev-parse HEAD).Trim()
    foreach ($relative in $tracked) {
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        if ($relative -match '(^|/)(obj|bin|\.git)/') { continue }
        $source = [IO.Path]::GetFullPath([IO.Path]::Combine($RepositoryPath, $relative))
        if (-not $source.StartsWith($RepositoryPath.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Tracked path escapes source repository.' }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        if (((Get-Item -LiteralPath $source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Tracked reparse-point files are not allowed in a restore snapshot.' }
        $target = Join-Path $snapshot $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
        Copy-Item -LiteralPath $source -Destination $target
    }
    $localPackages = Join-Path $snapshot 'Drilling.Common/Packages'
    $externalConfig = Join-Path $work 'external.NuGet.Config'
    $escapedLocal = [Security.SecurityElement]::Escape($localPackages)
    [IO.File]::WriteAllText($externalConfig, ('<?xml version="1.0"?><configuration><packageSources><clear/><add key="repository" value="' + $escapedLocal + '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>'), (New-Object Text.UTF8Encoding $false))
    $solution = Join-Path $snapshot 'Drilling.sln'
    Write-Host 'Restoring project/reference packages on the external PC (no source upload)...'
    & $dotnet restore $solution --packages $restorePackages --configfile $externalConfig --disable-parallel --disable-build-servers -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'External restore failed. No completed toolchain was installed.' }

    $feed = Join-Path $stage 'feed'
    [void][IO.Directory]::CreateDirectory($feed)
    $packageFiles = @(Get-ChildItem -LiteralPath $restorePackages -Filter '*.nupkg' -File -Recurse)
    $packageFiles += @(Get-ChildItem -LiteralPath $localPackages -Filter '*.nupkg' -File -Recurse)
    foreach ($package in $packageFiles) {
        $target = Join-Path $feed $package.Name
        if (Test-Path -LiteralPath $target) {
            if ((Get-BundleSha256 $target) -ne (Get-BundleSha256 $package.FullName)) { throw 'Different package contents share the same feed filename.' }
        } else { Copy-Item -LiteralPath $package.FullName -Destination $target }
    }
    if (@(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File).Count -eq 0) { throw 'Offline feed is empty.' }
    # A relative feed stays valid after USB transfer. Verification uses a second,
    # empty package cache and exactly this config, with all external sources removed.
    $offlineConfig = Join-Path $stage 'NuGet.Config'
    [IO.File]::WriteAllText($offlineConfig, '<?xml version="1.0"?><configuration><packageSources><clear/><add key="a3-offline-feed" value="feed"/></packageSources><config><add key="globalPackagesFolder" value="packages"/></config></configuration>', (New-Object Text.UTF8Encoding $false))
    $env:NUGET_PACKAGES = $offlinePackages
    $env:NUGET_CERT_REVOCATION_MODE = 'offline'
    Write-Host 'Verifying restore again with an empty cache and local feed only...'
    & $dotnet restore $solution --packages $offlinePackages --configfile $offlineConfig --force --force-evaluate --disable-parallel --disable-build-servers -p:NuGetAudit=false --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Local-feed-only restore failed. Toolchain is incomplete.' }
    foreach ($assets in @(Get-ChildItem -LiteralPath $snapshot -Filter 'project.assets.json' -File -Recurse)) {
        $json = Get-Content -LiteralPath $assets.FullName -Raw | ConvertFrom-Json
        foreach ($packageSource in $json.project.restore.sources.PSObject.Properties) {
            if ($packageSource.Name -match '^https?://' -or [IO.Path]::GetFullPath($packageSource.Name) -ne [IO.Path]::GetFullPath($feed)) { throw 'Offline verification unexpectedly used a non-local source.' }
        }
    }
    & $dotnet build-server shutdown
    if ($LASTEXITCODE -ne 0) { throw 'Cannot stop temporary SDK build servers before export.' }
    $notice = @"
A3 portable offline build toolchain
.NET SDK $sdkVersion win-x64: $sdkUrl
Official SHA-512: $sdkSha512
Preserve the SDK LICENSE.txt and ThirdPartyNotices.txt files and package licenses.
The feed contains original NuGet .nupkg archives, including repository-vendored packages.
The administrator must retain the applicable package and vendor redistribution rights.
Verification: portable SDK --info and empty-cache local-feed-only Drilling.sln restore.
This does not fix source compiler errors, supply missing proprietary SDK DLLs,
or validate machine/equipment operation. No build-success claim is implied.
"@
    [IO.File]::WriteAllText((Join-Path $stage 'A3-TOOLCHAIN-NOTICES.txt'), $notice, (New-Object Text.UTF8Encoding $false))
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object { [ordered]@{ path = $_.FullName.Substring($prefix.Length).Replace('\','/'); size = $_.Length; sha256 = Get-BundleSha256 $_.FullName } })
    $manifest = [ordered]@{ schemaVersion = 1; kind = 'a3-offline-toolchain'; createdUtc = [DateTime]::UtcNow.ToString('o'); sdkVersion = $sdkVersion; sdkUrl = $sdkUrl; sdkSha512 = $sdkSha512; metadataUrl = $metadataUrl; sourceRepository = 'A3_LD_Process_SW_IPS'; sourceCommit = $commit; localOnlyRestoreVerified = $true; packageCount = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File).Count; files = $files }
    [IO.File]::WriteAllText((Join-Path $stage 'toolchain-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
    $destination = Join-Path $OutputDirectory 'toolchain'
    if (Test-Path -LiteralPath $destination) { Move-Item -LiteralPath $destination -Destination (Join-Path $OutputDirectory ('toolchain-previous-' + [Guid]::NewGuid().ToString('N').Substring(0,8))) }
    Move-Item -LiteralPath $stage -Destination $destination
    Write-Host ('READY - copy this whole folder next to A3.OfflineStudio.exe: ' + $destination) -ForegroundColor Green
    Write-Host ('Offline feed packages: ' + $manifest.packageCount)
} finally {
    $safeRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $resolved = [IO.Path]::GetFullPath($work)
    if ($resolved.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('A3TC-')) { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue }
}
