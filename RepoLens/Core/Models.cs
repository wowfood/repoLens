using System.Text.Json.Serialization;
using DevContext.Configuration;

namespace DevContext.Core;

public static class SchemaVersions
{
    // Bumped to 11 when ExecutionState gained TimedOut. The value is persisted as a string, so an
    // artifact carrying it is unreadable by an earlier build; without the bump that would surface as
    // an opaque JSON parse error instead of the explicit schema-window message below.
    public const int Current = 11;
    public const int MinimumReadable = 5;

    public static bool IsReadable(int version) => version is >= MinimumReadable and <= Current;

    public static void EnsureReadable(int version, string artifact)
    {
        if (!IsReadable(version))
        {
            throw new InvalidDataException(
                $"{artifact} uses schema {version}; this version reads schemas {MinimumReadable}-{Current}.");
        }
    }
}

public sealed record InitializationReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string RepositoryRoot { get; init; }
    public required string ConfigPath { get; init; }
    public required bool Created { get; init; }
    public bool Migrated { get; init; }
    public required DevContextConfig Configuration { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionState
{
    Succeeded,
    Failed,
    Unavailable,
    Skipped,

    /// <summary>
    /// The command exceeded its configured wall-clock ceiling and was terminated. Distinct from
    /// <see cref="Failed"/> on purpose: a failed command produced a verdict, whereas a terminated one
    /// produced no information at all, and reporting "no regressions" from it would be a claim the
    /// run never earned.
    /// </summary>
    TimedOut
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DoctorCheckState
{
    Passed,
    Warning,
    Failed,
    Informational
}

public sealed record DoctorCheck(
    string Name,
    DoctorCheckState State,
    string Detail,
    string? Recommendation = null);

public sealed record DoctorProjectSummary(
    string Name,
    string Path,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> ItemTypes);

public sealed record DoctorReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string RepositoryRoot { get; init; }
    public required string ConfigPath { get; init; }
    public required string? SolutionPath { get; init; }
    public required string? SdkVersion { get; init; }
    public required bool BaselineExists { get; init; }
    public required IReadOnlyList<DoctorProjectSummary> Projects { get; init; }
    public IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; } = [];
    public required IReadOnlyList<DoctorCheck> Checks { get; init; }
    public bool IsHealthy => Checks.All(check => check.State != DoctorCheckState.Failed);
}

public sealed record ProjectOwnershipMatch(
    string ProjectName,
    string ProjectPath,
    string Reason,
    IReadOnlyList<string> ItemTypes);

