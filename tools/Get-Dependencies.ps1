#Requires -Version 7
<#
.SYNOPSIS
    Fetches the sources the add-in builds against into deps/, each at the commit tools/dependencies.json pins.

.DESCRIPTION
    Each dependency is a shallow clone of one commit, checked out sparsely: only the paths dependencies.json lists. A
    dependency already at its pinned commit is left as it is, so running this again is cheap. Nothing outside deps/ is
    written.
#>
[CmdletBinding()]
param(
    # Where the dependencies go; the build looks in deps/ beside the repository root.
    [string] $Destination = (Join-Path $PSScriptRoot '..' 'deps')
)

$ErrorActionPreference = 'Stop'
$pins = Get-Content (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json -AsHashtable

function Invoke-Git {
    git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

foreach ($name in $pins.Keys | Where-Object { $_ -ne '$comment' }) {
    $pin = $pins[$name]
    $folder = Join-Path $Destination $name
    if (Test-Path (Join-Path $folder '.git')) {
        $current = git -C $folder rev-parse HEAD 2>$null
        if ($current -eq $pin.commit) {
            Write-Host "$name is at $($pin.commit.Substring(0, 8))."
            continue
        }
    }
    else {
        New-Item -ItemType Directory -Force $folder | Out-Null
        Invoke-Git -C $folder init --quiet
        Invoke-Git -C $folder remote add origin $pin.repository
    }

    # A pin may move to another repository (a fork, before its branch merges), so the remote follows it.
    Invoke-Git -C $folder remote set-url origin $pin.repository
    Write-Host "Fetching $name at $($pin.commit.Substring(0, 8)) from $($pin.repository)..."
    Invoke-Git -C $folder sparse-checkout set --no-cone @($pin.paths)
    Invoke-Git -C $folder fetch --quiet --depth 1 origin $pin.commit
    Invoke-Git -C $folder checkout --quiet --force FETCH_HEAD
}
