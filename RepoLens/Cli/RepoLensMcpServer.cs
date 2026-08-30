using System.ComponentModel;
using System.Text.Json;
using DevContext.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevContext.Cli;

internal static class RepoLensMcpServer
{
    /// <summary>
    /// Sent to the client at initialization. Without it, a client sees nine similarly-shaped tools
    /// and no basis for choosing between them — and, more importantly, no statement of the one rule
    /// that makes this server worth using: that a result which abstains must not become a claim.
    /// </summary>
    internal const string Instructions = """
        RepoLens answers questions about this .NET repository from deterministic analysis: MSBuild
        evaluation plus Roslyn semantic compilation. No LLM, no embeddings, no guessing. The same
        question returns the same answer.

        Choosing a tool:
        - refs — an exact structural relationship: who calls this, what implements this, which tests
          cover this. Prefer it over query whenever the question has an exact answer. It resolves one
          symbol against the typed dependency graph and reports ambiguity instead of guessing.
        - query — relevant source for a task or open question, ranked and token-bounded. Use when you
          do not yet know which symbol matters.
        - explain — which evaluated projects own a path and what depends on them.
        - affected — what changed since the baseline, and the declarations, projects, and tests it
          touches.
        - context — bounded change, architecture, build, or risk narrative for orientation.
        - status — the stored baseline summary, without running anything.
        - baseline — record the repository's current state as the comparison point for a task.
          Required before status, affected, and verify. Writes to .dev-context/.
        - verify — rebuild, run tests and analyzers, and report regressions against the baseline.
          Slow, and it writes normal build artifacts. Never call it to answer a question.
        - doctor — check that the tooling and repository are usable when something looks wrong.

        The honesty contract, which matters more than any of the above:
        - Every query and refs result carries an evidence decision (Sufficient, Partial, or
          Insufficient), an explicit shouldAbstain flag, and the analysis gaps behind it.
        - When shouldAbstain is true, the analysis could not see enough to answer. Do not turn that
          into an assertion, and do not report an empty result as proof that nothing exists.
        - An empty refs result proves absence only when the relevant compilation records are
          complete. Otherwise it means "not found in what could be analysed", which is a weaker and
          different statement.
        - Relationships are strong evidence, not proof. Reflection, dependency-injection wiring,
          Razor, XAML, and generated code can reach past the graph.
        """;

    public static async Task RunAsync(DevContextApi api, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(api);
        builder.Services
            .AddMcpServer(options => options.ServerInstructions = Instructions)
            .WithStdioServerTransport()
            .WithTools<RepoLensMcpTools>()
            .WithPrompts<RepoLensMcpPrompts>();

        await builder.Build().RunAsync(cancellationToken);
    }
}

/// <summary>
/// An evidence bundle trimmed for transport.
///
/// <see cref="EvidenceBundle"/> carries the token-bounded <c>Prompt</c> and the <c>Blocks</c> whose
/// excerpts the prompt was rendered from, so returning it whole ships every excerpt twice: asking
/// for a 3,000-token budget delivered roughly 6,000. That defeats the budget, which is the product's
/// core value. The prompt is kept because it is the artifact worth reading; the blocks are reduced
/// to the coordinates needed to open the real file.
/// </summary>
internal sealed record McpEvidenceResult
{
    public required string BundleId { get; init; }
    public required string Query { get; init; }
    public required string Prompt { get; init; }
    public required int ApproximateTokens { get; init; }
    public required bool Truncated { get; init; }
    public required EvidenceSufficiency Sufficiency { get; init; }
    public required bool ShouldAbstain { get; init; }
    public required IReadOnlyList<string> SufficiencyReasons { get; init; }
    public required IReadOnlyList<string> AnalysisGaps { get; init; }
    public required IReadOnlyList<McpEvidenceLocation> Locations { get; init; }
    public required IReadOnlyList<EvidenceRelationship> Relationships { get; init; }
    public required IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; }

    public static McpEvidenceResult From(EvidenceBundle bundle) => new()
    {
        BundleId = bundle.BundleId,
        Query = bundle.Query,
        Prompt = bundle.Prompt,
        ApproximateTokens = bundle.ApproximateTokens,
        Truncated = bundle.Truncated,
        Sufficiency = bundle.Sufficiency,
        ShouldAbstain = bundle.ShouldAbstain,
        SufficiencyReasons = bundle.SufficiencyReasons,
        AnalysisGaps = bundle.AnalysisGaps,
        Locations = bundle.Blocks
            .Select(block => new McpEvidenceLocation
            {
                Id = block.Id,
                File = block.File,
                Project = block.Project,
                StartLine = block.StartLine,
                EndLine = block.EndLine,
                Kind = block.Kind,
                SemanticName = block.SemanticName,
                SelectionReasons = block.SelectionReasons
            })
            .ToArray(),
        Relationships = bundle.Relationships,
        CompilationCompleteness = bundle.CompilationCompleteness
    };
}