public sealed record OwnershipExplanation
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string RequestedPath { get; init; }
    public required string NormalizedPath { get; init; }
    public required bool Exists { get; init; }
    public required bool IsWithinRepository { get; init; }
    public required bool IsSharedInput { get; init; }
    public required IReadOnlyList<ProjectOwnershipMatch> Owners { get; init; }
    public required IReadOnlyList<string> AffectedProjects { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextPurpose
{
    Change,
    Architecture,
    Build,
    Risk
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContextScope
{
    Automatic,
    FullRepository,
    ChangedFiles,
    Project,
    Path
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisCompletenessState
{
    Complete,
    Partial,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceConfidence
{
    SemanticResolved,
    SyntaxFallback,
    ConventionHeuristic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvidenceSufficiency
{
    Sufficient,
    Partial,
    Insufficient
}

public sealed record RepositoryContextOptions
{
    public ContextPurpose Purpose { get; init; } = ContextPurpose.Change;
    public ContextScope Scope { get; init; } = ContextScope.Automatic;
    public string? Target { get; init; }
    public int MaxHotspots { get; init; } = 10;
    public int MaxSymbols { get; init; } = 200;
    public int GitHistoryMonths { get; init; } = 12;
    public string? CoberturaPath { get; init; }
}

public sealed record EvidenceQueryOptions
{
    public required string Query { get; init; }
    public int MaxTokens { get; init; } = 3000;
    public int MaxResults { get; init; } = 20;
    public int GraphDepth { get; init; } = 1;
    public bool ChangedOnly { get; init; }
    public bool IncludeTests { get; init; } = true;
    public string? Project { get; init; }
    public IReadOnlyList<string> Kinds { get; init; } = [];
}

public sealed record EvidenceBenchmarkCase
{
    public required string Name { get; init; }
    public required string Query { get; init; }
    public required IReadOnlyList<string> ExpectedFiles { get; init; }

    /// <summary>
    /// Structural edges the bundle must contain. Two forms are accepted:
    /// <c>"method-call"</c> requires at least one edge of that kind anywhere in the bundle, and
    /// <c>"method-call: src/A.cs -&gt; src/B.cs"</c> requires an edge of that kind whose source and
    /// target blocks live in those two files. The second form is what actually pins retrieval
    /// behaviour; the first only proves the relationship extractor emitted something.
    /// </summary>
    public IReadOnlyList<string> ExpectedRelationships { get; init; } = [];

    /// <summary>
    /// Minimum share of retrieved files that must be expected ones. Recall alone cannot fail a case
    /// that pads the budget with irrelevant files, so a case without a precision floor is only half
    /// a gate.
    /// </summary>
    public double MinPrecision { get; init; }

    /// <summary>
    /// Acceptance ceiling for the bundle's approximate token count, defaulting to
    /// <see cref="MaxTokens"/>. The two are separate on purpose: <see cref="MaxTokens"/> is the
    /// budget the query is given, and a bundle can never exceed the budget it was handed, so
    /// asserting against it detects nothing. Growth is only visible against a ceiling set below the
    /// budget, from what the case actually costs today.
    /// </summary>
    public int? MaxApproximateTokens { get; init; }

    public int MaxTokens { get; init; } = 1500;
    public int MaxResults { get; init; } = 8;
    public int GraphDepth { get; init; } = 1;
    public EvidenceSufficiency? ExpectedSufficiency { get; init; }
    public bool? ExpectAbstention { get; init; }

    /// <summary>
    /// Measures the case and reports its failures without failing the run. Reserved for behaviour
    /// that is known to be wrong today and is queued to be fixed: encoding the wrong answer as the
    /// expected one would make the corpus lie, and deleting the case would hide the deficiency
    /// entirely. An advisory case states the intended answer, reports the gap on every run, and
    /// becomes blocking by deleting this flag once the gap is closed.
    /// </summary>
    public bool Advisory { get; init; }
}

public sealed record EvidenceBenchmarkCaseResult
{
    public required string Name { get; init; }
    public required double FileRecall { get; init; }
    public required double FilePrecision { get; init; }
    public required IReadOnlyList<string> MissingFiles { get; init; }
    public required IReadOnlyList<string> UnexpectedFiles { get; init; }
    public required IReadOnlyList<string> MissingRelationships { get; init; }
    public required int ApproximateTokens { get; init; }
    public required int EvidenceBlocks { get; init; }
    public required long ColdMilliseconds { get; init; }
    public required long WarmMilliseconds { get; init; }
    public required bool Deterministic { get; init; }
    public required EvidenceSufficiency Sufficiency { get; init; }
    public required bool ShouldAbstain { get; init; }
    public required bool SufficiencyMatched { get; init; }
    public required bool Passed { get; init; }

    /// <summary>
    /// Why the case did not meet its acceptance conditions, one entry per unmet condition. Empty
    /// when every condition held. A gate that reports only a boolean forces whoever hit it to
    /// re-derive the cause by hand.
    /// </summary>
    public IReadOnlyList<string> FailureReasons { get; init; } = [];

    /// <summary>
    /// True when this case is advisory, so <see cref="FailureReasons"/> may be non-empty while
    /// <see cref="Passed"/> stays true. See <see cref="EvidenceBenchmarkCase.Advisory"/>.
    /// </summary>
    public bool Advisory { get; init; }
}

public sealed record EvidenceBenchmarkReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<EvidenceBenchmarkCaseResult> Cases { get; init; }
    public required double MeanFileRecall { get; init; }
    public required double MeanFilePrecision { get; init; }
    public required int TotalApproximateTokens { get; init; }
    public required bool Passed { get; init; }

    /// <summary>
    /// How many advisory cases did not meet their acceptance conditions. This is the size of the
    /// queue of known retrieval gaps; it does not affect <see cref="Passed"/>.
    /// </summary>
    public int AdvisoryFailures { get; init; }
}

public sealed record ApiContractInfo
{
    public required string PackageVersion { get; init; }
    public required int CurrentSchemaVersion { get; init; }
    public required int MinimumReadableSchemaVersion { get; init; }
    public required IReadOnlyList<string> SupportedTargetFrameworks { get; init; }
    public required bool RequiresTrustedRepository { get; init; }
}

public sealed record FileHotspot
{
    public required int Rank { get; init; }
    public required string Path { get; init; }
    public required string Project { get; init; }
    public required int LinesOfCode { get; init; }
    public required int MaximumCyclomaticComplexity { get; init; }
    public required int OutgoingDependencyCount { get; init; }
    public required int IncomingDependencyCount { get; init; }
    public required int DiagnosticCount { get; init; }
    public required int CommitCount { get; init; }
    public required int ContributorCount { get; init; }
    public required long Churn { get; init; }
    public DateTimeOffset? LastModifiedUtc { get; init; }
    public double? LineCoveragePercent { get; init; }
    public required IReadOnlyList<string> SelectionReasons { get; init; }
}

public sealed record CodeDependencyMetric(
    string TargetSymbol,
    string TargetName,
    string Relationship);

public sealed record CodeTypeMetric
{
    public required string SymbolIdentity { get; init; }
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string Project { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
    public required int LinesOfCode { get; init; }
    public required int MethodCount { get; init; }
    public required int PublicMethodCount { get; init; }
    public required int ConstructorCount { get; init; }
    public required int DependencyCount { get; init; }
    public required double AverageMethodComplexity { get; init; }
    public required int MaximumMethodComplexity { get; init; }
    public string? BaseType { get; init; }
    public required IReadOnlyList<string> Interfaces { get; init; }
    public required IReadOnlyList<CodeDependencyMetric> Dependencies { get; init; }
}

public sealed record CodeMethodMetric
{
    public required string SymbolIdentity { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }
    public required string ContainingType { get; init; }
    public required string ReturnType { get; init; }
    public required string Project { get; init; }
    public required string File { get; init; }
    public required int Line { get; init; }
    public required int LinesOfCode { get; init; }
    public required int ParameterCount { get; init; }
    public required int CyclomaticComplexity { get; init; }
    public required bool IsAsync { get; init; }
}

public sealed record RepositoryContextReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string RepositoryRoot { get; init; }
    public string? Branch { get; init; }
    public string? HeadCommit { get; init; }
    public required ContextPurpose Purpose { get; init; }
    public required ContextScope Scope { get; init; }
    public string? Target { get; init; }
    public required IReadOnlyList<string> AnalyzedProjects { get; init; }
    public required IReadOnlyList<string> AnalyzedFiles { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public IReadOnlyList<GitFileChange> Changes { get; init; } = [];
    public GitComparisonState GitComparison { get; init; } = GitComparisonState.Comparable;
    public required IReadOnlyList<DiagnosticRecord> Diagnostics { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> FailingTests { get; init; }
    public required IReadOnlyList<ProjectDependency> ProjectDependencies { get; init; }
    public required IReadOnlyList<SymbolRecord> Symbols { get; init; }
    public IReadOnlyList<TypeDefinitionRecord> TypeDefinitions { get; init; } = [];
    public IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; } = [];
    public required IReadOnlyList<CodeTypeMetric> Types { get; init; }
    public required IReadOnlyList<CodeMethodMetric> Methods { get; init; }
    public required IReadOnlyList<FileHotspot> Hotspots { get; init; }
    public IReadOnlyList<string> AnalysisGaps { get; init; } = [];
    public required string Markdown { get; init; }
    public required int ApproximateTokens { get; init; }
}

public sealed record RepositoryReportArtifact(
    string Path,
    int Characters,
    int ApproximateTokens);

public sealed record RepositoryTrendPoint
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required string ReportPath { get; init; }
    public required ContextPurpose Purpose { get; init; }
    public required ContextScope Scope { get; init; }
    public string? Target { get; init; }
    public required int DiagnosticCount { get; init; }
    public required int FailingTestCount { get; init; }
    public required int HotspotCount { get; init; }
    public required long HotspotChurn { get; init; }
    public required int HotspotsWithCoverage { get; init; }
    public required double? AverageLineCoveragePercent { get; init; }
    public int? DiagnosticDelta { get; init; }
    public int? FailingTestDelta { get; init; }
    public long? HotspotChurnDelta { get; init; }
    public double? AverageLineCoverageDelta { get; init; }
}

public sealed record RepositoryTrendReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public required IReadOnlyList<RepositoryTrendPoint> Points { get; init; }
}

public sealed record EvidenceBlock
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required string Project { get; init; }
    public required string File { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public required string ContentHash { get; init; }
    public required string Text { get; init; }
    public string? SymbolIdentity { get; init; }
    public string? SemanticName { get; init; }
    public required IReadOnlyList<string> SelectionReasons { get; init; }
    public required int ApproximateTokens { get; init; }
    public bool Truncated { get; init; }
}

public sealed record EvidenceRelationship(
    string SourceBlock,
    string TargetBlock,
    string Relationship,
    EvidenceConfidence Confidence)
{
    public string Origin { get; init; } = "unknown";
    public string? TargetFramework { get; init; }
    public string? EvidenceFile { get; init; }
    public int? EvidenceLine { get; init; }
    public int? EvidenceColumn { get; init; }
    public int? EvidenceEndLine { get; init; }
    public int? EvidenceEndColumn { get; init; }
}

public sealed record EvidenceBundle
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string BundleId { get; init; }
    public required string RepositoryInputHash { get; init; }
    public required string Query { get; init; }
    public required IReadOnlyList<EvidenceBlock> Blocks { get; init; }
    public required IReadOnlyList<EvidenceRelationship> Relationships { get; init; }
    public required IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; }
    public required IReadOnlyList<string> AnalysisGaps { get; init; }
    public required EvidenceSufficiency Sufficiency { get; init; }
    public required bool ShouldAbstain { get; init; }
    public required IReadOnlyList<string> SufficiencyReasons { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    public IReadOnlyList<GitFileChange> Changes { get; init; } = [];
    public GitComparisonState GitComparison { get; init; } = GitComparisonState.Comparable;
    public required bool Truncated { get; init; }
    public required int ApproximateTokens { get; init; }
    public required string Prompt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SymbolReferenceRelation
{
    Callers,
    Callees,
    Implementers,
    Implementations,
    Overrides,
    Subtypes,
    ConstructorsOf,
    Readers,
    Writers,
    TestsCovering,
    InjectedInto
}

public sealed record SymbolReferenceQueryOptions
{
    public required string Target { get; init; }
    public SymbolReferenceRelation Relation { get; init; } = SymbolReferenceRelation.Callers;
    public int MaxResults { get; init; } = 50;
    public int MaxTokens { get; init; } = 3000;
}

public sealed record SymbolReferenceMatch
{
    public required SymbolRecord Source { get; init; }
    public required SymbolRecord Target { get; init; }
    public required string Relationship { get; init; }
    public required EvidenceConfidence Confidence { get; init; }
    public required string Origin { get; init; }
    public string? TargetFramework { get; init; }
    public string? EvidenceFile { get; init; }
    public int? EvidenceLine { get; init; }
    public int? EvidenceColumn { get; init; }
    public int? EvidenceEndLine { get; init; }
    public int? EvidenceEndColumn { get; init; }
}

public sealed record SymbolReferenceQueryReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string ReportId { get; init; }
    public required string Query { get; init; }
    public required SymbolReferenceRelation Relation { get; init; }
    public SymbolRecord? ResolvedSymbol { get; init; }
    public required IReadOnlyList<SymbolRecord> AmbiguousSymbols { get; init; }
    public required IReadOnlyList<SymbolReferenceMatch> Matches { get; init; }
    public required IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; }
    public required IReadOnlyList<string> AnalysisGaps { get; init; }
    public required EvidenceSufficiency Sufficiency { get; init; }
    public required bool ShouldAbstain { get; init; }
    public required bool Truncated { get; init; }
    public required int ApproximateTokens { get; init; }
    public required string Markdown { get; init; }
}

public sealed record BaselineManifest
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string BaselineId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required string RepositoryRoot { get; init; }
    public required string? Branch { get; init; }
    public required string? HeadCommit { get; init; }
    public string? CapturedHeadCommit { get; init; }
    public string? DiffBaseReference { get; init; }
    public required bool WorkingTreeDirty { get; init; }
    public required string SdkVersion { get; init; }
    public required IReadOnlyList<StageTiming> Timings { get; init; }
    public string? RepositoryInputHash { get; init; }
    public bool? RepositoryIndexCacheHit { get; init; }
}

