#Requires -Version 7
<#
.SYNOPSIS
    Builds the add-in and lays it out as a HexIDE add-in package: artifacts/addins/rdcore/, with its addin.json.

.DESCRIPTION
    The package is unsigned: HexIDE loads it only in developer mode with "load unsigned add-ins" on, until HexIDE issues
    RDCore's publisher record and the package is signed with HexIDE's packer. Its manifest lists every file with its SHA-256,
    which HexIDE needs even then: an add-in's own assemblies load only from the files its manifest lists.

    Copy the folder into the addins folder beside HexIDE's executable to install it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Destination = (Join-Path $PSScriptRoot '..' 'artifacts' 'addins' 'rdcore')
)

$ErrorActionPreference = 'Stop'
$repository = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repository 'RDCore.HexIDE' 'RDCore.HexIDE.csproj'

$published = Join-Path ([IO.Path]::GetTempPath()) ("rdcore-addin-" + [guid]::NewGuid().ToString('N'))
dotnet publish $project --configuration $Configuration --output $published --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

try {
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    # The add-in ships its own assemblies and their resources; what HexIDE shares with it, it doesn't.
    Get-ChildItem $published -Recurse -File |
        Where-Object { $_.Extension -in '.dll', '.pdb', '.xml' -and $_.Name -notlike 'HexIDE*' -and $_.Name -notlike 'Avalonia*' } |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($published, $_.FullName)
            $target = Join-Path $Destination $relative
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target
        }

    $assembly = Join-Path $Destination 'RDCore.HexIDE.dll'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion.Split('+')[0]
    $files = Get-ChildItem $Destination -Recurse -File | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path   = [IO.Path]::GetRelativePath($Destination, $_.FullName).Replace('\', '/')
            sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{
        schema       = 2
        id           = 'rdcore'
        title        = 'RDCore for HexIDE'
        description  = "RDCore's language server and runtime for RDCore projects (.rdproj)."
        version      = $version
        author       = '9562-7303 Québec inc.'
        publisherId  = 'rubberduck-vba'
        entry        = 'RDCore.HexIDE.dll'
        capabilities = @('languageServer.contribute')
        files        = @($files)
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Destination 'addin.json') -Encoding utf8NoBOM
    Write-Host "RDCore for HexIDE $version is in $Destination ($($files.Count) files, unsigned)."
}
finally {
    Remove-Item $published -Recurse -Force -ErrorAction SilentlyContinue
}