internal sealed record McpEvidenceLocation
{
    public required string Id { get; init; }
    public required string File { get; init; }
    public required string Project { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string Kind { get; init; }
    public string? SemanticName { get; init; }
    public required IReadOnlyList<string> SelectionReasons { get; init; }
}

/// <summary>
/// A repository context report trimmed for transport. The full report carries rendered Markdown
/// alongside every symbol, type definition, and per-type/per-method metric it was rendered from,
/// and has no token bound at all. The narrative and the decision-relevant records are kept; the bulk
/// collections are available only when explicitly requested.
/// </summary>
internal sealed record McpContextResult
{
    public required ContextPurpose Purpose { get; init; }
    public required ContextScope Scope { get; init; }
    public string? Target { get; init; }
    public string? Branch { get; init; }
    public required string Markdown { get; init; }
    public required int ApproximateTokens { get; init; }
    public required IReadOnlyList<string> AnalyzedProjects { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public required IReadOnlyList<DiagnosticRecord> Diagnostics { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> FailingTests { get; init; }
    public required IReadOnlyList<FileHotspot> Hotspots { get; init; }
    public required IReadOnlyList<string> AnalysisGaps { get; init; }
    public required IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; }

    /// <summary>Populated only when the caller asks for symbols.</summary>
    public IReadOnlyList<SymbolRecord> Symbols { get; init; } = [];

    public static McpContextResult From(RepositoryContextReport report, bool includeSymbols) => new()
    {
        Purpose = report.Purpose,
        Scope = report.Scope,
        Target = report.Target,
        Branch = report.Branch,
        Markdown = report.Markdown,
        ApproximateTokens = report.ApproximateTokens,
        AnalyzedProjects = report.AnalyzedProjects,
        ChangedFiles = report.ChangedFiles,
        Diagnostics = report.Diagnostics,
        FailingTests = report.FailingTests,
        Hotspots = report.Hotspots,
        AnalysisGaps = report.AnalysisGaps,
        CompilationCompleteness = report.CompilationCompleteness,
        Symbols = includeSymbols ? report.Symbols : []
    };
}

[McpServerToolType]
internal sealed class RepoLensMcpTools(DevContextApi api)
{
    [McpServerTool(Name = "status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read the stored RepoLens baseline summary without rebuilding or running tests. Requires an existing baseline; call 'baseline' first if none exists.")]
    public Task<StatusReport> StatusAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => api.StatusAsync(cancellationToken));

    [McpServerTool(
        Name = "baseline",
        UseStructuredContent = true,
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false)]
    [Description("Record the repository's current git, build, test, and structural state as the comparison point for a task. Required before status, affected, and verify. Writes to .dev-context/ and can be slow. Refuses to overwrite an existing baseline unless replace is true.")]
    public Task<StatusReport> BaselineAsync(
        [Description("Replace an existing baseline. Only when the user explicitly began a new logical task: it discards the comparison point the current task is measured against.")] bool replace = false,
        CancellationToken cancellationToken = default) =>
        GuardAsync(() => api.BaselineAsync(replace, cancellationToken));

    [McpServerTool(Name = "doctor", UseStructuredContent = true, ReadOnly = true)]
    [Description("Check that the SDK, Git, configured solution, and optional providers are usable. Start here when another tool reports that something is unavailable.")]
    public Task<DoctorReport> DoctorAsync(
        [Description("Reuse the repository graph cache. Set false to force a full re-evaluation.")] bool useCache = true,
        CancellationToken cancellationToken = default) =>
        GuardAsync(() => api.DoctorAsync(useCache, cancellationToken));

