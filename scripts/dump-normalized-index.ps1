#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Emits a normalized, order-stable dump of a repository's RepoLens indexes.

.DESCRIPTION
    RepoLens claims deterministic output. The three-legged CI matrix builds everything on Windows,
    Linux, and macOS and then compares nothing, so that claim has never been tested across
    platforms. This script produces one canonical text file per run that can be compared byte for
    byte between operating systems.

    Normalization removes what legitimately differs between machines and keeps everything that must
    not:

    - Volatile keys are dropped. Timestamps and durations differ by definition. The repository input
      hash and the SDK version are dropped because CI resolves 8.0.x and 10.0.x to whatever patch
      each runner image ships, and a differing SDK patch is not a determinism failure.
    - Absolute paths are replaced and separators normalized, because the repository is checked out
      to a different directory on each runner.
    - Object keys are sorted, because JSON object order is an implementation detail of the
      serializer, whereas array order is a determinism signal and is deliberately preserved.

    Content hashes are kept. The repository's .gitattributes forces LF in every working copy, so a
    source file hashes identically on all three platforms; a difference there is a real defect.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RepositoryRoot,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$volatileKeys = @(
    'inputhash',
    'evaluationinputhash',
    'repositoryinputhash',
    'bundleid',
    'createdatutc',
    'capturedatutc',
    'generatedatutc',
    'timestamp',
    'durationms',
    'elapsedmilliseconds',
    'coldmilliseconds',
    'warmmilliseconds',
    'sdkversion',
    'machinename',
    'commit',
    'branch'
)

$root = ($RepositoryRoot | Resolve-Path).Path -replace '\\', '/'

# macOS resolves the temporary directory through a /private symlink, so the path recorded in the
# index carries a prefix the resolved root does not. Without the alias the substitution produced
# "/private<repo>/..." and every macOS path compared unequal.
$rootAliases = @($root, "/private$root") | Sort-Object -Property Length -Descending

function ConvertTo-NormalText {
    param([string]$Value)

    $normalized = $Value -replace '\\', '/'
    foreach ($alias in $rootAliases) {
        $normalized = [regex]::Replace($normalized, [regex]::Escape($alias), '<repo>', 'IgnoreCase')
    }

    # Any remaining rooted path is machine state — the SDK lives under Program Files on Windows,
    # /usr/share on Linux, and /usr/local on macOS — so only the leaf is kept. The leaf keeps the
    # reference's identity and its position in the list, where collapsing the whole path to a single
    # token would make every reference compare equal and hide a reordered or truncated set.
    #
    # Detected by shape rather than by a list of known prefixes. The earlier version matched
    # /Users, /home, /private, /tmp, /var and /opt, which meant Linux paths under /usr were not
    # normalized at all: that leg produced a dump 25% larger than the others and every comparison
    # against it failed for a reason that had nothing to do with RepoLens.
    if ($normalized -match '^(?:[A-Za-z]:)?/' ) {
        return '<abs>/' + [System.IO.Path]::GetFileName($normalized)
    }

    # Absolute paths embedded in a longer string, such as a compiler diagnostic.
    return [regex]::Replace(
        $normalized,
        '(?i)(?<![\w<])(?:[a-z]:)?/[^"]*',
        { param($match) '<abs>/' + [System.IO.Path]::GetFileName($match.Value) })
}

function ConvertTo-NormalNode {
    param($Node)

    if ($null -eq $Node) { return $null }

    if ($Node -is [string]) { return ConvertTo-NormalText $Node }

    if ($Node -is [System.Management.Automation.PSCustomObject]) {
        # The version of a targeting/runtime pack is chosen by whichever SDK patch the runner image
        # ships, so it differs between legs for reasons that have nothing to do with RepoLens. It is
        # dropped only on framework references; ordinary NuGet package versions must still match.
        $isFrameworkReference = $null -ne $Node.PSObject.Properties['frameworkReference']
        $ordered = [ordered]@{}
        foreach ($property in ($Node.PSObject.Properties | Sort-Object Name)) {
            $name = $property.Name.ToLowerInvariant()
            if ($volatileKeys -contains $name) { continue }
            if ($isFrameworkReference -and $name -eq 'packageversion') { continue }
            $ordered[$property.Name] = ConvertTo-NormalNode $property.Value
        }
        return $ordered
    }

    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
        return @($Node | ForEach-Object { ConvertTo-NormalNode $_ })
    }

    return $Node
}

$indexRoot = Join-Path $root '.dev-context' 'indexes'
if (-not (Test-Path $indexRoot)) {
    throw "No indexes were found at $indexRoot. Run 'dev-context baseline' first."
}

$builder = [System.Text.StringBuilder]::new()
foreach ($file in Get-ChildItem -Path $indexRoot -Filter *.json -File | Sort-Object Name) {
    [void]$builder.AppendLine("### $($file.Name)")
    $document = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    $normalized = ConvertTo-NormalNode $document
    [void]$builder.AppendLine(($normalized | ConvertTo-Json -Depth 64 -Compress))
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
# LF and no BOM, so the artifact compares byte for byte between runners.
[System.IO.File]::WriteAllText($OutputPath, ($builder.ToString() -replace "`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
Write-Output $OutputPath
