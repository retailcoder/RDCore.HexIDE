#Requires -Version 7
<#
.SYNOPSIS
    Publishes a built platform as a release in a development feed: a folder the add-in downloads from instead of GitHub.

.DESCRIPTION
    Zips the platform at -Platform as rdcore-<version>-<rid>.zip, writes its SHA256SUMS, and lists the release in the feed's
    copy of GitHub's releases API, as a published release would be. A release already in the feed with the same tag is
    replaced; any other is kept, so a feed can hold several versions.

    Point the add-in at the feed with "developmentFeed" in %LOCALAPPDATA%\RDCore\HexIDE\settings.json, and leave
    "platformRoot" out: that wins, and nothing is downloaded while it is set.

.EXAMPLE
    tools/New-DevelopmentFeed.ps1 -Platform C:\Dev\RDCore\artifacts\rdcore-dev -Version 0.1.0-dev.1 -Destination C:\Temp\feed
#>
[CmdletBinding()]
param(
    # A built platform root: rdcore.json at its top, as PlatformPublish.ps1 writes one.
    [Parameter(Mandatory)] [string] $Platform,
    # The release's version, without the leading v: 0.1.0-dev.1. A version with a suffix is listed as a prerelease.
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Destination,
    [string] $RuntimeIdentifier = 'win-x64',
    [string] $Repository = 'rubberduck-vba/RDCore'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Platform 'rdcore.json'))) { throw "$Platform has no rdcore.json: it isn't a built platform." }

$tag = "v$Version"
$zipName = "rdcore-$Version-$RuntimeIdentifier.zip"
$download = "$Repository/releases/download/$tag"
$assets = Join-Path $Destination 'github.com' $download
New-Item -ItemType Directory -Force $assets | Out-Null

$zip = Join-Path $assets $zipName
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $Platform '*') -DestinationPath $zip
$digest = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$sums = Join-Path $assets 'SHA256SUMS'
Set-Content $sums "$digest  $zipName" -Encoding ascii -NoNewline
Add-Content $sums "`n" -Encoding ascii -NoNewline

$api = Join-Path $Destination 'api.github.com' 'repos' $Repository
New-Item -ItemType Directory -Force $api | Out-Null
$list = Join-Path $api 'releases'
$releases = @(if (Test-Path $list) { Get-Content $list -Raw | ConvertFrom-Json | Where-Object { $_.tag_name -ne $tag } })
$releases += [ordered]@{
    tag_name   = $tag
    draft      = $false
    prerelease = $Version.Contains('-')
    assets     = @(
        [ordered]@{ name = $zipName; browser_download_url = "https://github.com/$download/$zipName"; size = (Get-Item $zip).Length }
        [ordered]@{ name = 'SHA256SUMS'; browser_download_url = "https://github.com/$download/SHA256SUMS"; size = (Get-Item $sums).Length }
    )
}
ConvertTo-Json -InputObject $releases -Depth 4 | Set-Content $list -Encoding utf8NoBOM
Write-Host "RDCore $Version is in the feed at $Destination ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, sha256 $($digest.Substring(0, 12)))."
