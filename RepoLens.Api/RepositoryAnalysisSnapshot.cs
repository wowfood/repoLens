using DevContext.Core;

namespace DevContext;

/// <summary>
/// A deterministic, point-in-time repository capture that does not mutate baseline state.
/// </summary>
public sealed record RepositoryAnalysisSnapshot(
    BaselineManifest Manifest,
    GitSnapshot Git,
    BuildSnapshot Build,
    TestSnapshot Tests,
    AnalysisSnapshot Analysis,
    RepositoryIndex Repository,
    SymbolIndex Symbols,
    DependencyIndex Dependencies,
    AffectedReport? Affected);
