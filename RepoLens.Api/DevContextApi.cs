using System.Reflection;
using System.Text.Json.Nodes;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext;

/// <summary>
/// In-process access to the deterministic engine used by the dev-context CLI.
/// </summary>
public sealed class DevContextApi
{
    private readonly EngineServices _services;
    private bool _isNewConfiguration;
    private bool _configurationRequiresSave;

    private DevContextApi(
        string repositoryRoot,
        DevContextConfig configuration,
        bool isNewConfiguration,
        bool configurationRequiresSave,
        EngineServices services)
    {
        RepositoryRoot = repositoryRoot;
        Configuration = configuration;
        _isNewConfiguration = isNewConfiguration;
        _configurationRequiresSave = configurationRequiresSave;
        _services = services;
    }

    public string RepositoryRoot { get; }
    public DevContextConfig Configuration { get; }
    public bool BaselineExists => _services.Store.BaselineExists(RepositoryRoot);

    /// <summary>Names accepted by <see cref="GetJsonSchema"/>.</summary>
    public static IReadOnlyList<string> JsonSchemaDocuments => JsonSchemaService.Documents;

    /// <summary>
    /// Returns a draft 2020-12 JSON Schema for one persisted document, or the complete catalog.
    /// </summary>
    public static JsonObject GetJsonSchema(string? document = null) => JsonSchemaService.Build(document);

    /// <summary>Describes the stable package and persisted-schema compatibility contract.</summary>
    public static ApiContractInfo Contract { get; } = new()
    {
        PackageVersion = typeof(DevContextApi).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(DevContextApi).Assembly.GetName().Version?.ToString()
            ?? "unknown",
        CurrentSchemaVersion = SchemaVersions.Current,
        MinimumReadableSchemaVersion = SchemaVersions.MinimumReadable,
        SupportedTargetFrameworks = ["net8.0 or later"],
        RequiresTrustedRepository = true
    };

    public static async Task<DevContextApi> OpenAsync(
        string startPath,
        DevContextConfig? configuration = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);

        var repositoryRoot = RepositoryLocator.FindRoot(startPath);
        DevContextConfig resolvedConfiguration;
        bool isNewConfiguration;
        bool configurationRequiresSave;

        if (configuration is null)
        {
            (resolvedConfiguration, isNewConfiguration, configurationRequiresSave) = await ConfigLoader.LoadAsync(
                repositoryRoot,
                cancellationToken);
        }
        else
        {
            resolvedConfiguration = ConfigLoader.Migrate(configuration, out configurationRequiresSave);
            ConfigLoader.Validate(resolvedConfiguration);
            isNewConfiguration = false;
        }

