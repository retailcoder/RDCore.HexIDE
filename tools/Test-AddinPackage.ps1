#Requires -Version 7
<#
.SYNOPSIS
    Checks a signed add-in package before it is published, as HexIDE will when it loads it; fails on the first thing wrong.

.DESCRIPTION
    The package is the one the publisher record in -Trust signs for, and nothing else:
    - addin.json's id is RDCoreServer.Id, read from the package's own RDCore.HexIDE.dll, and its publisherId is the record's;
    - its version is -Version, when given;
    - publisher.json/.sig and intermediate.json/.sig are the trust folder's bytes, and root.pub is its hexide-root.pub;
    - the chain verifies: root.pub signs intermediate.json, the intermediate's key signs publisher.json, and the publisher's
      key signs addin.json;
    - every file addin.json lists has the SHA-256 it lists, and the package holds no file but those, the manifest, its
      signature and the record's.
    It prints the publisher key's fingerprint, which the README publishes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Package,
    [string] $Trust = (Join-Path $PSScriptRoot '..' 'trust'),
    [string] $Version
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'AddinPackage.psm1') -Force

function Join-PackFile([string] $name) { Join-Path $Package $name }

$manifest = Get-Content (Join-PackFile 'addin.json') -Raw | ConvertFrom-Json
$id = Get-PackAddinId (Join-PackFile $manifest.entry)
if ($manifest.id -cne $id) { throw "addin.json's id is '$($manifest.id)'; the add-in's RDCoreServer.Id is '$id'." }
$publisherId = Get-PackPublisherId $Trust
if ($manifest.publisherId -cne $publisherId) {
    throw "addin.json's publisherId is '$($manifest.publisherId)'; the publisher record's is '$publisherId'."
}
if ($Version -and $manifest.version -cne $Version) { throw "addin.json's version is '$($manifest.version)', not '$Version'." }

$record = @{ 'publisher.json' = 'publisher.json'; 'publisher.sig' = 'publisher.sig'; 'intermediate.json' = 'intermediate.json'
    'intermediate.sig' = 'intermediate.sig'; 'root.pub' = 'hexide-root.pub' }
foreach ($name in $record.Keys) {
    $packed = [IO.File]::ReadAllBytes((Join-PackFile $name))
    $trusted = [IO.File]::ReadAllBytes((Join-Path $Trust $record[$name]))
    if (-not [Linq.Enumerable]::SequenceEqual($packed, $trusted)) { throw "The package's $name isn't the trust folder's $($record[$name])." }
}

$root = Get-Content (Join-PackFile 'root.pub') -Raw
$intermediate = Get-Content (Join-PackFile 'intermediate.json') -Raw | ConvertFrom-Json
$publisher = Get-Content (Join-PackFile 'publisher.json') -Raw | ConvertFrom-Json
if ($publisher.intermediateId -cne $intermediate.intermediateId) {
    throw "publisher.json names the intermediate '$($publisher.intermediateId)'; intermediate.json is '$($intermediate.intermediateId)'."
}
if (-not (Test-PackSignature $root (Join-PackFile 'intermediate.json') (Join-PackFile 'intermediate.sig'))) {
    throw 'intermediate.sig does not verify against root.pub.'
}
if (-not (Test-PackSignature $intermediate.publicKey (Join-PackFile 'publisher.json') (Join-PackFile 'publisher.sig'))) {
    throw "publisher.sig does not verify against the intermediate's key."
}
if (-not (Test-PackSignature $publisher.publicKey (Join-PackFile 'addin.json') (Join-PackFile 'addin.sig'))) {
    throw "addin.sig does not verify against the publisher's key."
}

$expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in @('addin.json', 'addin.sig') + @($record.Keys)) { [void]$expected.Add($name) }
foreach ($file in $manifest.files) {
    $hash = (Get-FileHash (Join-PackFile $file.path) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $file.sha256) { throw "$($file.path) doesn't have the SHA-256 addin.json lists." }
    [void]$expected.Add($file.path)
}
$present = Get-ChildItem $Package -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($Package, $_.FullName).Replace('\', '/') }
$extra = @($present | Where-Object { -not $expected.Contains($_) })
if ($extra) { throw "The package holds files addin.json doesn't list: $($extra -join ', ')." }

Write-Host "Package $($manifest.id) $($manifest.version) verifies: publisher $publisherId, $($manifest.files.Count) files."
Write-Host "Root key fingerprint:      $(Get-PackKeyFingerprint $root)"
Write-Host "Publisher key fingerprint: $(Get-PackKeyFingerprint $publisher.publicKey)"
