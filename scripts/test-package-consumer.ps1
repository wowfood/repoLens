[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '0.7.0',
    [string]$PackageDirectory = 'artifacts/packages'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedPackages = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackageDirectory))
$package = Join-Path $resolvedPackages "RepoLens.Api.$Version.nupkg"
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "Package not found: $package"
}

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "repolens-package-consumer-$([Guid]::NewGuid().ToString('N'))"))
$temporaryPrefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $fixtureRoot.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([IO.Path]::GetFileName($fixtureRoot)).StartsWith('repolens-package-consumer-', [StringComparison]::Ordinal)) {
    throw "Unsafe package-consumer fixture path: $fixtureRoot"
}

try {
    dotnet new console --name Consumer --output $fixtureRoot --framework net8.0 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the net8 package-consumer fixture.' }

    $project = Join-Path $fixtureRoot 'Consumer.csproj'
    dotnet add $project package RepoLens.Api --version $Version --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Could not add RepoLens.Api to the net8 consumer.' }
    $escapedPackages = [Security.SecurityElement]::Escape($resolvedPackages)
    $nugetConfig = Join-Path $fixtureRoot 'NuGet.Config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="RepoLens packages" value="$escapedPackages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8
    dotnet restore $project --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore RepoLens.Api into the net8 consumer.' }

    @'
using DevContext;

var contract = DevContextApi.Contract;
if (contract.CurrentSchemaVersion < contract.MinimumReadableSchemaVersion)
{
    throw new InvalidOperationException("Invalid RepoLens schema contract.");
}

Console.WriteLine($"RepoLens.Api {contract.PackageVersion}; schemas {contract.MinimumReadableSchemaVersion}-{contract.CurrentSchemaVersion}");
'@ | Set-Content -LiteralPath (Join-Path $fixtureRoot 'Program.cs') -Encoding utf8

    dotnet run --project $project --configuration $Configuration --framework net8.0
    if ($LASTEXITCODE -ne 0) { throw 'The net8 package consumer did not compile and run successfully.' }
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
