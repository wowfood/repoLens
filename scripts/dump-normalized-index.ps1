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

$root = (Resolve-Path $RepositoryRoot).Path
$rootPattern = [regex]::Escape(($root -replace '\\', '/'))

function ConvertTo-NormalText {
    param([string]$Value)
    $normalized = $Value -replace '\\', '/'
    $normalized = [regex]::Replace($normalized, $rootPattern, '<repo>', 'IgnoreCase')
    # A rooted path outside the repository is machine state: the home directory and the resolved SDK
    # patch version both appear in NuGet reference-assembly paths. Only the leaf is kept, so the
    # reference keeps its identity and its position in the list while the location is discarded.
    # Collapsing the whole path to one token instead would make every reference compare equal and
    # hide a genuinely reordered or truncated reference set.
    # Spaces are allowed inside the match ("C:/Program Files/dotnet/sdk/10.0.204/...") because
    # stopping at whitespace would leave the SDK version in the tail, which is exactly the part that
    # differs between runner images.
    $normalized = [regex]::Replace($normalized, '(?i)\b[a-z]:/[^"]*', { param($match) '<abs>/' + [System.IO.Path]::GetFileName($match.Value) })
    $normalized = [regex]::Replace($normalized, '(?<![\w<])/(?:Users|home|private|tmp|var|opt)/[^"]*', { param($match) '<abs>/' + [System.IO.Path]::GetFileName($match.Value) })
    return $normalized
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