public sealed record StageTiming(string Stage, long DurationMilliseconds);

public sealed record GitSnapshot
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string? Branch { get; init; }
    public required string? HeadCommit { get; init; }
    public required IReadOnlyList<GitFileState> Files { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitChangeProvenance
{
    Committed,
    WorkingTree,
    Both
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GitComparisonState
{
    Comparable,
    BaselineDiverged,
    BaselineCommitUnavailable
}

public sealed record GitFileChange(
    string Path,
    GitChangeProvenance Provenance);

public sealed record GitFileState(
    string Path,
    string IndexStatus,
    string WorkingTreeStatus,
    string? ContentSha256);

public sealed record BuildSnapshot
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required ExecutionState State { get; init; }
    public required int? ExitCode { get; init; }
    public required long DurationMilliseconds { get; init; }
    public required string Command { get; init; }
    public required IReadOnlyList<DiagnosticRecord> Diagnostics { get; init; }
    public string? Detail { get; init; }
}

public sealed record DiagnosticRecord(
    string Identity,
    string Tool,
    string Severity,
    string Rule,
    string? File,
    int? Line,
    int? Column,
    string Message,
    string? Project);

public sealed record TestSnapshot
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required ExecutionState State { get; init; }
    public required int Total { get; init; }
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required long DurationMilliseconds { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> Outcomes { get; init; }
    public string? Detail { get; init; }
    public string Mode { get; init; } = "all";
    public bool IsComplete { get; init; } = true;
    public bool RanFullSuiteAfterTargetedTests { get; init; }
    public IReadOnlyList<string> ProjectsExecuted { get; init; } = [];
    public bool CoverageRequested { get; init; }
    public IReadOnlyList<string> CoverageFiles { get; init; } = [];
    public string? CoverageDetail { get; init; }
}

public sealed record TestOutcomeRecord(
    string Identity,
    string Name,
    string? ClassName,
    string Outcome,
    long DurationMilliseconds,
    string? ErrorMessage);

public sealed record AnalysisSnapshot
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<DiagnosticRecord> Diagnostics { get; init; }
    public required ProviderResult DotnetFormat { get; init; }
    public required ProviderResult Qodana { get; init; }
}

