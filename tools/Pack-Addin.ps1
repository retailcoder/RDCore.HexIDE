#Requires -Version 7
<#
.SYNOPSIS
    Builds the add-in and lays it out as a HexIDE add-in package: artifacts/addins/rdcore/, with its addin.json.

.DESCRIPTION
    The package is unsigned: HexIDE loads it only in developer mode with "load unsigned add-ins" on, until HexIDE issues
    RDCore's publisher record and the package is signed with HexIDE's packer. Its manifest lists every file with its SHA-256,
    which HexIDE needs even then: an add-in's own assemblies load only from the files its manifest lists.

    The package carries the duck, assets/ducky.png, as its logo: the manifest's logoPath names it, and its files list it.
    HexIDE shows a signed package's logo on the add-in's options page; an unsigned package shows HexIDE's placeholder.

    Copy the folder into the addins folder beside HexIDE's executable to install it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Destination = (Join-Path $PSScriptRoot '..' 'artifacts' 'addins' 'rdcore'),
    # A folder the add-in is already published to (dotnet publish's output), packed as it is: the add-in is not built.
    [string] $Published
)

$ErrorActionPreference = 'Stop'
$repository = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repository 'RDCore.HexIDE' 'RDCore.HexIDE.csproj'
$logo = Join-Path $repository 'assets' 'ducky.png'
$logoPath = 'ducky.png'

$publishes = -not $Published
if ($publishes) {
    $Published = Join-Path ([IO.Path]::GetTempPath()) ("rdcore-addin-" + [guid]::NewGuid().ToString('N'))
    dotnet publish $project --configuration $Configuration --output $Published --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

try {
    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    # The add-in ships RDCore's own assemblies and their resources, and nothing else: HexIDE shares its own, Avalonia and
    # everything Avalonia depends on included, and anything new a dependency drags in is left out rather than shipped unseen.
    $shipped = Get-ChildItem $Published -Recurse -File |
        Where-Object { $_.Extension -in '.dll', '.pdb', '.xml' -and $_.Name -like 'RDCore.*' }
    $left = Get-ChildItem $Published -Recurse -File -Filter *.dll | Where-Object { $_.Name -notlike 'RDCore.*' }
    if ($left) { Write-Host "Not shipped, as HexIDE has its own: $(($left | ForEach-Object Name | Sort-Object -Unique) -join ', ')" }
    $shipped |
        ForEach-Object {
            $relative = [IO.Path]::GetRelativePath($Published, $_.FullName)
            $target = Join-Path $Destination $relative
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            Copy-Item $_.FullName $target
        }
    Copy-Item $logo (Join-Path $Destination $logoPath)

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
        logoPath     = $logoPath
        files        = @($files)
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $Destination 'addin.json') -Encoding utf8NoBOM
    Write-Host "RDCore for HexIDE $version is in $Destination ($($files.Count) files, unsigned)."
}
finally {
    if ($publishes) { Remove-Item $Published -Recurse -Force -ErrorAction SilentlyContinue }
}
