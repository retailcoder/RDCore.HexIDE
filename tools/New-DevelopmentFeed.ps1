#Requires -Version 7.4
<#
.SYNOPSIS
    Publishes a built platform as a release in a development feed: a folder the add-in downloads from instead of GitHub.

.DESCRIPTION
    Archives the platform at -Platform as RDCore's release workflow does, writes its SHA256SUMS, and lists the release in the
    feed's copy of GitHub's releases API, as a published release would be. A release already in the feed with the same tag is
    replaced; any other is kept, so a feed can hold several versions.

    The archive is rdcore-<version>-<rid>.zip for a Windows runtime identifier, and rdcore-<version>-<rid>.tar.gz for any
    other: a ustar archive whose executables (ELF or Mach-O files) are 0755 and every other file 0644, so the language server
    stays executable once the add-in unpacks it on Linux or macOS.

    Beside it is rdcore-<version>-<rid>.release.json, which SHA256SUMS lists too: the platform's own release.json, with the
    RDCore.SDK version it was built with as its "sdk". The add-in offers only a release built with an RDCore.SDK compatible
    with its own; an incompatible -SdkVersion writes a release that the add-in does not offer, to try that case.

    Point the add-in at the feed with "developmentFeed" in %LOCALAPPDATA%\RDCore\HexIDE\settings.json, and leave
    "platformRoot" out: that wins, and nothing is downloaded while it is set.

.EXAMPLE
    tools/New-DevelopmentFeed.ps1 -Platform C:\Dev\RDCore\artifacts\rdcore-dev -Version 0.1.0-dev.1 -Destination C:\Temp\feed -SdkVersion 0.1.1-alpha
#>
[CmdletBinding()]
param(
    # A built platform root: rdcore.json at its top, as PlatformPublish.ps1 writes one.
    [Parameter(Mandatory)] [string] $Platform,
    # The release's version, without the leading v: 0.1.0-dev.1. A version with a suffix is listed as a prerelease.
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Destination,
    # The runtime identifier the platform was built for; by default the "rid" its rdcore.json names, or win-x64 without one.
    [string] $RuntimeIdentifier,
    # The RDCore.SDK version the platform was built with (RDCoreSdkVersion, in RDCore's Directory.Build.props); by default the
    # "sdk" its release.json names. A platform without one, as a development build is, needs it.
    [string] $SdkVersion,
    [string] $Repository = 'rubberduck-vba/RDCore'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Formats.Tar
$manifestPath = Join-Path $Platform 'rdcore.json'
if (-not (Test-Path $manifestPath)) { throw "$Platform has no rdcore.json: it isn't a built platform." }
if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = (Get-Content $manifestPath -Raw | ConvertFrom-Json).rid
    if (-not $RuntimeIdentifier) { $RuntimeIdentifier = 'win-x64' }
}

$releaseJsonPath = Join-Path $Platform 'release.json'
$info = if (Test-Path $releaseJsonPath) { Get-Content $releaseJsonPath -Raw | ConvertFrom-Json -AsHashtable } else { [ordered]@{ schema = 1 } }
if ($SdkVersion) { $info.sdk = [ordered]@{ package = 'RDCore.SDK'; version = $SdkVersion } }
elseif (-not $info.sdk) {
    throw "$Platform has no release.json naming the RDCore.SDK it was built with: pass -SdkVersion, as RDCore's Directory.Build.props names it (RDCoreSdkVersion)."
}

$tag = "v$Version"
$isZip = $RuntimeIdentifier -like 'win*'
$archiveName = "rdcore-$Version-$RuntimeIdentifier.$(if ($isZip) { 'zip' } else { 'tar.gz' })"
$download = "$Repository/releases/download/$tag"
$assets = Join-Path $Destination 'github.com' $download
New-Item -ItemType Directory -Force $assets | Out-Null