public sealed record ProviderResult(
    ExecutionState State,
    long DurationMilliseconds,
    string? Detail);

public sealed record RepositoryIndex
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string? Solution { get; init; }
    public required IReadOnlyList<ProjectRecord> Projects { get; init; }
}

public sealed record ProjectRecord(
    string Name,
    string Path,
    bool IsTestProject,
    IReadOnlyList<string> TargetFrameworks,
    string? Nullable,
    string? LanguageVersion,
    CompilerSettingsRecord CompilerSettings,
    IReadOnlyList<PackageReferenceRecord> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> SourceFiles)
{
    public string? AssemblyName { get; init; }
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];
    public IReadOnlyList<ProjectItemRecord> Items { get; init; } = [];
    public IReadOnlyList<ResolvedReferenceRecord> MetadataReferences { get; init; } = [];
    public IReadOnlyList<string> AnalyzerReferences { get; init; } = [];
    public IReadOnlyList<GlobalUsingRecord> GlobalUsings { get; init; } = [];
    public IReadOnlyList<TargetFrameworkAnalysisRecord> TargetFrameworkAnalyses { get; init; } = [];
    public ExecutionState ReferenceResolutionState { get; init; } = ExecutionState.Skipped;
    public string? ReferenceResolutionDetail { get; init; }
}