        return new DevContextApi(
            repositoryRoot,
            resolvedConfiguration,
            isNewConfiguration,
            configurationRequiresSave,
            CreateServices());
    }

    public async Task<RepositoryAnalysisSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var bundle = await _services.Capture.CaptureAsync(
            RepositoryRoot,
            Configuration,
            null,
            CapturePurpose.Baseline,
            null,
            cancellationToken);

        return new RepositoryAnalysisSnapshot(
            bundle.Manifest,
            bundle.Git,
            bundle.Build,
            bundle.Tests,
            bundle.Analysis,
            bundle.Repository,
            bundle.Symbols,
            bundle.Dependencies,
            bundle.Affected);
    }

    /// <summary>
    /// Writes a validated default configuration when one does not already exist, or atomically
    /// saves a supported configuration migration. Current configuration is never overwritten.
    /// </summary>
    public async Task<InitializationReport> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var created = _isNewConfiguration;
        var migrated = _configurationRequiresSave;
        await _services.Store.SaveConfigIfNeededAsync(
            RepositoryRoot,
            Configuration,
            created || migrated,
            cancellationToken);
        _isNewConfiguration = false;
        _configurationRequiresSave = false;

        return new InitializationReport
        {
            RepositoryRoot = RepositoryRoot,
            ConfigPath = ContextPaths.Config(RepositoryRoot),
            Created = created,
            Migrated = migrated,
            Configuration = Configuration
        };
    }

    public Task<StatusReport> BaselineAsync(
        bool replace = false,
        CancellationToken cancellationToken = default) =>
        BaselineAsync(replace, null, cancellationToken);

    /// <summary>
    /// Captures a baseline whose Git diff base is the merge base of <paramref name="fromReference"/>
    /// and the current HEAD. Current health remains the baseline for later regression comparison.
    /// </summary>
    public async Task<StatusReport> BaselineAsync(
        bool replace,
        string? fromReference,
        CancellationToken cancellationToken = default)
    {
        if (BaselineExists && !replace)
        {
            throw new InvalidOperationException(
                "A baseline already exists. Set replace only when explicitly beginning a new logical task.");
        }

        await _services.Store.SaveConfigIfNeededAsync(
            RepositoryRoot,
            Configuration,
            _isNewConfiguration || _configurationRequiresSave,
            cancellationToken);

        var bundle = await _services.Capture.CaptureAsync(
            RepositoryRoot,
            Configuration,
            null,
            CapturePurpose.Baseline,
            null,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(fromReference))
        {
            var referenceChanges = await _services.Git.ChangesAgainstReferenceAsync(
                RepositoryRoot,
                fromReference,
                bundle.Git,
                cancellationToken);
            var baseCommit = referenceChanges.BaseCommit
                             ?? throw new InvalidOperationException("Git reference comparison produced no merge base.");
            bundle = bundle with
            {
                Git = bundle.Git with { HeadCommit = baseCommit },
                Manifest = bundle.Manifest with
                {
                    HeadCommit = baseCommit,
                    CapturedHeadCommit = bundle.Git.HeadCommit,
                    DiffBaseReference = fromReference
                }
            };
        }

        await _services.Store.SaveBaselineAsync(
            RepositoryRoot,
            bundle,
            Configuration,
            replace,
            cancellationToken);

        _isNewConfiguration = false;
        _configurationRequiresSave = false;
        return await StatusAsync(cancellationToken);
    }

    /// <summary>
    /// Runs stateless current-health verification and affected analysis against the merge base of
    /// a Git reference and HEAD. No immutable baseline is required or created.
    /// </summary>
    public async Task<ReferenceReviewReport> ReviewAsync(
        string reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        var bundle = await _services.Capture.CaptureAsync(
            RepositoryRoot,
            Configuration,
            null,
            CapturePurpose.ReferenceReview,
            null,
            cancellationToken,
            reference);
        var changes = bundle.Changes
                      ?? throw new InvalidOperationException("Reference review did not produce a Git change set.");
        var affected = bundle.Affected
                       ?? throw new InvalidOperationException("Reference review did not produce affected-code analysis.");
        var executionFailures = bundle.Build.State == ExecutionState.Unavailable
                                || bundle.Tests.State == ExecutionState.Unavailable
                                || bundle.Tests.State == ExecutionState.Failed && bundle.Tests.Outcomes.Count == 0;
        var providerFailures = Configuration.Analysis.DotnetFormat
                               && bundle.Analysis.DotnetFormat.State is ExecutionState.Failed or ExecutionState.Unavailable
                               || Configuration.Analysis.Qodana
                               && bundle.Analysis.Qodana.State is ExecutionState.Failed or ExecutionState.Unavailable;
        var diagnosticFailures = bundle.Analysis.Diagnostics.Any(diagnostic =>
            diagnostic.Severity == "error"
            || Configuration.Analysis.FailOnNewWarnings && diagnostic.Severity == "warning");
        var testFailures = bundle.Tests.Outcomes.Any(outcome => TestService.IsFailed(outcome.Outcome));
        var hasFailures = bundle.Build.State == ExecutionState.Failed
                          || bundle.Tests.State == ExecutionState.Failed
                          || providerFailures
                          || diagnosticFailures
                          || testFailures;

        return new ReferenceReviewReport
        {
            Reference = reference,
            BaseCommit = changes.BaseCommit!,
            HeadCommit = changes.HeadCommit!,
            VerifiedAtUtc = DateTimeOffset.UtcNow,
            ChangedFiles = changes.ChangedFiles,
            Changes = changes.Changes,
            Projects = affected.Projects,
            Symbols = affected.Symbols,
            ChangedSymbols = affected.ChangedSymbols,
            Tests = affected.Tests,
            TestCases = affected.TestCases,
            CurrentBuild = bundle.Build,
            CurrentTests = bundle.Tests,
            CurrentAnalysis = bundle.Analysis,
            HasFailures = hasFailures,
            HasExecutionFailures = executionFailures || providerFailures
        };
    }

    public Task<StatusReport> StatusAsync(CancellationToken cancellationToken = default)
    {
        _services.Store.EnsureBaseline(RepositoryRoot);
        return _services.Store.ReadStatusAsync(RepositoryRoot, cancellationToken);
    }

    public Task<VerificationReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        _services.Store.EnsureBaseline(RepositoryRoot);
        return _services.Verification.VerifyAsync(RepositoryRoot, Configuration, cancellationToken);
    }

    public Task<AffectedReport> AffectedAsync(CancellationToken cancellationToken = default)
    {
        _services.Store.EnsureBaseline(RepositoryRoot);
        return _services.Affected.CalculateAsync(RepositoryRoot, Configuration, cancellationToken);
    }

    public Task<CleanupReport> CleanAsync(CancellationToken cancellationToken = default)
    {
        _services.Store.EnsureBaseline(RepositoryRoot);
        return _services.Cleanup.RunAsync(RepositoryRoot, Configuration, cancellationToken);
    }

    /// <summary>
    /// Checks the local SDK, configuration, project discovery, and optional tool providers.
    /// This operation does not require or create a baseline.
    /// </summary>
    public Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default) =>
        DoctorAsync(true, cancellationToken);

    /// <summary>Runs diagnostics, optionally bypassing the repository graph cache.</summary>
    public async Task<DoctorReport> DoctorAsync(
        bool useCache,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        var sdk = await _services.Runner.RunAsync(
            "dotnet",
            ["--version"],
            RepositoryRoot,
            cancellationToken);
        checks.Add(sdk.State == ExecutionState.Succeeded
            ? new DoctorCheck(".NET SDK", DoctorCheckState.Passed, sdk.StandardOutput.Trim())
            : new DoctorCheck(
                ".NET SDK",
                DoctorCheckState.Failed,
                FirstDetail(sdk),
                "Install a supported .NET SDK and ensure dotnet is available on PATH."));

        var configPath = ContextPaths.Config(RepositoryRoot);
        checks.Add(new DoctorCheck(
            "Configuration",
            DoctorCheckState.Passed,
            File.Exists(configPath)
                ? $"Loaded {Path.GetRelativePath(RepositoryRoot, configPath).Replace('\\', '/')}"
                : "Using inferred defaults; no configuration file has been saved yet."));

        var inventory = await _services.Files.GetFilesAsync(
            RepositoryRoot,
            Configuration.Indexing,
            cancellationToken);
        var configuredExcludes = Configuration.Indexing.Exclude.Count == 0
            ? "none"
            : string.Join(", ", Configuration.Indexing.Exclude);
        checks.Add(new DoctorCheck(
            "Repository scope",
            Configuration.Indexing.RespectGitignore && !inventory.GitIgnoreApplied
                ? DoctorCheckState.Warning
                : DoctorCheckState.Passed,
            $"{inventory.RelativePaths.Count} files selected; .gitignore " +
            $"{(Configuration.Indexing.RespectGitignore ? inventory.GitIgnoreApplied ? "applied" : "requested but unavailable" : "disabled")}; " +
            $"built-in excludes: {string.Join(", ", RepositoryFileFilter.BuiltInExcludes)}; " +
            $"configured excludes: {configuredExcludes}.",
            Configuration.Indexing.RespectGitignore && !inventory.GitIgnoreApplied
                ? "Ensure Git is available so repository ignore rules can be applied."
                : null));

        var solutionPath = string.IsNullOrWhiteSpace(Configuration.Solution)
            ? null
            : Path.GetFullPath(Path.Combine(RepositoryRoot, Configuration.Solution));
        checks.Add(solutionPath is null
            ? new DoctorCheck(
                "Solution",
                DoctorCheckState.Warning,
                "No solution is configured; projects will be discovered from the repository.",
                "Set solution in .dev-context/config.json when the repository contains multiple solutions.")
            : File.Exists(solutionPath)
                ? new DoctorCheck(
                    "Solution",
                    DoctorCheckState.Passed,
                    Path.GetRelativePath(RepositoryRoot, solutionPath).Replace('\\', '/'))
                : new DoctorCheck(
                    "Solution",
                    DoctorCheckState.Failed,
                    $"Configured solution does not exist: {Configuration.Solution}",
                    "Correct solution in .dev-context/config.json."));

        var nuget = await _services.Runner.RunAsync(
            "dotnet",
            ["nuget", "list", "source", "--format", "short"],
            RepositoryRoot,
            cancellationToken);
        checks.Add(nuget.State == ExecutionState.Succeeded
            ? new DoctorCheck("NuGet configuration", DoctorCheckState.Passed, "NuGet sources are readable.")
            : new DoctorCheck(
                "NuGet configuration",
                DoctorCheckState.Warning,
                FirstDetail(nuget),
                "Check user/repository NuGet.Config permissions and source definitions."));

        IReadOnlyList<DoctorProjectSummary> projects = [];
        IReadOnlyList<CompilationCompletenessRecord> compilationCompleteness = [];
        try
        {
            var diagnosticConfig = useCache
                ? Configuration
                : Configuration with { Cache = new CacheConfig { Enabled = false } };
            var graph = await _services.Graph.BuildAsync(
                RepositoryRoot,
                diagnosticConfig,
                cancellationToken);
            projects = graph.Repository.Projects
                .Select(project => new DoctorProjectSummary(
                    project.Name,
                    project.Path,
                    project.TargetFrameworks,
                    project.Items.Select(item => item.ItemType)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Order(StringComparer.OrdinalIgnoreCase)
                        .ToArray()))
                .OrderBy(project => project.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            compilationCompleteness = graph.Symbols.CompilationCompleteness;
            checks.Add(new DoctorCheck(
                "Repository graph cache",
                DoctorCheckState.Informational,
                useCache
                    ? graph.CacheHit
                        ? $"HIT ({graph.ProjectCacheHits} project entries reused)."
                        : $"MISS ({graph.ProjectCacheHits} project entries reused, " +
                          $"{graph.ProjectCacheMisses} rebuilt)."
                    : "BYPASSED by request."));
            checks.Add(projects.Count > 0
                ? new DoctorCheck(
                    "Project discovery",
                    DoctorCheckState.Passed,
                    $"Discovered {projects.Count} project(s) with evaluated MSBuild inputs.")
                : new DoctorCheck(
                    "Project discovery",
                    DoctorCheckState.Failed,
                    "No projects were discovered.",
                    "Check the configured solution and project files."));
            var partialCompilations = graph.Symbols.CompilationCompleteness
                .Count(record => record.State == AnalysisCompletenessState.Partial);
            var failedCompilations = graph.Symbols.CompilationCompleteness
                .Count(record => record.State == AnalysisCompletenessState.Failed);
            var topDiagnostics = graph.Symbols.CompilationCompleteness
                .SelectMany(record => record.DiagnosticSummaries)
                .GroupBy(summary => summary.Id, StringComparer.Ordinal)
                .Select(group => new { Id = group.Key, Count = group.Sum(summary => summary.Count) })
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.Id, StringComparer.Ordinal)
                .Take(5)
                .Select(summary => $"{summary.Id} x{summary.Count}")
                .ToArray();
            var diagnosticRecommendation = topDiagnostics.Length == 0
                ? "Run doctor --explain-gaps for the per-project completeness breakdown."
                : $"Top diagnostics: {string.Join(", ", topDiagnostics)}. Run doctor --explain-gaps for files and gaps.";
            checks.Add(failedCompilations > 0
                ? new DoctorCheck(
                    "Semantic completeness",
                    DoctorCheckState.Failed,
                    $"{failedCompilations} failed and {partialCompilations} partial semantic compilation(s).",
                    diagnosticRecommendation)
                : partialCompilations > 0
                    ? new DoctorCheck(
                        "Semantic completeness",
                        DoctorCheckState.Warning,
                        $"{partialCompilations} of {graph.Symbols.CompilationCompleteness.Count} semantic compilation(s) are partial.",
                        diagnosticRecommendation)
                    : new DoctorCheck(
                        "Semantic completeness",
                        DoctorCheckState.Passed,
                        $"All {graph.Symbols.CompilationCompleteness.Count} semantic compilation(s) are complete."));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            checks.Add(new DoctorCheck(
                "Project discovery",
                DoctorCheckState.Failed,
                exception.Message,
                "Run dotnet build on the configured solution and resolve MSBuild evaluation errors."));
        }

        await CheckOptionalProviderAsync(
            checks,
            "dotnet format",
            Configuration.Analysis.DotnetFormat,
            "dotnet",
            ["format", "--help"],
            cancellationToken);
        await CheckOptionalProviderAsync(
            checks,
            "Qodana",
            Configuration.Analysis.Qodana,
            Configuration.Analysis.QodanaCommand,
            ["--version"],
            cancellationToken);
        checks.Add(new DoctorCheck(
            "Baseline",
            DoctorCheckState.Informational,
            BaselineExists ? "A baseline is available." : "No baseline exists; stateless API operations remain available."));
        checks.Add(new DoctorCheck(
            "Repository trust",
            Configuration.Indexing.ExecuteSourceGenerators
                ? DoctorCheckState.Warning
                : DoctorCheckState.Informational,
            Configuration.Indexing.ExecuteSourceGenerators
                ? "Source generators from evaluated analyzer assemblies are loaded and executed in-process."
                : "Source-generator execution is disabled; analyzer assemblies are not loaded by semantic indexing.",
            Configuration.Indexing.ExecuteSourceGenerators
                ? "Run RepoLens only against repositories and restored dependencies you trust, or set indexing.executeSourceGenerators to false."
                : null));

        return new DoctorReport
        {
            RepositoryRoot = RepositoryRoot,
            ConfigPath = configPath,
            SolutionPath = solutionPath,
            SdkVersion = sdk.State == ExecutionState.Succeeded ? sdk.StandardOutput.Trim() : null,
            BaselineExists = BaselineExists,
            Projects = projects,
            CompilationCompleteness = compilationCompleteness,
            Checks = checks
        };
    }

    /// <summary>
    /// Explains which evaluated projects own a path and which project dependants are affected.
    /// This operation does not require a baseline.
    /// </summary>
    public async Task<OwnershipExplanation> ExplainAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var requestedFullPath = Path.GetFullPath(
            Path.IsPathRooted(path) ? path : Path.Combine(RepositoryRoot, path));
        var relativePath = Path.GetRelativePath(RepositoryRoot, requestedFullPath);
        var isWithinRepository = relativePath != ".."
                                 && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !Path.IsPathRooted(relativePath);
        var normalizedPath = isWithinRepository
            ? ProjectOwnershipResolver.NormalizePath(relativePath)
            : requestedFullPath.Replace('\\', '/');

        IReadOnlyList<ProjectOwnershipMatch> owners = [];
        IReadOnlyList<string> affectedProjects = [];
        if (isWithinRepository)
        {
            var graph = await _services.Graph.BuildAsync(RepositoryRoot, Configuration, cancellationToken);
            owners = ProjectOwnershipResolver.Explain(normalizedPath, graph.Repository.Projects);
            affectedProjects = ProjectOwnershipResolver.ExpandAffectedProjects(
                    owners.Select(owner => owner.ProjectPath),
                    graph.Dependencies.Projects)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return new OwnershipExplanation
        {
            RequestedPath = path,
            NormalizedPath = normalizedPath,
            Exists = File.Exists(requestedFullPath) || Directory.Exists(requestedFullPath),
            IsWithinRepository = isWithinRepository,
            IsSharedInput = isWithinRepository && ProjectOwnershipResolver.IsSharedProjectInput(normalizedPath),
            Owners = owners,
            AffectedProjects = affectedProjects
        };
    }

    /// <summary>
    /// Builds a bounded, purpose-specific repository context with transparent hotspot metrics.
    /// Existing baseline build/test diagnostics are reused when available; no build or tests are run.
    /// </summary>
    public Task<RepositoryContextReport> ContextAsync(
        RepositoryContextOptions? options = null,
        CancellationToken cancellationToken = default) =>
        _services.Intelligence.BuildAsync(
            RepositoryRoot,
            Configuration,
            options ?? new RepositoryContextOptions(),
            cancellationToken);

    /// <summary>
    /// Retrieves a deterministic, token-bounded bundle of source evidence for a task.
    /// The bundle includes source coordinates, selection reasons, relationships, and
    /// explicit semantic-analysis gaps so consumers can distinguish absence from uncertainty.
    /// </summary>
    public Task<EvidenceBundle> QueryAsync(
        EvidenceQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _services.Evidence.BuildAsync(
            RepositoryRoot,
            Configuration,
            options,
            cancellationToken);
    }

    /// <summary>Returns exact, direction-aware structural relationships for one resolved symbol.</summary>
    public Task<SymbolReferenceQueryReport> QueryReferencesAsync(
        SymbolReferenceQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _services.References.QueryAsync(
            RepositoryRoot,
            Configuration,
            options,
            cancellationToken);
    }

    /// <summary>
    /// Runs a repeatable evidence-retrieval corpus twice per case and reports recall,
    /// precision, token use, latency, and deterministic-output checks.
    /// </summary>
    public Task<EvidenceBenchmarkReport> BenchmarkAsync(
        IReadOnlyList<EvidenceBenchmarkCase> cases,
        CancellationToken cancellationToken = default) =>
        _services.Benchmark.RunAsync(RepositoryRoot, Configuration, cases, cancellationToken);

    /// <summary>
    /// Saves a context report as Markdown. Default reports are retained under .dev-context/reports.
    /// </summary>
    public Task<RepositoryReportArtifact> SaveReportAsync(
        RepositoryContextReport report,
        string? outputPath = null,
        int retain = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return _services.Intelligence.SaveAsync(
            RepositoryRoot,
            report,
            outputPath,
            retain,
            cancellationToken);
    }

    /// <summary>
    /// Compares versioned metrics retained alongside default Markdown reports.
    /// </summary>
    public Task<RepositoryTrendReport> TrendAsync(
        int maxPoints = 20,
        CancellationToken cancellationToken = default) =>
        _services.Intelligence.TrendAsync(RepositoryRoot, maxPoints, cancellationToken);

    public void Reset()
    {
        _services.Store.Reset(RepositoryRoot);
        _services.Graph.ClearMemoryCache();
    }

    private static EngineServices CreateServices()
    {
        IProcessRunner runner = new ProcessRunner();
        var git = new GitService(runner);
        var files = new RepositoryFileFilter(runner);
        var projects = new ProjectIndexer(runner, files);
        var graph = new RepositoryGraphService(runner, projects, files);
        var build = new BuildService(runner);
        var tests = new TestService(runner);
        var analysis = new AnalysisService(runner);
        var store = new ContextStore();
        var capture = new BaselineCaptureService(runner, git, graph, build, tests, analysis);

        var evidence = new EvidenceQueryService(graph, git, store, files);
        var references = new SymbolReferenceQueryService(graph);
        return new EngineServices(
            runner,
            git,
            files,
            graph,
            store,
            capture,
            new VerificationService(capture, store),
            new AffectedService(git, graph, store),
            new CleanupService(runner, git),
            new RepositoryIntelligenceService(runner, git, graph, store),
            evidence,
            references,
            new EvidenceBenchmarkService(evidence));
    }

    private async Task CheckOptionalProviderAsync(
        ICollection<DoctorCheck> checks,
        string name,
        bool enabled,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!enabled)
        {
            checks.Add(new DoctorCheck(name, DoctorCheckState.Informational, "Disabled by configuration."));
            return;
        }

        var result = await _services.Runner.RunAsync(
            executable,
            arguments,
            RepositoryRoot,
            cancellationToken);
        checks.Add(result.State == ExecutionState.Succeeded
            ? new DoctorCheck(name, DoctorCheckState.Passed, "Configured provider is available.")
            : new DoctorCheck(
                name,
                DoctorCheckState.Failed,
                FirstDetail(result),
                $"Install {name} or disable it in .dev-context/config.json."));
    }

    private static string FirstDetail(ProcessResult result) =>
        new[] { result.StandardError, result.StandardOutput }
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.Length > 0)
        ?? $"Command failed with state {result.State}.";

    private sealed record EngineServices(
        IProcessRunner Runner,
        GitService Git,
        RepositoryFileFilter Files,
        RepositoryGraphService Graph,
        ContextStore Store,
        BaselineCaptureService Capture,
        VerificationService Verification,
        AffectedService Affected,
        CleanupService Cleanup,
        RepositoryIntelligenceService Intelligence,
        EvidenceQueryService Evidence,
        SymbolReferenceQueryService References,
        EvidenceBenchmarkService Benchmark);
}
