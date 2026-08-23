using System.Text;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed class CleanupService(IProcessRunner processRunner, GitService gitService)
{
    public async Task<CleanupReport> RunAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        if (!config.Cleanup.Enabled)
        {
            return new CleanupReport
            {
                State = ExecutionState.Skipped,
                Command = config.Cleanup.Command,
                DurationMilliseconds = 0,
                ModifiedFiles = [],
                Detail = "Cleanup is disabled in config.json."
            };
        }

        var parts = SplitCommand(config.Cleanup.Command);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("The configured cleanup command is empty.");
        }

        var before = await gitService.CaptureAsync(repositoryRoot, cancellationToken);
        var result = await processRunner.RunAsync(
            parts[0],
            parts.Skip(1).ToArray(),
            repositoryRoot,
            cancellationToken);
        var after = await gitService.CaptureAsync(repositoryRoot, cancellationToken);

        return new CleanupReport
        {
            State = result.State,
            Command = result.Command,
            DurationMilliseconds = result.DurationMilliseconds,
            ModifiedFiles = GitService.ChangedSince(before, after),
            Detail = result.State == ExecutionState.Succeeded
                ? null
                : FirstUsefulLine(result.StandardError, result.StandardOutput)
        };
    }

    internal static IReadOnlyList<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        char quote = default;

        for (var index = 0; index < command.Length; index++)
        {
            var character = command[index];
            if (inQuotes)
            {
                if (character == quote)
                {
                    inQuotes = false;
                }
                else if (character == '\\' && index + 1 < command.Length && command[index + 1] == quote)
                {
                    current.Append(command[++index]);
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '\'' or '"')
            {
                inQuotes = true;
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                AddCurrent(parts, current);
            }
            else
            {
                current.Append(character);
            }
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("The configured cleanup command contains an unmatched quote.");
        }

        AddCurrent(parts, current);
        return parts;
    }

    private static void AddCurrent(ICollection<string> parts, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        parts.Add(current.ToString());
        current.Clear();
    }

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault()
        ?? "Cleanup command failed without output.";
}
