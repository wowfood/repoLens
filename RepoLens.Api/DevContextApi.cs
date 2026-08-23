using System.Reflection;
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

    private DevContextApi(
        string repositoryRoot,
        DevContextConfig configuration,
        bool isNewConfiguration,
        EngineServices services)
    {
        RepositoryRoot = repositoryRoot;
        Configuration = configuration;
        _isNewConfiguration = isNewConfiguration;
        _services = services;
    }

    public string RepositoryRoot { get; }
    public DevContextConfig Configuration { get; }
    public bool BaselineExists => _services.Store.BaselineExists(RepositoryRoot);

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

        if (configuration is null)
        {
            (resolvedConfiguration, isNewConfiguration) = await ConfigLoader.LoadAsync(
                repositoryRoot,
                cancellationToken);
        }
        else
        {
            ConfigLoader.Validate(configuration);
            resolvedConfiguration = configuration;
            isNewConfiguration = false;
        }

        return new DevContextApi(
            repositoryRoot,
            resolvedConfiguration,
            isNewConfiguration,
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

    public async Task<StatusReport> BaselineAsync(
        bool replace = false,
        CancellationToken cancellationToken = default)
    {
        if (BaselineExists && !replace)
        {
            throw new InvalidOperationException(
                "A baseline already exists. Set replace only when explicitly beginning a new logical task.");
        }

        await _services.Store.SaveConfigIfNewAsync(
            RepositoryRoot,
            Configuration,
            _isNewConfiguration,
            cancellationToken);

        var bundle = await _services.Capture.CaptureAsync(
            RepositoryRoot,
            Configuration,
            null,
            CapturePurpose.Baseline,
            null,
            cancellationToken);

        await _services.Store.SaveBaselineAsync(
            RepositoryRoot,
            bundle,
            Configuration,
            replace,
            cancellationToken);

        _isNewConfiguration = false;
        return await StatusAsync(cancellationToken);
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
    public async Task<DoctorReport> DoctorAsync(CancellationToken cancellationToken = default)
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
        try
        {
            var diagnosticConfig = Configuration with { Cache = new CacheConfig { Enabled = false } };
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
            checks.Add(failedCompilations > 0
                ? new DoctorCheck(
                    "Semantic completeness",
                    DoctorCheckState.Failed,
                    $"{failedCompilations} failed and {partialCompilations} partial semantic compilation(s).",
                    "Inspect compilationCompleteness in JSON context for unresolved references and generated-source gaps.")
                : partialCompilations > 0
                    ? new DoctorCheck(
                        "Semantic completeness",
                        DoctorCheckState.Warning,
                        $"{partialCompilations} of {graph.Symbols.CompilationCompleteness.Count} semantic compilation(s) are partial.",
                        "Inspect compilationCompleteness in JSON context before treating missing relationships as absent.")
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

    public void Reset() => _services.Store.Reset(RepositoryRoot);

    private static EngineServices CreateServices()
    {
        IProcessRunner runner = new ProcessRunner();
        var git = new GitService(runner);
        var projects = new ProjectIndexer(runner);
        var graph = new RepositoryGraphService(runner, projects);
        var build = new BuildService(runner);
        var tests = new TestService(runner);
        var analysis = new AnalysisService(runner);
        var store = new ContextStore();
        var capture = new BaselineCaptureService(runner, git, graph, build, tests, analysis);

        var evidence = new EvidenceQueryService(graph, git, store);
        return new EngineServices(
            runner,
            graph,
            store,
            capture,
            new VerificationService(capture, store),
            new AffectedService(git, graph, store),
            new CleanupService(runner, git),
            new RepositoryIntelligenceService(runner, git, graph, store),
            evidence,
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
        RepositoryGraphService Graph,
        ContextStore Store,
        BaselineCaptureService Capture,
        VerificationService Verification,
        AffectedService Affected,
        CleanupService Cleanup,
        RepositoryIntelligenceService Intelligence,
        EvidenceQueryService Evidence,
        EvidenceBenchmarkService Benchmark);
}
