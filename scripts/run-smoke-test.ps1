[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$OutputDirectory,

    [switch]$NoBuild,

    [switch]$KeepFixture
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Utf8File {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Content
    )

    $parent = [IO.Path]::GetDirectoryName($Path)
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }

    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Format-Command {
    param(
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $formatted = foreach ($argument in $Arguments) {
        if ($argument -match '\s|"') {
            '"' + $argument.Replace('"', '\"') + '"'
        }
        else {
            $argument
        }
    }

    return (($Executable) + ' ' + ($formatted -join ' ')).Trim()
}

function Invoke-External {
    param(
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Failed to start: $(Format-Command $Executable $Arguments)"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stopwatch.Stop()

        return [pscustomobject]@{
            Command      = Format-Command $Executable $Arguments
            ExitCode     = $process.ExitCode
            StandardOut  = $stdoutTask.GetAwaiter().GetResult()
            StandardErr  = $stderrTask.GetAwaiter().GetResult()
            DurationMs   = $stopwatch.ElapsedMilliseconds
        }
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$Executable,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [int]$ExpectedExitCode = 0
    )

    $result = Invoke-External $Executable $Arguments $WorkingDirectory
    if ($result.ExitCode -ne $ExpectedExitCode) {
        throw @"
Command returned $($result.ExitCode); expected $ExpectedExitCode.
$($result.Command)
$($result.StandardOut)
$($result.StandardErr)
"@
    }

    return $result
}

function Invoke-DevContext {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $allArguments = @($script:DevContextDll)
    $allArguments += $Arguments
    return Invoke-External 'dotnet' $allArguments $WorkingDirectory
}

function Assert-Contains {
    param(
        [Parameter(Mandatory)] [string]$Value,
        [Parameter(Mandatory)] [string]$Expected,
        [Parameter(Mandatory)] [string]$Label
    )

    if (-not $Value.Contains($Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label did not contain expected text: $Expected"
    }
}

function Add-TranscriptSection {
    param(
        [Parameter(Mandatory)] [Text.StringBuilder]$Builder,
        [Parameter(Mandatory)] [string]$Title,
        [Parameter(Mandatory)]$Result
    )

    [void]$Builder.AppendLine("===== $Title =====")
    [void]$Builder.AppendLine("command: $($Result.Command)")
    [void]$Builder.AppendLine("exit-code: $($Result.ExitCode)")
    [void]$Builder.AppendLine("duration-ms: $($Result.DurationMs)")
    [void]$Builder.AppendLine($Result.StandardOut.TrimEnd())
    if (-not [string]::IsNullOrWhiteSpace($Result.StandardErr)) {
        [void]$Builder.AppendLine('stderr:')
        [void]$Builder.AppendLine($Result.StandardErr.TrimEnd())
    }
    [void]$Builder.AppendLine()
}

function Measure-ContextText {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [string]$Text
    )

    $lineCount = if ($Text.Length -eq 0) {
        0
    }
    else {
        [Text.RegularExpressions.Regex]::Matches($Text, "`r`n|`n|`r").Count + 1
    }

    return [ordered]@{
        name                    = $Name
        characters              = $Text.Length
        utf8Bytes               = [Text.Encoding]::UTF8.GetByteCount($Text)
        lines                   = $lineCount
        approximateTextTokens   = [int][Math]::Ceiling($Text.Length / 4.0)
    }
}

function Remove-SmokeFixtureSafely {
    param([Parameter(Mandatory)] [string]$Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $leaf = [IO.Path]::GetFileName($resolvedPath)
    if (-not $resolvedPath.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -or
        -not $leaf.StartsWith('dev-context-smoke-', [StringComparison]::Ordinal)) {
        throw "Refusing to remove unexpected smoke-test path: $resolvedPath"
    }

    if ([IO.Directory]::Exists($resolvedPath)) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = [IO.Path]::Combine(
        $repositoryRoot,
        'artifacts',
        'smoke',
        (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location).Path $OutputDirectory
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$script:DevContextDll = [IO.Path]::Combine(
    $repositoryRoot,
    'RepoLens',
    'bin',
    $Configuration,
    'net10.0',
    'dev-context.dll')
$solutionPath = Join-Path $repositoryRoot 'RepoLens.sln'
if (-not $NoBuild) {
    Write-Host 'Building dev-context and its tests...'
    $toolBuild = Invoke-Checked 'dotnet' @('build', $solutionPath, '--nologo') $repositoryRoot
    Write-Utf8File (Join-Path $OutputDirectory 'tool-build.txt') (
        $toolBuild.StandardOut + $toolBuild.StandardErr)
}
if (-not [IO.File]::Exists($script:DevContextDll)) {
    throw "dev-context was not found at $script:DevContextDll. Run without -NoBuild first."
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('dev-context-smoke-' + [Guid]::NewGuid().ToString('N'))
$fixtureRetained = $false
$success = $false

$calculatorProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
'@

$testProject = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsTestProject>true</IsTestProject>
    <UseVSTest>true</UseVSTest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MSTest" Version="4.0.2" />
    <ProjectReference Include="../../src/Calculator/Calculator.csproj" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Microsoft.VisualStudio.TestTools.UnitTesting" />
  </ItemGroup>
</Project>
'@

$calculatorSource = @'
namespace SmokeSample;

public static class Calculator
{
    public static int Add(int left, int right) => left + right;
}
'@

$passingTestSource = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace SmokeSample.Tests;

[TestClass]
public sealed class CalculatorTests
{
    [TestMethod]
    public void Add_returns_sum()
    {
        Assert.AreEqual(4, Calculator.Add(2, 2));
    }
}
'@

$failingTestSource = $passingTestSource.Replace(
    'Assert.AreEqual(4, Calculator.Add(2, 2));',
    'Assert.AreEqual(5, Calculator.Add(2, 2));')
$badlyFormattedSource = $calculatorSource.Replace(
    'public static int Add(int left, int right) => left + right;',
    'public static int Add(int left,int right)=>left+right;')

try {
    Write-Host "Creating isolated fixture: $fixtureRoot"
    [IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
    $calculatorDirectory = [IO.Path]::Combine($fixtureRoot, 'src', 'Calculator')
    $testDirectory = [IO.Path]::Combine($fixtureRoot, 'tests', 'Calculator.Tests')
    [IO.Directory]::CreateDirectory($calculatorDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($testDirectory) | Out-Null

    Write-Utf8File (Join-Path $fixtureRoot '.gitignore') @'
bin/
obj/
TestResults/
.dev-context/
'@
    Write-Utf8File (Join-Path $calculatorDirectory 'Calculator.csproj') $calculatorProject
    Write-Utf8File (Join-Path $calculatorDirectory 'Calculator.cs') $calculatorSource
    Write-Utf8File (Join-Path $testDirectory 'Calculator.Tests.csproj') $testProject
    $testFile = Join-Path $testDirectory 'CalculatorTests.cs'
    Write-Utf8File $testFile $passingTestSource

    [void](Invoke-Checked 'dotnet' @('new', 'sln', '--name', 'SmokeSample', '--format', 'sln') $fixtureRoot)
    [void](Invoke-Checked 'dotnet' @(
        'sln', 'SmokeSample.sln', 'add',
        'src/Calculator/Calculator.csproj',
        'tests/Calculator.Tests/Calculator.Tests.csproj') $fixtureRoot)
    [void](Invoke-Checked 'git' @('init', '--quiet') $fixtureRoot)
    [void](Invoke-Checked 'git' @('add', '.') $fixtureRoot)
    [void](Invoke-Checked 'git' @(
        '-c', 'user.name=dev-context smoke test',
        '-c', 'user.email=smoke-test@example.invalid',
        'commit', '--quiet', '-m', 'smoke baseline') $fixtureRoot)
    $baseCommit = (Invoke-Checked 'git' @('rev-parse', 'HEAD') $fixtureRoot).StandardOut.Trim()
    $baseBranch = (Invoke-Checked 'git' @('branch', '--show-current') $fixtureRoot).StandardOut.Trim()

    Write-Host 'Creating baseline...'
    $baseline = Invoke-DevContext @('baseline') $fixtureRoot
    if ($baseline.ExitCode -ne 0) {
        throw "Baseline failed:`n$($baseline.StandardOut)`n$($baseline.StandardErr)"
    }
    Write-Utf8File (Join-Path $OutputDirectory 'baseline.txt') $baseline.StandardOut

    $duplicateBaseline = Invoke-DevContext @('baseline') $fixtureRoot
    if ($duplicateBaseline.ExitCode -ne 2) {
        throw "Duplicate baseline returned $($duplicateBaseline.ExitCode); expected 2."
    }
    Assert-Contains $duplicateBaseline.StandardErr 'A baseline already exists' 'duplicate baseline error'

    $statusText = Invoke-DevContext @('status') $fixtureRoot
    if ($statusText.ExitCode -ne 0) {
        throw "Status failed:`n$($statusText.StandardErr)"
    }
    $statusJson = Invoke-DevContext @('status', '--format', 'json') $fixtureRoot
    if ($statusJson.ExitCode -ne 0) {
        throw "JSON status failed:`n$($statusJson.StandardErr)"
    }
    $statusObject = $statusJson.StandardOut | ConvertFrom-Json -Depth 50
    if ($statusObject.repository.projects.Count -ne 2 -or $statusObject.tests.failed -ne 0) {
        throw 'Baseline status did not report two projects and zero failing tests.'
    }
    Write-Utf8File (Join-Path $OutputDirectory 'status.json') $statusJson.StandardOut

    $rawContext = [Text.StringBuilder]::new()
    Add-TranscriptSection $rawContext 'SDK and runtime discovery' (
        Invoke-External 'dotnet' @('--info') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Git baseline discovery' (
        Invoke-External 'git' @('status', '--short', '--branch') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Solution discovery' (
        Invoke-External 'dotnet' @('sln', 'SmokeSample.sln', 'list') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Full baseline build output' (
        Invoke-External 'dotnet' @('build', 'SmokeSample.sln', '--nologo', '--verbosity', 'normal') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Full baseline test output' (
        Invoke-External 'dotnet' @(
            'test', 'SmokeSample.sln', '--no-build', '--nologo',
            '--logger', 'console;verbosity=normal') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Evaluated production project metadata' (
        Invoke-External 'dotnet' @(
            'msbuild', 'src/Calculator/Calculator.csproj', '-nologo',
            '-getProperty:TargetFramework,Nullable,LangVersion,TreatWarningsAsErrors',
            '-getItem:ProjectReference,PackageReference,Compile') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Evaluated test project metadata' (
        Invoke-External 'dotnet' @(
            'msbuild', 'tests/Calculator.Tests/Calculator.Tests.csproj', '-nologo',
            '-getProperty:TargetFramework,Nullable,LangVersion,IsTestProject',
            '-getItem:ProjectReference,PackageReference,Compile') $fixtureRoot)

    $configPath = [IO.Path]::Combine($fixtureRoot, '.dev-context', 'config.json')
    $configObject = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json -Depth 50
    $configObject.analysis.dotnetFormat = $true
    Write-Utf8File $configPath ($configObject | ConvertTo-Json -Depth 50)

    Write-Host 'Introducing deterministic test and formatting regressions...'
    Write-Utf8File $testFile $failingTestSource
    Write-Utf8File (Join-Path $calculatorDirectory 'Calculator.cs') $badlyFormattedSource
    Add-TranscriptSection $rawContext 'Changed files and diff' (
        Invoke-External 'git' @('diff', '--no-ext-diff', '--unified=3') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Full current build output' (
        Invoke-External 'dotnet' @('build', 'SmokeSample.sln', '--nologo', '--verbosity', 'normal') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Full current test output' (
        Invoke-External 'dotnet' @(
            'test', 'SmokeSample.sln', '--no-build', '--nologo',
            '--logger', 'console;verbosity=detailed') $fixtureRoot)
    Add-TranscriptSection $rawContext 'Formatting verification output' (
        Invoke-External 'dotnet' @(
            'format', 'SmokeSample.sln', '--verify-no-changes', '--no-restore',
            '--verbosity', 'normal') $fixtureRoot)

    $affected = Invoke-DevContext @('affected') $fixtureRoot
    if ($affected.ExitCode -ne 0) {
        throw "Affected analysis failed:`n$($affected.StandardErr)"
    }
    Assert-Contains $affected.StandardOut 'Calculator.Tests.csproj' 'affected output'
    Assert-Contains $affected.StandardOut 'Add_returns_sum' 'affected output'
    Write-Utf8File (Join-Path $OutputDirectory 'affected.txt') $affected.StandardOut

    $evidenceQueryArguments = @(
        'query',
        'change Calculator Add and its focused tests',
        '--max-tokens', '1200',
        '--max-results', '8',
        '--graph-depth', '1')
    $evidenceQuery = Invoke-DevContext $evidenceQueryArguments $fixtureRoot
    if ($evidenceQuery.ExitCode -ne 0) {
        throw "Evidence query failed:`n$($evidenceQuery.StandardOut)`n$($evidenceQuery.StandardErr)"
    }
    Assert-Contains $evidenceQuery.StandardOut 'src/Calculator/Calculator.cs' 'evidence query'
    Assert-Contains $evidenceQuery.StandardOut 'tests/Calculator.Tests/CalculatorTests.cs' 'evidence query'
    Write-Utf8File (Join-Path $OutputDirectory 'evidence-query.txt') $evidenceQuery.StandardOut

    $evidenceQueryJson = Invoke-DevContext ($evidenceQueryArguments + @('--format', 'json')) $fixtureRoot
    if ($evidenceQueryJson.ExitCode -ne 0) {
        throw "JSON evidence query failed:`n$($evidenceQueryJson.StandardErr)"
    }
    $evidenceObject = $evidenceQueryJson.StandardOut | ConvertFrom-Json -Depth 50
    if ($evidenceObject.approximateTokens -gt 1200) {
        throw "Evidence query exceeded its token budget: $($evidenceObject.approximateTokens) > 1200."
    }
    if ($evidenceObject.blocks.Count -gt 8) {
        throw "Evidence query exceeded its result bound: $($evidenceObject.blocks.Count) > 8."
    }
    Write-Utf8File (Join-Path $OutputDirectory 'evidence-query.json') $evidenceQueryJson.StandardOut

    # refs answers a structural question exactly, where query ranks by relevance. It is the command
    # the skills tell an agent to prefer, so it belongs in the smoke path.
    $references = Invoke-DevContext @(
        'refs', 'Add', '--relation', 'callers', '--format', 'json') $fixtureRoot
    if ($references.ExitCode -ne 0) {
        throw "Reference query failed:`n$($references.StandardOut)`n$($references.StandardErr)"
    }
    $referenceObject = $references.StandardOut | ConvertFrom-Json -Depth 50
    if ($referenceObject.resolvedSymbol.name -ne 'Add') {
        throw "refs resolved '$($referenceObject.resolvedSymbol.name)'; expected 'Add'."
    }
    if (-not ($referenceObject.matches.source.name -contains 'Add_returns_sum')) {
        throw 'refs did not report the test method as a caller of Add.'
    }
    if ($referenceObject.shouldAbstain) {
        throw 'refs abstained on a repository whose compilation records are complete.'
    }
    Write-Utf8File (Join-Path $OutputDirectory 'refs.json') $references.StandardOut

    $regression = Invoke-DevContext @('verify') $fixtureRoot
    if ($regression.ExitCode -ne 1) {
        throw "Regression verification returned $($regression.ExitCode); expected 1.`n$($regression.StandardOut)`n$($regression.StandardErr)"
    }
    Assert-Contains $regression.StandardOut 'Regressions: yes' 'regression verification'
    Assert-Contains $regression.StandardOut 'Add_returns_sum' 'regression verification'
    Assert-Contains $regression.StandardOut 'affected-first (targeted/incomplete)' 'regression verification'
    Assert-Contains $regression.StandardOut 'dotnet-format' 'regression verification'
    Write-Utf8File (Join-Path $OutputDirectory 'verify-regression.txt') $regression.StandardOut

    $currentManifest = Get-Content -Raw -LiteralPath (
        [IO.Path]::Combine($fixtureRoot, '.dev-context', 'current', 'manifest.json')) | ConvertFrom-Json -Depth 50
    if ($currentManifest.repositoryIndexCacheHit -ne $true) {
        throw 'Verification did not reuse the repository graph cache populated by affected analysis.'
    }

    # Commit the regression. Everything above compared a dirty working tree against the baseline;
    # this is the commit-aware half of the delta, which nothing in the smoke path exercised. A
    # regression that has been committed is still a regression, and its provenance changes.
    Write-Host 'Committing the regression to exercise the commit-aware delta...'
    # On a feature branch, so that the ref-based review below has a real merge base to find rather
    # than comparing a branch against itself.
    [void](Invoke-Checked 'git' @('checkout', '--quiet', '-b', 'feature/smoke-review') $fixtureRoot)
    [void](Invoke-Checked 'git' @('add', '--all') $fixtureRoot)
    [void](Invoke-Checked 'git' @(
        '-c', 'user.name=dev-context smoke test',
        '-c', 'user.email=smoke-test@example.invalid',
        'commit', '--quiet', '-m', 'committed regression') $fixtureRoot)
    $committedCommit = (Invoke-Checked 'git' @('rev-parse', 'HEAD') $fixtureRoot).StandardOut.Trim()

    $committedAffected = Invoke-DevContext @('affected', '--format', 'json') $fixtureRoot
    if ($committedAffected.ExitCode -ne 0) {
        throw "Affected analysis after commit failed:`n$($committedAffected.StandardErr)"
    }
    $committedAffectedObject = $committedAffected.StandardOut | ConvertFrom-Json -Depth 50
    if (-not ($committedAffectedObject.changes.provenance -contains 'Committed')) {
        throw 'Affected analysis did not report Committed provenance after the regression was committed.'
    }
    if (-not ($committedAffectedObject.changedFiles -contains 'tests/Calculator.Tests/CalculatorTests.cs')) {
        throw 'Affected analysis lost the committed test change.'
    }
    Write-Utf8File (Join-Path $OutputDirectory 'affected-committed.json') $committedAffected.StandardOut

    $committedVerify = Invoke-DevContext @('verify') $fixtureRoot
    if ($committedVerify.ExitCode -ne 1) {
        throw "Verification after commit returned $($committedVerify.ExitCode); expected 1.`n$($committedVerify.StandardOut)"
    }
    Assert-Contains $committedVerify.StandardOut 'Regressions: yes' 'committed verification'
    Write-Utf8File (Join-Path $OutputDirectory 'verify-committed.txt') $committedVerify.StandardOut

    # Stateless review against a git ref. It must not read or write baseline state, which is what
    # makes it usable in CI on a pull request.
    $review = Invoke-DevContext @('verify', '--against', $baseBranch, '--format', 'json') $fixtureRoot
    if ($review.ExitCode -notin @(0, 1)) {
        throw "Reference review returned $($review.ExitCode); expected 0 or 1.`n$($review.StandardErr)"
    }
    $reviewObject = $review.StandardOut | ConvertFrom-Json -Depth 50
    if ($reviewObject.baseCommit -ne $baseCommit -or $reviewObject.headCommit -ne $committedCommit) {
        throw "Reference review compared $($reviewObject.baseCommit)..$($reviewObject.headCommit); expected $baseCommit..$committedCommit."
    }
    if (-not ($reviewObject.changes.provenance -contains 'Committed')) {
        throw 'Reference review did not report committed provenance.'
    }
    Write-Utf8File (Join-Path $OutputDirectory 'verify-against-ref.json') $review.StandardOut

    $compactContext = [Text.StringBuilder]::new()
    Add-TranscriptSection $compactContext 'Stored baseline status' $statusText
    Add-TranscriptSection $compactContext 'Affected analysis' $affected
    Add-TranscriptSection $compactContext 'Delta verification' $regression

    $rawText = $rawContext.ToString()
    $compactText = $compactContext.ToString()
    Write-Utf8File (Join-Path $OutputDirectory 'raw-context.txt') $rawText
    Write-Utf8File (Join-Path $OutputDirectory 'compact-context.txt') $compactText

    $rawMetrics = Measure-ContextText 'raw-tooling-context' $rawText
    $compactMetrics = Measure-ContextText 'dev-context-summary' $compactText
    $evidenceMetrics = Measure-ContextText 'token-bounded-evidence-query' $evidenceQuery.StandardOut
    $reductionPercent = if ($rawMetrics.approximateTextTokens -eq 0) {
        0
    }
    else {
        [Math]::Round(
            (1.0 - ($compactMetrics.approximateTextTokens / $rawMetrics.approximateTextTokens)) * 100.0,
            2)
    }
    $tokenProxy = [ordered]@{
        schemaVersion = 1
        method = 'Approximate text tokens are ceil(characters / 4). This measures context volume, not total model usage.'
        raw = $rawMetrics
        compact = $compactMetrics
        evidenceQuery = $evidenceMetrics
        approximateReductionPercent = $reductionPercent
    }
    Write-Utf8File (Join-Path $OutputDirectory 'token-proxy.json') (
        $tokenProxy | ConvertTo-Json -Depth 10)

    Write-Host 'Restoring the passing test and checking the delta clears...'
    Write-Utf8File $testFile $passingTestSource
    Write-Utf8File (Join-Path $calculatorDirectory 'Calculator.cs') $calculatorSource
    $restored = Invoke-DevContext @('verify') $fixtureRoot
    if ($restored.ExitCode -ne 0) {
        throw "Restored verification failed:`n$($restored.StandardOut)`n$($restored.StandardErr)"
    }
    Assert-Contains $restored.StandardOut 'Regressions: no' 'restored verification'
    Write-Utf8File (Join-Path $OutputDirectory 'verify-restored.txt') $restored.StandardOut

    $cleanup = Invoke-DevContext @('clean', '--format', 'json') $fixtureRoot
    if ($cleanup.ExitCode -ne 0) {
        throw "Disabled cleanup check failed:`n$($cleanup.StandardErr)"
    }
    $cleanupObject = $cleanup.StandardOut | ConvertFrom-Json
    if ($cleanupObject.state -ne 'Skipped') {
        throw "Cleanup state was $($cleanupObject.state); expected Skipped."
    }
    Write-Utf8File (Join-Path $OutputDirectory 'cleanup.json') $cleanup.StandardOut

    $summary = [ordered]@{
        schemaVersion = 1
        success = $true
        fixture = $fixtureRoot
        fixtureRetained = [bool]$KeepFixture
        results = [ordered]@{
            baselineExitCode = $baseline.ExitCode
            duplicateBaselineExitCode = $duplicateBaseline.ExitCode
            affectedExitCode = $affected.ExitCode
            evidenceQueryExitCode = $evidenceQuery.ExitCode
            referenceQueryExitCode = $references.ExitCode
            regressionVerifyExitCode = $regression.ExitCode
            committedVerifyExitCode = $committedVerify.ExitCode
            referenceReviewExitCode = $review.ExitCode
            restoredVerifyExitCode = $restored.ExitCode
            cleanupState = $cleanupObject.state
        }
        tokenProxy = $tokenProxy
    }
    Write-Utf8File (Join-Path $OutputDirectory 'summary.json') (
        $summary | ConvertTo-Json -Depth 12)

    $success = $true
    Write-Host ''
    Write-Host 'Smoke test passed.' -ForegroundColor Green
    Write-Host "Artifacts: $OutputDirectory"
    Write-Host "Raw context proxy:     $($rawMetrics.approximateTextTokens) tokens"
    Write-Host "Compact context proxy: $($compactMetrics.approximateTextTokens) tokens"
    Write-Host "Evidence query proxy:  $($evidenceMetrics.approximateTextTokens) tokens (budget: 1200)"
    Write-Host "Approximate reduction: $reductionPercent%"
}
finally {
    if ($KeepFixture) {
        $fixtureRetained = $true
        Write-Host "Fixture retained: $fixtureRoot"
    }
    elseif ([IO.Directory]::Exists($fixtureRoot)) {
        Remove-SmokeFixtureSafely $fixtureRoot
    }

    if (-not $success) {
        Write-Host "Smoke test failed. Partial artifacts: $OutputDirectory" -ForegroundColor Red
    }
}
