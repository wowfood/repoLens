namespace DevContext.Infrastructure;

internal static class ContextPaths
{
    public const string DirectoryName = ".dev-context";

    public static string Root(string repositoryRoot) => Path.Combine(repositoryRoot, DirectoryName);
    public static string Config(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "config.json");
    public static string Baseline(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "baseline");
    public static string Current(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "current");
    public static string Indexes(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "indexes");
    public static string Summary(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "summary.md");
    public static string Runs(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "runs");
    public static string Cache(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "cache");

    /// <summary>
    /// Content hashes keyed by size and modification time, so a warm call re-reads only what changed.
    /// It sits beside the cache rather than inside it because the cache directory is swapped
    /// wholesale on publish, and this table is worth keeping across that swap.
    /// </summary>
    public static string Fingerprints(string repositoryRoot) =>
        Path.Combine(Root(repositoryRoot), "fingerprints.json");

    /// <summary>
    /// Held while the graph cache directory is swapped, so two dev-context processes analyzing the
    /// same repository cannot delete and rename it underneath one another.
    /// </summary>
    public static string CacheLock(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "cache.lock");
    public static string Reports(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "reports");
}