    [McpServerTool(Name = "affected", UseStructuredContent = true, ReadOnly = true)]
    [Description("Identify files, declarations, symbols, projects, and tests affected since the RepoLens baseline. Requires an existing baseline.")]
    public Task<AffectedReport> AffectedAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => api.AffectedAsync(cancellationToken));

    [McpServerTool(Name = "explain", UseStructuredContent = true, ReadOnly = true)]
    [Description("Explain evaluated project ownership and downstream impact for a repository-relative or absolute path.")]
    public Task<OwnershipExplanation> ExplainAsync(
        [Description("Repository-relative or absolute file/directory path.")] string path,
        CancellationToken cancellationToken) =>
        GuardAsync(() => api.ExplainAsync(path, cancellationToken));

    [McpServerTool(Name = "context", UseStructuredContent = true, ReadOnly = true)]
    [Description("Build change, architecture, build, or risk context from deterministic repository evidence. Returns a rendered narrative plus decision-relevant records; ask for symbols explicitly when you need them. The narrative grows with scope, so pass scope=project or scope=changed with a target on a large repository rather than defaulting to the whole thing; check approximateTokens in the result. Does not build or run tests.")]
    public async Task<McpContextResult> ContextAsync(
        [Description("Context purpose: change, architecture, build, or risk.")] string purpose = "change",
        [Description("Scope: automatic, full, changed, project, or path.")] string scope = "automatic",
        [Description("Project or path target when the selected scope requires one.")] string? target = null,
        [Description("Optional repository-relative or absolute Cobertura coverage file.")] string? coveragePath = null,
        [Description("Maximum hotspot records to return; must be positive.")] int maxHotspots = 10,
        [Description("Maximum symbol records to analyze; must be positive.")] int maxSymbols = 50,
        [Description("Include the full symbol list in the result. Off by default because it is large and the narrative usually answers the question.")] bool includeSymbols = false,
        [Description("Git history window in months; must be positive.")] int historyMonths = 12,
        CancellationToken cancellationToken = default) =>
        McpContextResult.From(
            await GuardAsync(() => api.ContextAsync(
                new RepositoryContextOptions
                {
                    Purpose = ParsePurpose(purpose),
                    Scope = ParseScope(scope),
                    Target = target,
                    CoberturaPath = coveragePath,
                    MaxHotspots = Positive(maxHotspots, nameof(maxHotspots)),
                    MaxSymbols = Positive(maxSymbols, nameof(maxSymbols)),
                    GitHistoryMonths = Positive(historyMonths, nameof(historyMonths))
                },
                cancellationToken)),
            includeSymbols);

    [McpServerTool(Name = "query", UseStructuredContent = true, ReadOnly = true)]
    [Description("Retrieve a deterministic, token-bounded source-evidence bundle for a coding task or repository question. Returns a prompt-ready narrative plus the coordinates it came from. Check shouldAbstain before asserting anything from the result.")]
    public async Task<McpEvidenceResult> QueryAsync(
        [Description("Coding task or repository question to ground in source evidence.")] string query,
        [Description("Approximate token budget; minimum 256.")] int maxTokens = 3000,
        [Description("Maximum evidence blocks; must be positive.")] int maxResults = 20,
        [Description("Semantic graph expansion depth from 0 through 3.")] int graphDepth = 1,
        [Description("Limit seed evidence to files changed since the baseline.")] bool changedOnly = false,
        [Description("Include test evidence in the result.")] bool includeTests = true,
        [Description("Optional repository-relative project filter.")] string? project = null,
        [Description("Optional declaration-kind filters.")] string[]? kinds = null,
        CancellationToken cancellationToken = default) =>
        McpEvidenceResult.From(await GuardAsync(() => api.QueryAsync(
            new EvidenceQueryOptions
            {
                Query = Required(query, nameof(query)),
                MaxTokens = AtLeast(maxTokens, 256, nameof(maxTokens)),
                MaxResults = Positive(maxResults, nameof(maxResults)),
                GraphDepth = InRange(graphDepth, 0, 3, nameof(graphDepth)),
                ChangedOnly = changedOnly,
                IncludeTests = includeTests,
                Project = project,
                Kinds = kinds ?? []
            },
            cancellationToken)));

