#Requires -Version 7
<#
.SYNOPSIS
    Builds the add-in and lays it out as a HexIDE add-in package: artifacts/addins/rdcore/, with its addin.json.

.DESCRIPTION
    The package is unsigned: HexIDE loads it only in developer mode with "load unsigned add-ins" on, until HexIDE issues
    RDCore's publisher record and the package is signed with HexIDE's packer. Its manifest lists every file with its SHA-256,
    which HexIDE needs even then: an add-in's own assemblies load only from the files its manifest lists.

    The package carries every assembly the add-in runs with, except the ones HexIDE shares with its add-ins: an assembly the
    package doesn't carry resolves to HexIDE's own. HexIDE's are HexIDE.Core's and those of the packages the add-in takes no
    runtime assets of (Avalonia, StreamJsonRpc and what StreamJsonRpc pulls in), with everything those depend on, as the
    add-in's restore resolved them (its project.assets.json). The pack fails when an assembly the package carries references
    one that is in neither the package, HexIDE's set nor .NET's shared framework, or one older than it was built against.

    The package carries the duck, assets/ducky.png, as its logo: the manifest's logoPath names it, and its files list it.
    HexIDE shows a signed package's logo on the add-in's options page; an unsigned package shows HexIDE's placeholder.

    Copy the folder into the addins folder beside HexIDE's executable to install it.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Destination = (Join-Path $PSScriptRoot '..' 'artifacts' 'addins' 'rdcore'),
    # A folder the add-in is already published to (dotnet publish's output, or its build output), packed as it is: the add-in
    # is not built.
    [string] $Published,
    # The add-in's restore, which says which packages are HexIDE's: the project's own, unless the add-in was restored elsewhere.
    [string] $Assets = (Join-Path $PSScriptRoot '..' 'RDCore.HexIDE' 'obj' 'project.assets.json')
)

$ErrorActionPreference = 'Stop'
$repository = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repository 'RDCore.HexIDE' 'RDCore.HexIDE.csproj'
$logo = Join-Path $repository 'assets' 'ducky.png'
$logoPath = 'ducky.png'
# The add-in contract: HexIDE loads its own copy.
$contract = 'HexIDE.Core'

# The assembly a file holds, with the assemblies it references; null for a file that isn't a .NET assembly (a native library).
function Read-PackAssembly([string] $path) {
    $stream = [IO.File]::OpenRead($path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        if (-not $pe.HasMetadata) { return $null }
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        if (-not $reader.IsAssembly) { return $null }
        $definition = $reader.GetAssemblyDefinition()
        [pscustomobject]@{
            Name       = $reader.GetString($definition.Name)
            Version    = $definition.Version
            References = @(foreach ($handle in $reader.AssemblyReferences) {
                    $reference = $reader.GetAssemblyReference($handle)
                    [pscustomobject]@{ Name = $reader.GetString($reference.Name); Version = $reference.Version }
                })
        }
    }
    catch [BadImageFormatException] { return $null }
    finally {
        $pe.Dispose()
        $stream.Dispose()
    }
}

# The names of the assemblies HexIDE brings: its contract's, and those of the packages the add-in leaves to HexIDE, with
# everything they depend on.
function Get-PackHexIdeAssemblies([string] $assetsPath) {
    if (-not (Test-Path $assetsPath)) { throw "No restore to read HexIDE's assemblies from: $assetsPath. Restore the add-in first." }
    $restore = Get-Content $assetsPath -Raw | ConvertFrom-Json -AsHashtable
    $framework = @($restore.project.frameworks.Keys)[0]
    $graph = @{}
    foreach ($key in $restore.targets[$framework].Keys) { $graph[$key.Split('/')[0]] = $restore.targets[$framework][$key] }
    if (-not $graph.ContainsKey($contract)) { throw "The restore in $assetsPath has no $contract reference." }

    # A package the add-in takes no runtime assets of is HexIDE's: its include lists the assets the add-in takes.
    $direct = $restore.project.frameworks[$framework].dependencies
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($contract)
    foreach ($name in $direct.Keys) {
        $include = $direct[$name].include
        if ($include -and $include -notmatch '\b(Runtime|All)\b') { $pending.Enqueue($name) }
    }

    $packages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assemblies = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($pending.Count) {
        $name = $pending.Dequeue()
        if (-not $packages.Add($name) -or -not $graph.ContainsKey($name)) { continue }
        $library = $graph[$name]
        # A package whose runtime assets are excluded has a placeholder as its runtime: its compile assets name its assemblies.
        foreach ($asset in @($library.compile.Keys) + @($library.runtime.Keys)) {
            if ($asset -and [IO.Path]::GetExtension($asset) -eq '.dll') { [void]$assemblies.Add([IO.Path]::GetFileNameWithoutExtension($asset)) }
        }
        foreach ($dependency in @($library.dependencies.Keys)) { if ($dependency) { $pending.Enqueue($dependency) } }
    }
    [pscustomobject]@{ Framework = $framework; Assemblies = $assemblies }
}

