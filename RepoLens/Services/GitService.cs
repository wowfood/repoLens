using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed class GitService(IProcessRunner processRunner)
{
    public async Task<GitSnapshot> CaptureAsync(
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        var branchResult = await processRunner.RunAsync(
            "git",
            ["branch", "--show-current"],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(branchResult);

        var headResult = await processRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", "HEAD"],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(headResult);

        var statusResult = await processRunner.RunAsync(
            "git",
            ["status", "--porcelain=v1", "-z", "--untracked-files=all"],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(statusResult);
        if (statusResult.State == ExecutionState.Failed)
        {
            throw new InvalidOperationException(
                $"Git status failed: {FirstUsefulLine(statusResult.StandardError)}");
        }

        var entries = statusResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<GitFileState>();
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4)
            {
                continue;
            }

            var indexStatus = entry[0].ToString();
            var workingTreeStatus = entry[1].ToString();
            var path = NormalizePath(entry[3..]);

            if (entry[0] is 'R' or 'C' && index + 1 < entries.Length)
            {
                index++;
            }

            if (IsGeneratedToolPath(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            var hash = File.Exists(fullPath)
                ? await Hashing.FileAsync(fullPath, cancellationToken)
                : null;
            files.Add(new GitFileState(path, indexStatus, workingTreeStatus, hash));
        }

        return new GitSnapshot
        {
            Branch = ValueOrNull(branchResult.StandardOutput),
            HeadCommit = headResult.State == ExecutionState.Succeeded
                ? ValueOrNull(headResult.StandardOutput)
                : null,
            Files = files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray()
        };
    }

    public static IReadOnlyList<string> ChangedSince(GitSnapshot baseline, GitSnapshot current)
    {
        var before = baseline.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        var after = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);

        return before.Keys
            .Union(after.Keys, StringComparer.Ordinal)
            .Where(path => !IsGeneratedToolPath(path))
            .Where(path => !before.TryGetValue(path, out var oldState) ||
                           !after.TryGetValue(path, out var newState) ||
                           oldState != newState)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void EnsureGitAvailable(ProcessResult result)
    {
        if (result.State == ExecutionState.Unavailable)
        {
            throw new InvalidOperationException("Git is unavailable. Install Git and ensure it is on PATH.");
        }
    }

    private static string? ValueOrNull(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static bool IsGeneratedToolPath(string path) => path
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Any(segment => segment.Equals(ContextPaths.DirectoryName, StringComparison.OrdinalIgnoreCase)
                        || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                        || segment.Equals(".idea", StringComparison.OrdinalIgnoreCase));

    private static string FirstUsefulLine(string value) =>
        value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown error";
}