    [McpServerTool(Name = "refs", UseStructuredContent = true, ReadOnly = true)]
    [Description("Resolve exact, direction-aware structural references for a symbol name or file:line target. Prefer this over query when the question has an exact answer. An empty result proves absence only when the reported compilation records are complete.")]
    public Task<SymbolReferenceQueryReport> ReferencesAsync(
        [Description("Fully qualified or bare symbol name, or repository-relative file:line target.")] string target,
        [Description("Relation: callers, callees, implementers, implementations, overrides, subtypes, constructors-of, readers, writers, tests-covering, or injected-into.")] string relation = "callers",
        [Description("Maximum reference matches; must be positive.")] int maxResults = 50,
        [Description("Approximate token budget; minimum 256.")] int maxTokens = 3000,
        CancellationToken cancellationToken = default) =>
        GuardAsync(() => api.QueryReferencesAsync(
            new SymbolReferenceQueryOptions
            {
                Target = Required(target, nameof(target)),
                Relation = ParseRelation(relation),
                MaxResults = Positive(maxResults, nameof(maxResults)),
                MaxTokens = AtLeast(maxTokens, 256, nameof(maxTokens))
            },
            cancellationToken));

    [McpServerTool(
        Name = "verify",
        UseStructuredContent = true,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Explicitly rebuild, run configured tests and analyzers, and report regressions since the baseline. This can be slow and writes normal build/test artifacts. Never call it to answer a question.")]
    public Task<VerificationReport> VerifyAsync(CancellationToken cancellationToken) =>
        GuardAsync(() => api.VerifyAsync(cancellationToken));

    /// <summary>
    /// Maps engine failures onto the protocol. Without this the common ones — no baseline yet, a
    /// diverged baseline, an unavailable dotnet or git, a stored artifact from another version —
    /// escaped as raw exceptions, so the client saw an internal error instead of the remedy. Every
    /// one of these is actionable, and several name a tool the client can call next.
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation();
        }
        catch (McpException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new McpProtocolException(
                $"{exception.Message} Re-create it with the 'baseline' tool.",
                McpErrorCode.InvalidRequest);
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException
                                              or JsonException)
        {
            throw new McpProtocolException(exception.Message, McpErrorCode.InvalidRequest);
        }
    }

    private static ContextPurpose ParsePurpose(string value) => Normalize(value) switch
    {
        "change" => ContextPurpose.Change,
        "architecture" => ContextPurpose.Architecture,
        "build" => ContextPurpose.Build,
        "risk" => ContextPurpose.Risk,
        _ => throw InvalidParameter(nameof(value), value, "change, architecture, build, or risk")
    };

    private static ContextScope ParseScope(string value) => Normalize(value) switch
    {
        "automatic" => ContextScope.Automatic,
        "full" or "fullrepository" => ContextScope.FullRepository,
        "changed" or "changedfiles" => ContextScope.ChangedFiles,
        "project" => ContextScope.Project,
        "path" => ContextScope.Path,
        _ => throw InvalidParameter(nameof(value), value, "automatic, full, changed, project, or path")
    };

    private static SymbolReferenceRelation ParseRelation(string value) => Normalize(value) switch
    {
        "callers" => SymbolReferenceRelation.Callers,
        "callees" => SymbolReferenceRelation.Callees,
        "implementers" => SymbolReferenceRelation.Implementers,
        "implementations" => SymbolReferenceRelation.Implementations,
        "overrides" => SymbolReferenceRelation.Overrides,
        "subtypes" => SymbolReferenceRelation.Subtypes,
        "constructorsof" => SymbolReferenceRelation.ConstructorsOf,
        "readers" => SymbolReferenceRelation.Readers,
        "writers" => SymbolReferenceRelation.Writers,
        "testscovering" => SymbolReferenceRelation.TestsCovering,
        "injectedinto" => SymbolReferenceRelation.InjectedInto,
        _ => throw InvalidParameter(
            nameof(value),
            value,
            "callers, callees, implementers, implementations, overrides, subtypes, constructors-of, readers, writers, tests-covering, or injected-into")
    };

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new McpProtocolException($"'{name}' must not be empty.", McpErrorCode.InvalidParams)
            : value;

