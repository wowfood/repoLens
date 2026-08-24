using System.Text;
using System.Text.RegularExpressions;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record RepositoryFileInventory(
    IReadOnlyList<string> RelativePaths,
    bool GitIgnoreApplied);

/// <summary>
/// Provides one deterministic repository file inventory for all indexing and evidence services.
/// </summary>
internal sealed class RepositoryFileFilter(IProcessRunner processRunner)
{
    internal static IReadOnlyList<string> BuiltInExcludes { get; } =
        [".git/**", ".dev-context/**", ".idea/**", "**/bin/**", "**/obj/**"];

    private static readonly HashSet<string> BuiltInExcludedDirectories = new(
        [".git", ".dev-context", ".idea", "bin", "obj"],
        PathComparer);

    public async Task<RepositoryFileInventory> GetFilesAsync(
        string repositoryRoot,
        IndexingConfig config,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(repositoryRoot);
        IReadOnlyList<string>? candidates = null;
        var gitIgnoreApplied = false;

        if (config.RespectGitignore && HasGitMetadata(root))
        {
            var result = await processRunner.RunAsync(
                "git",
                ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                root,
                cancellationToken);
            if (result.State == ExecutionState.Succeeded)
            {
                candidates = result.StandardOutput
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
                gitIgnoreApplied = true;
            }
        }

        candidates ??= Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        var excludeMatchers = config.Exclude
            .Select(GlobMatcher.Create)
            .ToArray();
        var relativePaths = candidates
            .Select(NormalizeRelativePath)
            .Where(path => path.Length > 0)
            .Where(path => !HasBuiltInExcludedDirectory(path))
            .Where(path => excludeMatchers.All(matcher => !matcher.IsMatch(path)))
            .Where(path => IsExistingFileWithinRoot(root, path))
            .Distinct(PathComparer)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new RepositoryFileInventory(relativePaths, gitIgnoreApplied);
    }

    public static string ToFullPath(string repositoryRoot, string relativePath) =>
        Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static bool HasGitMetadata(string repositoryRoot)
    {
        var gitPath = Path.Combine(repositoryRoot, ".git");
        return Directory.Exists(gitPath) || File.Exists(gitPath);
    }

    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool HasBuiltInExcludedDirectory(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
               && segments[..^1].Any(BuiltInExcludedDirectories.Contains);
    }

    private static bool IsExistingFileWithinRoot(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(ToFullPath(root, relativePath));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison) && File.Exists(fullPath);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class GlobMatcher(Regex regex)
    {
        public bool IsMatch(string relativePath) => regex.IsMatch(relativePath);

        public static GlobMatcher Create(string pattern)
        {
            var normalized = pattern.Trim().Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }
            if (normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "**";
            }

            var hasDirectory = normalized.Contains('/', StringComparison.Ordinal);
            var expression = new StringBuilder("^");
            if (!hasDirectory)
            {
                expression.Append("(?:.*/)?");
            }

            for (var index = 0; index < normalized.Length; index++)
            {
                var character = normalized[index];
                if (character == '*' && index + 1 < normalized.Length && normalized[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < normalized.Length && normalized[index + 1] == '/')
                    {
                        index++;
                        expression.Append("(?:.*/)?");
                    }
                    else
                    {
                        expression.Append(".*");
                    }
                }
                else if (character == '*')
                {
                    expression.Append("[^/]*");
                }
                else if (character == '?')
                {
                    expression.Append("[^/]");
                }
                else
                {
                    expression.Append(Regex.Escape(character.ToString()));
                }
            }

            expression.Append("(?:/.*)?$");
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            if (OperatingSystem.IsWindows())
            {
                options |= RegexOptions.IgnoreCase;
            }

            return new GlobMatcher(new Regex(
                expression.ToString(),
                options,
                TimeSpan.FromSeconds(1)));
        }
    }
}