public sealed record CompilerSettingsRecord(
    string? OutputType,
    bool TreatWarningsAsErrors,
    string? WarningsAsErrors,
    string? NoWarn,
    string? AnalysisLevel,
    string? DefineConstants,
    bool AllowUnsafe,
    bool Optimize);

public sealed record PackageReferenceRecord(string Name, string? Version);

public sealed record ResolvedReferenceRecord(
    string Path,
    string? Source,
    string? PackageName,
    string? PackageVersion,
    string? FrameworkReference);

public sealed record GlobalUsingRecord(string Name, bool IsStatic, string? Alias);

public sealed record TargetFrameworkAnalysisRecord
{
    public required string TargetFramework { get; init; }
    public IReadOnlyList<ResolvedReferenceRecord> MetadataReferences { get; init; } = [];
    public IReadOnlyList<string> AnalyzerReferences { get; init; } = [];
    public IReadOnlyList<GlobalUsingRecord> GlobalUsings { get; init; } = [];
    public ExecutionState ReferenceResolutionState { get; init; } = ExecutionState.Skipped;
    public string? ReferenceResolutionDetail { get; init; }
}

public sealed record ProjectItemRecord(string ItemType, string Path);

public sealed record SymbolIndex
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<SymbolRecord> Symbols { get; init; }
    public IReadOnlyList<TypeDefinitionRecord> TypeDefinitions { get; init; } = [];
    public IReadOnlyList<CompilationCompletenessRecord> CompilationCompleteness { get; init; } = [];
    public IReadOnlyList<GeneratedSourceRecord> GeneratedSources { get; init; } = [];
}