# The assemblies, by name, of the .NET shared framework the add-in targets, with their versions: none of them ships.
function Get-PackFrameworkAssemblies([string] $framework) {
    $major = [int]($framework -replace '^net(\d+)\..*$', '$1')
    $listed = '^Microsoft\.NETCore\.App (?<full>(?<version>(?<major>\d+)\.\d+\.\d+)\S*) \[(?<path>.+)\]$'
    $runtime = dotnet --list-runtimes |
        ForEach-Object { if ($_ -match $listed) { $Matches.Clone() } } |
        Where-Object { [int]$_.major -eq $major } |
        Sort-Object { [version]$_.version } -Descending |
        Select-Object -First 1
    if (-not $runtime) { throw "No .NET $major runtime to check the add-in's references against: dotnet --list-runtimes lists none." }
    $assemblies = @{}
    foreach ($file in Get-ChildItem (Join-Path $runtime.path $runtime.full) -Filter *.dll) {
        if ($assembly = Read-PackAssembly $file.FullName) { $assemblies[$assembly.Name] = $assembly.Version }
    }
    $assemblies
}

$publishes = -not $Published
if ($publishes) {
    $Published = Join-Path ([IO.Path]::GetTempPath()) ("rdcore-addin-" + [guid]::NewGuid().ToString('N'))
    dotnet publish $project --configuration $Configuration --output $Published --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

try {
    $hexIde = Get-PackHexIdeAssemblies $Assets
    $framework = Get-PackFrameworkAssemblies $hexIde.Framework

    # Every assembly ships but HexIDE's, satellites and a runtime's own copies included; only RDCore's carry symbols and docs.
    $shipped = [Collections.Generic.List[IO.FileInfo]]::new()
    $left = [Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in Get-ChildItem $Published -Recurse -File) {
        $name = [IO.Path]::GetFileNameWithoutExtension($file.Name)
        if ($file.Extension -eq '.dll') {
            if ($hexIde.Assemblies.Contains(($name -replace '\.resources$', ''))) { [void]$left.Add($file.Name) } else { $shipped.Add($file) }
        }
        elseif ($file.Extension -in '.pdb', '.xml' -and $name -like 'RDCore.*') { $shipped.Add($file) }
    }
    if ($left.Count) { Write-Host "Not shipped, as HexIDE has its own: $($left -join ', ')" }

    # Each reference of a shipped assembly resolves: to one the package carries, at its version or later, to HexIDE's, or to .NET's.
    $assemblies = @($shipped | Where-Object { $_.Extension -eq '.dll' -and $_.Name -notlike '*.resources.dll' } |
            ForEach-Object { Read-PackAssembly $_.FullName } | Where-Object { $_ })
    $carried = @{}
    foreach ($assembly in $assemblies) {
        if (-not $carried.ContainsKey($assembly.Name) -or $carried[$assembly.Name] -lt $assembly.Version) {
            $carried[$assembly.Name] = $assembly.Version
        }
    }
    $unresolved = foreach ($assembly in $assemblies) {
        foreach ($reference in $assembly.References) {
            $version = if ($carried.ContainsKey($reference.Name)) { $carried[$reference.Name] }
            elseif ($framework.ContainsKey($reference.Name)) { $framework[$reference.Name] }
            if ($null -ne $version) {
                if ($version -lt $reference.Version) {
                    "$($assembly.Name) needs $($reference.Name) $($reference.Version), and only $version is there."
                }
            }
            elseif (-not $hexIde.Assemblies.Contains($reference.Name)) {
                "$($assembly.Name) needs $($reference.Name) $($reference.Version), which is in neither the package, HexIDE nor .NET."
            }
        }
    }
    if ($unresolved) {
        $unresolved | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
        throw "The add-in can't be packed: an assembly it needs isn't there."
    }

    if (Test-Path $Destination) { Remove-Item $Destination -Recurse -Force }
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($file in $shipped) {
        $target = Join-Path $Destination ([IO.Path]::GetRelativePath($Published, $file.FullName))
        New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
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
