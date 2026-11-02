# PowerShell 5.1 compatible. Imported by the preparation script and packaging checks.
Set-StrictMode -Version 2.0

function Get-BundleSha256 {
    param([Parameter(Mandatory=$true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Test-BundleFile {
    param([string]$Path, [long]$Size, [string]$Sha256, [ValidateSet('SHA256', 'SHA512')][string]$HashAlgorithm = 'SHA256')
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Size) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm $HashAlgorithm).Hash.ToLowerInvariant() -eq $Sha256.ToLowerInvariant()
}

function Get-CustomBundleModel {
    param([Parameter(Mandatory=$true)][string]$Path, [string]$Name = 'Custom local GGUF')
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { throw 'Custom model file was not found.' }
    $file = Get-Item -LiteralPath $absolute -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Custom model cannot be a link or reparse point.' }
    if ($file.Extension -ine '.gguf') { throw 'Custom model must be a .gguf file.' }
    if ($file.Name -match '-\d{5}-of-\d{5}\.gguf$') { throw 'Select a complete single-file GGUF, not a split GGUF shard.' }
    if ($file.Length -lt 24 -or $file.Length -gt 18GB) { throw 'Custom GGUF must be at least 24 bytes and at most 18 GiB.' }
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name.Length -gt 120 -or $Name -match '[\x00-\x1f\x7f:\\/]') { throw 'Use a short model display name without paths or control characters.' }
    $stream = [IO.File]::OpenRead($absolute)
    try {
        $magic = New-Object byte[] 4
        if ($stream.Read($magic, 0, 4) -ne 4 -or [Text.Encoding]::ASCII.GetString($magic) -ne 'GGUF') { throw 'Custom model does not have the GGUF file signature.' }
    } finally { $stream.Dispose() }
    return [ordered]@{ name = $Name.Trim(); file = 'model.gguf'; localPath = $absolute; size = $file.Length; sha256 = Get-BundleSha256 $absolute }
}

function Copy-CustomBundleLicense {
    param([Parameter(Mandatory=$true)][string]$Path, [Parameter(Mandatory=$true)][string]$Destination)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Custom model license file was not found.' }
    $file = Get-Item -LiteralPath $Path -Force
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -gt 2MB) { throw 'Custom model license must be a regular text file of at most 2 MiB.' }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes -contains 0) { throw 'Custom model license must be UTF-8/plain text, not a binary or UTF-16 file.' }
    [IO.File]::WriteAllBytes($Destination, $bytes)
}

function Receive-VerifiedBundleFile {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$Destination,
        [Parameter(Mandatory=$true)][long]$Size,
        [Parameter(Mandatory=$true)][string]$Sha256,
        [int]$MaximumAttempts = 3,
        [ValidateSet('SHA256', 'SHA512')][string]$HashAlgorithm = 'SHA256'
    )
    if ($Url -notmatch '^https://') { throw 'Only HTTPS package sources are allowed.' }
    $digestLength = if ($HashAlgorithm -eq 'SHA512') { 128 } else { 64 }
    if ($Sha256 -notmatch ('^[0-9a-fA-F]{' + $digestLength + '}$')) { throw ('Invalid expected ' + $HashAlgorithm + ' digest.') }
    if (Test-BundleFile $Destination $Size $Sha256 $HashAlgorithm) {
        Write-Host ('Verified cache: ' + [IO.Path]::GetFileName($Destination))
        return
    }
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($Destination))
    [void][IO.Directory]::CreateDirectory($parent)
    $partial = $Destination + '.partial'
    # Files with a completed but wrong digest are never resumed or accepted.
    if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $response = $null; $inputStream = $null; $outputStream = $null
        try {
            $offset = 0L
            if (Test-Path -LiteralPath $partial -PathType Leaf) {
                $offset = (Get-Item -LiteralPath $partial).Length
                if ($offset -eq $Size -and (Test-BundleFile $partial $Size $Sha256 $HashAlgorithm)) {
                    Move-Item -LiteralPath $partial -Destination $Destination
                    return
                }
                if ($offset -ge $Size) { Remove-Item -LiteralPath $partial -Force; $offset = 0L }
            }
            Write-Host ('Downloading {0} (attempt {1}/{2}, resume {3:N1} MiB)' -f [IO.Path]::GetFileName($Destination), $attempt, $MaximumAttempts, ($offset / 1MB))
            $request = [Net.HttpWebRequest]::Create($Url)
            $request.Method = 'GET'
            $request.UserAgent = 'A3-OfflineStudio-Bundle/1'
            $request.Timeout = 60000
            $request.ReadWriteTimeout = 60000
            $request.AllowAutoRedirect = $true
            if ($offset -gt 0) { $request.AddRange($offset) }
            $response = $request.GetResponse()
            if ([int]$response.StatusCode -eq 206) {
                $range = [string]$response.Headers['Content-Range']
                if ($range -notmatch ('^bytes ' + $offset + '-')) { throw 'Server returned an unexpected byte range.' }
            } elseif ([int]$response.StatusCode -eq 200) {
                # Some CDNs ignore Range. Start over instead of appending a duplicate file.
                $offset = 0L
            } else { throw ('Unexpected HTTP response: ' + [int]$response.StatusCode) }
            $mode = if ($offset -gt 0) { [IO.FileMode]::Append } else { [IO.FileMode]::Create }
            $outputStream = [IO.File]::Open($partial, $mode, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $inputStream = $response.GetResponseStream()
            $buffer = New-Object byte[] (1024 * 1024)
            $received = $offset; $lastReport = [DateTime]::UtcNow
            while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outputStream.Write($buffer, 0, $read); $received += $read
                if ($received -gt $Size) { throw 'Download exceeds the expected file size.' }
                if (([DateTime]::UtcNow - $lastReport).TotalSeconds -ge 10) {
                    Write-Host ('  {0:N1}% ({1:N1} / {2:N1} MiB)' -f ($received * 100.0 / $Size), ($received / 1MB), ($Size / 1MB))
                    $lastReport = [DateTime]::UtcNow
                }
            }
            $outputStream.Dispose(); $outputStream = $null
            $inputStream.Dispose(); $inputStream = $null
            $response.Dispose(); $response = $null
            if (-not (Test-BundleFile $partial $Size $Sha256 $HashAlgorithm)) {
                if ((Get-Item -LiteralPath $partial).Length -eq $Size) { Remove-Item -LiteralPath $partial -Force }
                throw ('Downloaded file size or ' + $HashAlgorithm + ' does not match the pinned source.')
            }
            Move-Item -LiteralPath $partial -Destination $Destination
            return
        } catch {
            Write-Warning $_.Exception.Message
            if ($attempt -eq $MaximumAttempts) {
                throw ('Download failed. Re-run the same command to resume the cached partial file. Source: ' + $Url)
            }
            Start-Sleep -Seconds ([Math]::Min(2 * $attempt, 6))
        } finally {
            if ($null -ne $outputStream) { $outputStream.Dispose() }
            if ($null -ne $inputStream) { $inputStream.Dispose() }
            if ($null -ne $response) { $response.Dispose() }
        }
    }
}