public sealed record GeneratedSourceRecord(
    string Id,
    string Project,
    string TargetFramework,
    string File,
    string ContentHash,
    string Text,
    int Lines);

public sealed record SymbolRecord(
    string Identity,
    string Kind,
    string Name,
    string? Namespace,
    string? ContainingType,
    string Project,
    string File,
    int Line,
    string? BaseType,
    IReadOnlyList<string> Interfaces)
{
    public string? SemanticName { get; init; }
    public int? EndLine { get; init; }
}

public sealed record SourceLocationRecord(string File, int Line)
{
    public int? EndLine { get; init; }
}

public sealed record AttributeDefinitionRecord(
    string TypeName,
    IReadOnlyList<string> Arguments);

public sealed record TypeParameterDefinitionRecord(
    string Name,
    string Variance,
    IReadOnlyList<string> Constraints);

public sealed record ParameterDefinitionRecord(
    string Name,
    string TypeName,
    string Nullability,
    string RefKind,
    bool IsParams,
    bool IsOptional,
    string? DefaultValue,
    IReadOnlyList<AttributeDefinitionRecord> Attributes);

public sealed record MemberDefinitionRecord(
    string Identity,
    string Kind,
    string Name,
    string SemanticName,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    string? DeclaredType,
    string? Nullability,
    IReadOnlyList<string> Accessors,
    IReadOnlyList<ParameterDefinitionRecord> Parameters,
    IReadOnlyList<TypeParameterDefinitionRecord> TypeParameters,
    IReadOnlyList<AttributeDefinitionRecord> Attributes,
    SourceLocationRecord Location);

public sealed record TypeDefinitionRecord(
    string SymbolIdentity,
    string Kind,
    string Name,
    string FullName,
    string? Namespace,
    string? ContainingType,
    string Project,
    string Accessibility,
    IReadOnlyList<string> Modifiers,
    IReadOnlyList<TypeParameterDefinitionRecord> TypeParameters,
    IReadOnlyList<AttributeDefinitionRecord> Attributes,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<MemberDefinitionRecord> Members,
    IReadOnlyList<SourceLocationRecord> Declarations);

public sealed record CompilationCompletenessRecord
{
    public required string Project { get; init; }
    public required IReadOnlyList<string> TargetFrameworks { get; init; }
    public string? TargetFramework { get; init; }
    public required AnalysisCompletenessState State { get; init; }
    public required ExecutionState ReferenceResolutionState { get; init; }
    public required int ExpectedSourceFiles { get; init; }
    public required int LoadedSourceFiles { get; init; }
    public required int ResolvedMetadataReferences { get; init; }
    public required int FailedMetadataReferences { get; init; }
    public required int AnalyzerReferences { get; init; }
    public required bool GeneratedSourcesIncluded { get; init; }
    public bool SourceGeneratorsExecuted { get; init; }
    public int SourceGeneratorsDiscovered { get; init; }
    public int GeneratedSourceFiles { get; init; }
    public required int CompilationErrors { get; init; }
    public required IReadOnlyList<string> DiagnosticIds { get; init; }
    public IReadOnlyList<CompilationDiagnosticSummary> DiagnosticSummaries { get; init; } = [];
    public required IReadOnlyList<string> Gaps { get; init; }
}

