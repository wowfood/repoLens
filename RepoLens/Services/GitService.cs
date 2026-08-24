using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record GitChangeSet(
    string? BaseCommit,
    string? HeadCommit,
    GitComparisonState Comparison,
    IReadOnlyList<GitFileChange> Changes)
{
    public IReadOnlyList<string> ChangedFiles => Changes.Select(change => change.Path).ToArray();
    public bool IsComplete => Comparison == GitComparisonState.Comparable;
}

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

    internal static GitChangeSet WorkingTreeChangesSince(GitSnapshot baseline, GitSnapshot current) =>
        CreateChangeSet(
            baseline.HeadCommit,
            current.HeadCommit,
            GitComparisonState.Comparable,
            ChangedSince(baseline, current),
            []);

    public async Task<GitChangeSet> ChangesSinceAsync(
        string repositoryRoot,
        GitSnapshot baseline,
        GitSnapshot current,
        CancellationToken cancellationToken)
    {
        var workingTreeChanges = ChangedSince(baseline, current);
        if (baseline.HeadCommit is null)
        {
            return CreateChangeSet(
                null,
                current.HeadCommit,
                current.HeadCommit is null
                    ? GitComparisonState.Comparable
                    : GitComparisonState.BaselineCommitUnavailable,
                workingTreeChanges,
                []);
        }

        if (current.HeadCommit is null)
        {
            return CreateChangeSet(
                baseline.HeadCommit,
                null,
                GitComparisonState.BaselineDiverged,
                workingTreeChanges,
                []);
        }

        if (baseline.HeadCommit.Equals(current.HeadCommit, StringComparison.Ordinal))
        {
            return CreateChangeSet(
                baseline.HeadCommit,
                current.HeadCommit,
                GitComparisonState.Comparable,
                workingTreeChanges,
                []);
        }

        var ancestor = await processRunner.RunAsync(
            "git",
            ["merge-base", "--is-ancestor", baseline.HeadCommit, current.HeadCommit],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(ancestor);
        if (ancestor.ExitCode == 1)
        {
            return CreateChangeSet(
                baseline.HeadCommit,
                current.HeadCommit,
                GitComparisonState.BaselineDiverged,
                workingTreeChanges,
                []);
        }

        if (ancestor.State != ExecutionState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git could not compare the baseline HEAD with the current HEAD: {FirstUsefulLine(ancestor.StandardError)}");
        }

        var committed = await CommittedChangesAsync(
            repositoryRoot,
            baseline.HeadCommit,
            current.HeadCommit,
            cancellationToken);
        return CreateChangeSet(
            baseline.HeadCommit,
            current.HeadCommit,
            GitComparisonState.Comparable,
            workingTreeChanges,
            committed);
    }

    public async Task<GitChangeSet> ChangesAgainstReferenceAsync(
        string repositoryRoot,
        string reference,
        GitSnapshot current,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        if (reference.StartsWith("-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Git references must not begin with '-'.");
        }

        if (current.HeadCommit is null)
        {
            throw new InvalidOperationException("A committed HEAD is required for reference comparison.");
        }

        var referenceResult = await processRunner.RunAsync(
            "git",
            ["rev-parse", "--verify", "--end-of-options", $"{reference}^{{commit}}"],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(referenceResult);
        if (referenceResult.State != ExecutionState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git reference '{reference}' does not resolve to a commit: {FirstUsefulLine(referenceResult.StandardError)}");
        }

        var resolvedReference = ValueOrNull(referenceResult.StandardOutput)
                                ?? throw new InvalidOperationException($"Git reference '{reference}' resolved to an empty value.");
        var mergeBaseResult = await processRunner.RunAsync(
            "git",
            ["merge-base", resolvedReference, current.HeadCommit],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(mergeBaseResult);
        if (mergeBaseResult.State != ExecutionState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git could not find a merge base for '{reference}' and HEAD: {FirstUsefulLine(mergeBaseResult.StandardError)}");
        }

        var mergeBase = ValueOrNull(mergeBaseResult.StandardOutput)
                        ?? throw new InvalidOperationException($"Git returned no merge base for '{reference}' and HEAD.");
        IReadOnlyList<string> committed = mergeBase.Equals(current.HeadCommit, StringComparison.Ordinal)
            ? []
            : await CommittedChangesAsync(repositoryRoot, mergeBase, current.HeadCommit, cancellationToken);
        return CreateChangeSet(
            mergeBase,
            current.HeadCommit,
            GitComparisonState.Comparable,
            current.Files.Select(file => file.Path),
            committed);
    }

    private async Task<IReadOnlyList<string>> CommittedChangesAsync(
        string repositoryRoot,
        string baseCommit,
        string headCommit,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "git",
            ["diff", "--name-status", "-z", "--find-renames", $"{baseCommit}..{headCommit}", "--"],
            repositoryRoot,
            cancellationToken);
        EnsureGitAvailable(result);
        if (result.State != ExecutionState.Succeeded)
        {
            throw new InvalidOperationException(
                $"Git committed-change detection failed: {FirstUsefulLine(result.StandardError)}");
        }

        return ParseNameStatus(result.StandardOutput)
            .Where(path => !IsGeneratedToolPath(path))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<string> ParseNameStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var index = 0; index < fields.Length; index++)
        {
            var status = fields[index];
            string? path = null;
            var tab = status.IndexOf('\t');
            if (tab >= 0)
            {
                path = status[(tab + 1)..];
                status = status[..tab];
            }
            else if (index + 1 < fields.Length)
            {
                path = fields[++index];
            }

            if (!string.IsNullOrEmpty(path))
            {
                paths.Add(NormalizePath(path));
            }

            if (status.Length > 0
                && status[0] is 'R' or 'C'
                && index + 1 < fields.Length)
            {
                paths.Add(NormalizePath(fields[++index]));
            }
        }

        return paths;
    }

    private static GitChangeSet CreateChangeSet(
        string? baseCommit,
        string? headCommit,
        GitComparisonState comparison,
        IEnumerable<string> workingTreeChanges,
        IEnumerable<string> committedChanges)
    {
        var working = workingTreeChanges.ToHashSet(StringComparer.Ordinal);
        var committed = committedChanges.ToHashSet(StringComparer.Ordinal);
        var changes = working
            .Union(committed, StringComparer.Ordinal)
            .Where(path => !IsGeneratedToolPath(path))
            .Order(StringComparer.Ordinal)
            .Select(path => new GitFileChange(
                path,
                working.Contains(path) && committed.Contains(path)
                    ? GitChangeProvenance.Both
                    : committed.Contains(path)
                        ? GitChangeProvenance.Committed
                        : GitChangeProvenance.WorkingTree))
            .ToArray();
        return new GitChangeSet(baseCommit, headCommit, comparison, changes);
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
