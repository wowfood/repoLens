#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the retrieval benchmark against the synthetic fixture repository.

.DESCRIPTION
    The self-referential corpus asserts on RepoLens's own file paths, so its ground truth moves with
    every refactor of RepoLens. This corpus runs against a fixture repository that only changes when
    a case is deliberately rewritten, which is what makes retrieval quality comparable between
    commits.

    Optionally emits a normalized dump of the fixture's indexes for the cross-platform determinism
    comparison.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [string]$NormalizedIndexPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$corpus = Join-Path $repositoryRoot 'RepoLens.Tests' 'Fixtures' 'BenchmarkRepo' 'corpus.json'

if (-not $NoBuild) {
    & dotnet build (Join-Path $repositoryRoot 'RepoLens' 'RepoLens.csproj') --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Building the CLI failed.' }
}

$cli = Join-Path $repositoryRoot 'RepoLens' 'bin' $Configuration 'net10.0' 'dev-context.dll'
if (-not (Test-Path $cli)) {
    throw "dev-context was not found at $cli. Run without -NoBuild first."
}

$fixture = & (Join-Path $PSScriptRoot 'materialize-benchmark-fixture.ps1')
Write-Host "Fixture repository: $fixture"

try {
    Push-Location $fixture
    try {
        & dotnet $cli init | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'dev-context init failed in the fixture repository.' }

        & dotnet $cli benchmark $corpus
        $benchmarkExit = $LASTEXITCODE

        if ($NormalizedIndexPath) {
            # The benchmark builds the graph but does not persist indexes; a baseline does.
            & dotnet $cli baseline | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'dev-context baseline failed in the fixture repository.' }
        }
    }
    finally {
        Pop-Location
    }

    if ($NormalizedIndexPath) {
        $output = [System.IO.Path]::IsPathRooted($NormalizedIndexPath) `
            ? $NormalizedIndexPath `
            : (Join-Path $repositoryRoot $NormalizedIndexPath)
        & (Join-Path $PSScriptRoot 'dump-normalized-index.ps1') -RepositoryRoot $fixture -OutputPath $output | Out-Null
        Write-Host "Normalized index: $output"
    }

    if ($benchmarkExit -ne 0) {
        throw "The fixture retrieval benchmark failed with exit code $benchmarkExit."
    }
}
finally {
    Remove-Item -Recurse -Force $fixture -ErrorAction SilentlyContinue
}