public sealed record CompilationDiagnosticSummary(
    string Id,
    int Count,
    IReadOnlyList<string> Files);

public sealed record DependencyIndex
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<ProjectDependency> Projects { get; init; }
    public required IReadOnlyList<TypeDependency> Types { get; init; }
    public IReadOnlyList<SymbolReference> Symbols { get; init; } = [];
}

public sealed record ProjectDependency(string Project, string ReferencedProject);
public sealed record TypeDependency(string Symbol, string RelatedType, string Relationship);
public sealed record SymbolReference(
    string SourceSymbol,
    string TargetSymbol,
    string Relationship,
    string SourceProject,
    string TargetProject)
{
    public EvidenceConfidence Confidence { get; init; } = EvidenceConfidence.SemanticResolved;
    public string Origin { get; init; } = "unknown";
    public string? TargetFramework { get; init; }
    public string? EvidenceFile { get; init; }
    public int? EvidenceLine { get; init; }
    public int? EvidenceColumn { get; init; }
    public int? EvidenceEndLine { get; init; }
    public int? EvidenceEndColumn { get; init; }
}

public sealed record VerificationReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string BaselineId { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public IReadOnlyList<GitFileChange> Changes { get; init; } = [];
    public GitComparisonState GitComparison { get; init; } = GitComparisonState.Comparable;
    public IReadOnlyList<SymbolRecord> ChangedSymbols { get; init; } = [];
    public required IReadOnlyList<DiagnosticRecord> NewDiagnostics { get; init; }
    public required IReadOnlyList<DiagnosticRecord> ResolvedDiagnostics { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> NewFailingTests { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> ExistingFailingTests { get; init; }
    public required IReadOnlyList<TestOutcomeRecord> ResolvedFailingTests { get; init; }
    public required BuildSnapshot CurrentBuild { get; init; }
    public required TestSnapshot CurrentTests { get; init; }
    public required AnalysisSnapshot CurrentAnalysis { get; init; }
    public required bool HasRegressions { get; init; }
    public bool HasExecutionFailures { get; init; }
}

public sealed record ReferenceReviewReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required string Reference { get; init; }
    public required string BaseCommit { get; init; }
    public required string HeadCommit { get; init; }
    public required DateTimeOffset VerifiedAtUtc { get; init; }
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public required IReadOnlyList<GitFileChange> Changes { get; init; }
    public required IReadOnlyList<string> Projects { get; init; }
    public required IReadOnlyList<SymbolRecord> Symbols { get; init; }
    public required IReadOnlyList<SymbolRecord> ChangedSymbols { get; init; }
    public required IReadOnlyList<string> Tests { get; init; }
    public required IReadOnlyList<string> TestCases { get; init; }
    public required BuildSnapshot CurrentBuild { get; init; }
    public required TestSnapshot CurrentTests { get; init; }
    public required AnalysisSnapshot CurrentAnalysis { get; init; }
    public required bool HasFailures { get; init; }
    public required bool HasExecutionFailures { get; init; }
}

public sealed record StatusReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required BaselineManifest Manifest { get; init; }
    public required GitSnapshot Git { get; init; }
    public required BuildSnapshot Build { get; init; }
    public required TestSnapshot Tests { get; init; }
    public required AnalysisSnapshot Analysis { get; init; }
    public required RepositoryIndex Repository { get; init; }
}

public sealed record AffectedReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required IReadOnlyList<string> ChangedFiles { get; init; }
    public IReadOnlyList<GitFileChange> Changes { get; init; } = [];
    public GitComparisonState GitComparison { get; init; } = GitComparisonState.Comparable;
    public required IReadOnlyList<string> Projects { get; init; }
    public required IReadOnlyList<SymbolRecord> Symbols { get; init; }
    public IReadOnlyList<SymbolRecord> ChangedSymbols { get; init; } = [];
    public required IReadOnlyList<string> Tests { get; init; }
    public IReadOnlyList<string> TestCases { get; init; } = [];
}

public sealed record CleanupReport
{
    public int SchemaVersion { get; init; } = SchemaVersions.Current;
    public required ExecutionState State { get; init; }
    public required string? Command { get; init; }
    public required long DurationMilliseconds { get; init; }
    public required IReadOnlyList<string> ModifiedFiles { get; init; }
    public string? Detail { get; init; }
}
