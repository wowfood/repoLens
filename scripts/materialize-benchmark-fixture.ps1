#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Materializes the benchmark fixture repository into a working directory.

.DESCRIPTION
    Copies RepoLens.Tests/Fixtures/BenchmarkRepo into the destination, stripping the trailing
    ".fixture" extension that keeps the source inert inside this repository, then initializes a git
    repository and restores it so Roslyn compiles it cleanly.

    Emits the destination path on success.
#>
[CmdletBinding()]
param(
    [string]$Destination,
    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot '..' 'RepoLens.Tests' 'Fixtures' 'BenchmarkRepo'
if (-not (Test-Path $source)) {
    throw "Benchmark fixture source was not found at $source."
}

if (-not $Destination) {
    $Destination = Join-Path ([System.IO.Path]::GetTempPath()) "repolens-benchmark-$([guid]::NewGuid().ToString('N'))"
}

if (Test-Path $Destination) {
    Remove-Item -Recurse -Force $Destination
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

$sourceRoot = (Resolve-Path $source).Path
foreach ($file in Get-ChildItem -Recurse -File -Path $sourceRoot) {
    $relative = $file.FullName.Substring($sourceRoot.Length).TrimStart([char]'\', [char]'/')
    if ($relative -eq 'README.md' -or $relative -eq 'corpus.json') {
        continue
    }

    if ($relative.EndsWith('.fixture')) {
        $relative = $relative.Substring(0, $relative.Length - '.fixture'.Length)
    }

    $target = Join-Path $Destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}

Push-Location $Destination
try {
    & git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'git init failed in the materialized fixture.' }

    # Committing matters for reproducibility, not tidiness: an uncommitted tree makes every file a
    # change, which feeds the evidence ranker a different changed-file set and moves the benchmark's
    # numbers. The commit metadata is pinned for the same reason.
    & git -c core.autocrlf=false -c core.safecrlf=false add -A
    if ($LASTEXITCODE -ne 0) { throw 'git add failed in the materialized fixture.' }
    & git -c user.name='RepoLens Benchmark' -c user.email='benchmark@repolens.invalid' `
        -c commit.gpgsign=false commit --quiet --message 'Benchmark fixture'
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed in the materialized fixture.' }

    if (-not $SkipRestore) {
        # Every project explicitly, including the one only reached as a ProjectReference. Restoring
        # it transitively works, but leaves its assets file being written while the referencing
        # restore is still running, and a fixture whose references half-resolve produces analysis
        # gaps that inflate every bundle in the corpus by about a hundred tokens. That reads as a
        # retrieval regression and is not one.
        foreach ($project in @(
            'src/Inventory/Inventory.csproj',
            'src/Ordering/Ordering.csproj',
            'tests/Ordering.Tests/Ordering.Tests.csproj')) {
            & dotnet restore $project | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $project in the materialized fixture." }
        }
    }
}
finally {
    Pop-Location
}

Write-Output $Destination
