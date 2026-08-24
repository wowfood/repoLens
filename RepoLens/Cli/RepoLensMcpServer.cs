using System.ComponentModel;
using DevContext.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DevContext.Cli;

internal static class RepoLensMcpServer
{
    public static async Task RunAsync(DevContextApi api, CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(api);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<RepoLensMcpTools>();

        await builder.Build().RunAsync(cancellationToken);
    }
}

[McpServerToolType]
internal sealed class RepoLensMcpTools(DevContextApi api)
{
    [McpServerTool(Name = "status", UseStructuredContent = true, ReadOnly = true)]
    [Description("Read the stored RepoLens baseline summary without rebuilding or running tests. Requires an existing baseline.")]
    public Task<StatusReport> StatusAsync(CancellationToken cancellationToken) =>
        api.StatusAsync(cancellationToken);

    [McpServerTool(Name = "affected", UseStructuredContent = true, ReadOnly = true)]
    [Description("Identify files, declarations, symbols, projects, and tests affected since the RepoLens baseline. Requires an existing baseline.")]
    public Task<AffectedReport> AffectedAsync(CancellationToken cancellationToken) =>
        api.AffectedAsync(cancellationToken);

    [McpServerTool(Name = "explain", UseStructuredContent = true, ReadOnly = true)]
    [Description("Explain evaluated project ownership and downstream impact for a repository-relative or absolute path.")]
    public Task<OwnershipExplanation> ExplainAsync(
        [Description("Repository-relative or absolute file/directory path.")] string path,
        CancellationToken cancellationToken) =>
        api.ExplainAsync(path, cancellationToken);

    [McpServerTool(Name = "context", UseStructuredContent = true, ReadOnly = true)]
    [Description("Build bounded change, architecture, build, or risk context from deterministic repository evidence. Does not build or run tests.")]
    public Task<RepositoryContextReport> ContextAsync(
        [Description("Context purpose: change, architecture, build, or risk.")] string purpose = "change",
        [Description("Scope: automatic, full, changed, project, or path.")] string scope = "automatic",
        [Description("Project or path target when the selected scope requires one.")] string? target = null,
        [Description("Optional repository-relative or absolute Cobertura coverage file.")] string? coveragePath = null,
        [Description("Maximum hotspot records to return; must be positive.")] int maxHotspots = 10,
        [Description("Maximum symbol records to return; must be positive.")] int maxSymbols = 200,
        [Description("Git history window in months; must be positive.")] int historyMonths = 12,
        CancellationToken cancellationToken = default) =>
        api.ContextAsync(
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
            cancellationToken);

    [McpServerTool(Name = "query", UseStructuredContent = true, ReadOnly = true)]
    [Description("Retrieve a deterministic, token-bounded source-evidence bundle for a coding task or repository question.")]
    public Task<EvidenceBundle> QueryAsync(
        [Description("Coding task or repository question to ground in source evidence.")] string query,
        [Description("Approximate token budget; minimum 256.")] int maxTokens = 3000,
        [Description("Maximum evidence blocks; must be positive.")] int maxResults = 20,
        [Description("Semantic graph expansion depth from 0 through 3.")] int graphDepth = 1,
        [Description("Limit seed evidence to files changed since the baseline.")] bool changedOnly = false,
        [Description("Include test evidence in the result.")] bool includeTests = true,
        [Description("Optional repository-relative project filter.")] string? project = null,
        [Description("Optional declaration-kind filters.")] string[]? kinds = null,
        CancellationToken cancellationToken = default) =>
        api.QueryAsync(
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
            cancellationToken);

    [McpServerTool(Name = "refs", UseStructuredContent = true, ReadOnly = true)]
    [Description("Resolve exact, direction-aware structural references for a symbol name or file:line target.")]
    public Task<SymbolReferenceQueryReport> ReferencesAsync(
        [Description("Fully qualified or bare symbol name, or repository-relative file:line target.")] string target,
        [Description("Relation: callers, callees, implementers, implementations, overrides, subtypes, constructors-of, readers, writers, tests-covering, or injected-into.")] string relation = "callers",
        [Description("Maximum reference matches; must be positive.")] int maxResults = 50,
        [Description("Approximate token budget; minimum 256.")] int maxTokens = 3000,
        CancellationToken cancellationToken = default) =>
        api.QueryReferencesAsync(
            new SymbolReferenceQueryOptions
            {
                Target = Required(target, nameof(target)),
                Relation = ParseRelation(relation),
                MaxResults = Positive(maxResults, nameof(maxResults)),
                MaxTokens = AtLeast(maxTokens, 256, nameof(maxTokens))
            },
            cancellationToken);

    [McpServerTool(
        Name = "verify",
        UseStructuredContent = true,
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Explicitly rebuild, run configured tests and analyzers, and report regressions since the baseline. This can be slow and writes normal build/test artifacts.")]
    public Task<VerificationReport> VerifyAsync(CancellationToken cancellationToken) =>
        api.VerifyAsync(cancellationToken);

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
