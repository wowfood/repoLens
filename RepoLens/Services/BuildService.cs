using System.Text.RegularExpressions;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed partial class BuildService(IProcessRunner processRunner)
{
    public async Task<(BuildSnapshot Build, string RawLog)> CaptureAsync(
        string repositoryRoot,
        DevContextConfig config,
        CancellationToken cancellationToken)
    {
        var target = ResolveTarget(repositoryRoot, config.Solution);
        if (target is null)
        {
            return (new BuildSnapshot
            {
                State = ExecutionState.Skipped,
                ExitCode = null,
                DurationMilliseconds = 0,
                Command = "dotnet build",
                Diagnostics = [],
                Detail = "No solution or project file was found."
            }, string.Empty);
        }

        var result = await processRunner.RunAsync(
            "dotnet",
            ["build", target, "--nologo", "--verbosity", "minimal"],
            repositoryRoot,
            cancellationToken);
        var rawLog = JoinOutput(result);
        var diagnostics = ParseDiagnostics(rawLog, repositoryRoot);

        return (new BuildSnapshot
        {
            State = result.State,
            ExitCode = result.ExitCode,
            DurationMilliseconds = result.DurationMilliseconds,
            Command = result.Command,
            Diagnostics = diagnostics,
            Detail = result.State switch
            {
                ExecutionState.Unavailable => "The dotnet command is unavailable.",
                ExecutionState.TimedOut => FirstUsefulLine(result.StandardError, result.StandardOutput),
                ExecutionState.Failed => FirstUsefulLine(result.StandardError, result.StandardOutput),
                _ => null
            }
        }, rawLog);
    }

    internal static IReadOnlyList<DiagnosticRecord> ParseDiagnostics(string output, string repositoryRoot)
    {
        var diagnostics = new Dictionary<string, DiagnosticRecord>(StringComparer.Ordinal);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = DiagnosticPattern().Match(line.Trim());
            if (!match.Success)
            {
                continue;
            }

            var file = NormalizeFile(match.Groups["file"].Value, repositoryRoot);
            var severity = match.Groups["severity"].Value.ToLowerInvariant();
            var rule = match.Groups["rule"].Value;
            var message = match.Groups["message"].Value.Trim();
            var project = match.Groups["project"].Success
                ? NormalizeFile(match.Groups["project"].Value, repositoryRoot)
                : null;
            var identity = Hashing.Text(string.Join('|', "roslyn", severity, rule, file, message));

            diagnostics[identity] = new DiagnosticRecord(
                identity,
                "roslyn",
                severity,
                rule,
                file,
                int.Parse(match.Groups["line"].Value),
                int.Parse(match.Groups["column"].Value),
                message,
                project);
        }

        return diagnostics.Values
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ThenBy(diagnostic => diagnostic.Rule, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string? ResolveTarget(string repositoryRoot, string? configuredSolution)
    {
        if (!string.IsNullOrWhiteSpace(configuredSolution))
        {
            var configuredPath = Path.GetFullPath(Path.Combine(repositoryRoot, configuredSolution));
            if (!File.Exists(configuredPath))
            {
                throw new InvalidOperationException(
                    $"Configured solution does not exist: {configuredSolution}");
            }

            return configuredPath;
        }

        return Directory.EnumerateFiles(repositoryRoot, "*.sln", SearchOption.TopDirectoryOnly)
                   .Concat(Directory.EnumerateFiles(repositoryRoot, "*.slnx", SearchOption.TopDirectoryOnly))
                   .Concat(Directory.EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
                       .Concat(Directory.EnumerateFiles(repositoryRoot, "*.fsproj", SearchOption.AllDirectories))
                       .Concat(Directory.EnumerateFiles(repositoryRoot, "*.vbproj", SearchOption.AllDirectories))
                       .Where(path => !IsGeneratedPath(path)))
                   .Order(StringComparer.OrdinalIgnoreCase)
                   .FirstOrDefault();
    }

    private static bool IsGeneratedPath(string path) =>
        path.Split(Path.DirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj" or ContextPaths.DirectoryName);

    private static string NormalizeFile(string path, string repositoryRoot)
    {
        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            return trimmed.Replace('\\', '/');
        }

        var relative = Path.GetRelativePath(repositoryRoot, trimmed);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? trimmed.Replace('\\', '/')
            : relative.Replace('\\', '/');
    }

    private static string JoinOutput(ProcessResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(line => line.Contains("error", StringComparison.OrdinalIgnoreCase))
        ?? values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault()
        ?? "Command failed without output.";

    [GeneratedRegex(
        @"^(?<file>.+?)\((?<line>\d+),(?<column>\d+)\):\s+(?<severity>warning|error)\s+(?<rule>[^:\s]+):\s+(?<message>.*?)(?:\s+\[(?<project>.+?)\])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiagnosticPattern();
}