    private static int Positive(int value, string name) => AtLeast(value, 1, name);

    private static int AtLeast(int value, int minimum, string name) =>
        value < minimum
            ? throw new McpProtocolException($"'{name}' must be at least {minimum}.", McpErrorCode.InvalidParams)
            : value;

    private static int InRange(int value, int minimum, int maximum, string name) =>
        value < minimum || value > maximum
            ? throw new McpProtocolException(
                $"'{name}' must be from {minimum} through {maximum}.",
                McpErrorCode.InvalidParams)
            : value;

    private static McpProtocolException InvalidParameter(
        string name,
        string value,
        string expected) =>
        new($"Unknown {name} '{value}'. Use {expected}.", McpErrorCode.InvalidParams);

    private static string Normalize(string value) =>
        value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}

/// <summary>
/// The two shipped workflows, exposed over the protocol. A skill file has to be written once per
/// vendor; a prompt reaches every client that speaks MCP, which is the point of running a server at
/// all.
/// </summary>
[McpServerPromptType]
internal sealed class RepoLensMcpPrompts
{
    [McpServerPrompt(Name = "coding-task")]
    [Description("Run a complete coding task against this repository: baseline, grounded reconnaissance, minimal change, cleanup, and verification of only this task's regressions.")]
    public static string CodingTask(
        [Description("What to implement, fix, or refactor.")] string task) =>
        $"""
        Complete this coding task in the current repository: {task}

        Use the RepoLens tools for repository facts and normal tools for reasoning and editing.

        1. Baseline. Call `baseline`. If one already exists and this continues the same logical task,
           keep it — do not replace it, and never pass replace unless the user explicitly began a new
           task. Call `status` and record the branch, HEAD, pre-existing working-tree changes, and
           existing build/test/diagnostic failures. Those are not your regressions.
        2. Ground the work. Call `query` for the task to find relevant source. Call `refs` for every
           question with an exact answer — who calls this, what implements this, which tests cover
           this. Call `explain` for project ownership. Read the source the evidence points at rather
           than scanning the repository.
        3. Respect abstention. If a result has shouldAbstain set, it could not see enough to answer.
           Do not assert from it, and do not read an empty result as proof that nothing exists. Widen
           the search with normal tools and say so in your report.
        4. Implement the smallest coherent change. Preserve existing naming, structure, error
           handling, and architectural boundaries. Add or update focused tests when behavior changes.
        5. Verify. Run cleanup if `.dev-context/config.json` enables it, then call `verify`. Fix only
           regressions this task introduced; leave pre-existing failures alone. If verification could
           not execute, say so rather than reporting success.
        6. Report what changed, the baseline id, the verification outcome, pre-existing failures you
           left in place, and anything that could not be checked. Never describe a skipped or failed
           check as passing.
        """;

    [McpServerPrompt(Name = "ground-question")]
    [Description("Answer a question about this repository from retrieved evidence, honouring RepoLens's abstention contract.")]
    public static string GroundQuestion(
        [Description("The question about the repository.")] string question) =>
        $"""
        Answer this question about the current repository: {question}

        Ground the answer in retrieved evidence rather than assumption.

        - If the question has an exact structural answer — who calls this, what implements this,
          which tests cover this, what does this path belong to — call `refs` or `explain`. They
          resolve against the typed dependency graph and report ambiguity instead of guessing.
        - Otherwise call `query` to retrieve ranked, token-bounded source evidence.
        - Read the files the result points at before concluding. Relationships are strong evidence,
          not proof: reflection, dependency-injection wiring, Razor, XAML, and generated code can
          reach past the graph.
        - If the result sets shouldAbstain, or reports Insufficient evidence, say what could not be
          determined and why, citing the reported analysis gaps. Do not present an abstention as an
          answer, and do not treat an empty result as proof of absence unless the compilation records
          are complete.

        Cite repository-relative file paths and line numbers for every claim.
        """;
}