function Expand-BundleRuntime {
    param([string]$ArchivePath, [string]$Destination)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    [void][IO.Directory]::CreateDirectory($targetRoot)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $total = 0L
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('/', '\')
            if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains(':') -or $relative.Split('\') -contains '..') { throw 'Unsafe runtime archive path.' }
            if (-not $seen.Add($relative)) { throw 'Duplicate runtime archive path.' }
            if (($entry.ExternalAttributes -band 0x400) -ne 0 -or (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Runtime archive links are not allowed.' }
            $absolute = [IO.Path]::GetFullPath([IO.Path]::Combine($targetRoot, $relative))
            if (-not $absolute.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime archive escapes its directory.' }
            $total += $entry.Length
            if ($entry.Length -gt 512MB -or $total -gt 1GB) { throw 'Runtime archive exceeds extraction limits.' }
            if ($relative.EndsWith('\')) { [void][IO.Directory]::CreateDirectory($absolute); continue }
            [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute))
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $absolute, $false)
        }
    } finally { $archive.Dispose() }
    if (-not (Test-Path -LiteralPath (Join-Path $Destination 'llama-server.exe') -PathType Leaf)) {
        throw 'Pinned runtime package does not contain llama-server.exe at its expected root.'
    }
}

function Write-BundleManifest {
    param([string]$Directory, [string]$ModelName, [string]$ModelSha256, [object[]]$Sources)
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $entries = @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Bundle input contains a reparse point.' }
        $relative = $_.FullName.Substring($root.Length).Replace('\', '/')
        if ($relative -ne 'manifest.json') {
            [ordered]@{ path = $relative; size = $_.Length; sha256 = Get-BundleSha256 $_.FullName }
        }
    })
    $manifest = [ordered]@{
        schemaVersion = 1
        bundleId = 'a3-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
        createdUtc = [DateTime]::UtcNow.ToString('o')
        model = [ordered]@{ name = $ModelName; file = 'models/model.gguf'; sha256 = $ModelSha256; contextSize = 4096 }
        runtime = [ordered]@{ file = 'runtime/llama-server.exe' }
        files = $entries
        sources = @($Sources)
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding $false))
    return $manifest
}

function Compress-OfflineBundle {
    param([string]$Directory, [string]$ArchivePath)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $ArchivePath) { throw 'Output archive already exists; choose a new destination.' }
    # .NET Framework's CreateFromDirectory writes backslashes on older Windows.
    # Create entries explicitly so the archive and manifest share portable paths.
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::Open($ArchivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName)) {
            $relative = $file.FullName.Substring($root.Length).Replace('\', '/')
            # GGUF is already quantized: storing avoids long compression with little benefit.
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::NoCompression)
        }
    } finally { $archive.Dispose() }
    $digest = Get-BundleSha256 $ArchivePath
    [IO.File]::WriteAllText(($ArchivePath + '.sha256'), ($digest + '  ' + [IO.Path]::GetFileName($ArchivePath) + [Environment]::NewLine), (New-Object Text.UTF8Encoding $false))
    return $digest
}