# an ELF (Linux) or Mach-O (macOS) executable, by its first four bytes, as RDCore's New-ReleaseArchive.ps1 tells one
function Test-NativeExecutable([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $magic = [byte[]]::new(4)
        if ($stream.Read($magic, 0, 4) -ne 4) { return $false }
    }
    finally { $stream.Dispose() }
    ($magic[0] -eq 0x7F -and $magic[1] -eq 0x45 -and $magic[2] -eq 0x4C -and $magic[3] -eq 0x46) -or
        [BitConverter]::ToUInt32($magic, 0) -in 0xFEEDFACF, 0xCFFAEDFE, 0xCAFEBABE, 0xBEBAFECA
}

$archive = Join-Path $assets $archiveName
if (Test-Path $archive) { Remove-Item $archive }
if ($isZip) {
    Compress-Archive -Path (Join-Path $Platform '*') -DestinationPath $archive
}
else {
    $root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Platform).ProviderPath)
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    # each folder an entry of its own, before what is in it, so that an unpacked folder is 0755 whatever the umask
    $folders = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $files) {
        $parts = $path.Split('/')
        for ($i = 1; $i -lt $parts.Count; $i++) { [void]$folders.Add((($parts[0..($i - 1)]) -join '/') + '/') }
    }
    $entries = [string[]]@($files + @($folders))
    [Array]::Sort($entries, [StringComparer]::Ordinal)

    $executable = [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'
    $regular = [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead'
    $file = [IO.File]::Create($archive)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Ustar, $false)
    try {
        foreach ($name in $entries) {
            if ($name.EndsWith('/')) {
                $entry = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::Directory, $name)
                $entry.Mode = $executable
                $writer.WriteEntry($entry)
                continue
            }
            $source = Join-Path $root $name
            $entry = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $name)
            $entry.Mode = if (Test-NativeExecutable $source) { $executable } else { $regular }
            $stream = [IO.File]::OpenRead($source)
            try {
                $entry.DataStream = $stream
                $writer.WriteEntry($entry)
            }
            finally { $stream.Dispose() }
        }
    }
    finally { $writer.Dispose(); $gzip.Dispose(); $file.Dispose() }
}

$infoName = "rdcore-$Version-$RuntimeIdentifier.release.json"
$infoPath = Join-Path $assets $infoName
[IO.File]::WriteAllText($infoPath, (ConvertTo-Json -InputObject $info -Depth 10), [Text.UTF8Encoding]::new($false))

$digest = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$infoDigest = (Get-FileHash $infoPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $assets 'SHA256SUMS'
[IO.File]::WriteAllText($sums, "$digest  $archiveName`n$infoDigest  $infoName`n", [Text.Encoding]::ASCII)

$api = Join-Path $Destination 'api.github.com' 'repos' $Repository
New-Item -ItemType Directory -Force $api | Out-Null
$list = Join-Path $api 'releases'
$releases = @(if (Test-Path $list) { Get-Content $list -Raw | ConvertFrom-Json | Where-Object { $_.tag_name -ne $tag } })
$releases += [ordered]@{
    tag_name   = $tag
    draft      = $false
    prerelease = $Version.Contains('-')
    assets     = @(
        [ordered]@{ name = $archiveName; browser_download_url = "https://github.com/$download/$archiveName"; size = (Get-Item $archive).Length }
        [ordered]@{ name = 'SHA256SUMS'; browser_download_url = "https://github.com/$download/SHA256SUMS"; size = (Get-Item $sums).Length }
        [ordered]@{ name = $infoName; browser_download_url = "https://github.com/$download/$infoName"; size = (Get-Item $infoPath).Length }
    )
}
ConvertTo-Json -InputObject $releases -Depth 4 | Set-Content $list -Encoding utf8NoBOM
Write-Host "RDCore $Version ($RuntimeIdentifier, RDCore.SDK $($info.sdk.version)) is in the feed at $Destination ($([math]::Round((Get-Item $archive).Length / 1MB, 1)) MB, sha256 $($digest.Substring(0, 12)))."
