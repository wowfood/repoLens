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
    public static string Reports(string repositoryRoot) => Path.Combine(Root(repositoryRoot), "reports");
}
