using System.Text.Json;
using DevContext.Configuration;
using DevContext.Core;
using DevContext.Infrastructure;

namespace DevContext.Services;

internal sealed record AnalysisProviderCapture(
    ProviderResult Provider,
    IReadOnlyList<DiagnosticRecord> Diagnostics);

internal sealed class AnalysisService(IProcessRunner processRunner)
{
    public async Task<(AnalysisSnapshot Analysis, string RawLog)> CaptureAsync(
        string repositoryRoot,
        DevContextConfig config,
        BuildSnapshot build,
        string runId,
        CancellationToken cancellationToken)
    {
        var logs = new List<string>();
        var resultRoot = Path.Combine(ContextPaths.Runs(repositoryRoot), runId, "analysis");
        if (Directory.Exists(resultRoot))
        {
            Directory.Delete(resultRoot, true);
        }
        Directory.CreateDirectory(resultRoot);

        try
        {
            var format = await CaptureFormatAsync(
                repositoryRoot,
                config,
                resultRoot,
                logs,
                cancellationToken);
            var qodana = await CaptureQodanaAsync(
                repositoryRoot,
                config,
                resultRoot,
                logs,
                cancellationToken);
            var diagnostics = (config.Analysis.Roslyn ? build.Diagnostics : [])
                .Concat(format.Diagnostics)
                .Concat(qodana.Diagnostics)
                .DistinctBy(diagnostic => diagnostic.Identity, StringComparer.Ordinal)
                .OrderBy(diagnostic => diagnostic.Tool, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Line)
                .ThenBy(diagnostic => diagnostic.Rule, StringComparer.Ordinal)
                .ToArray();

            return (new AnalysisSnapshot
            {
                Diagnostics = diagnostics,
                DotnetFormat = format.Provider,
                Qodana = qodana.Provider
            }, string.Join(Environment.NewLine, logs.Where(value => !string.IsNullOrWhiteSpace(value))));
        }
        finally
        {
            if (!config.Storage.RetainRawLogs && Directory.Exists(resultRoot))
            {
                Directory.Delete(resultRoot, true);
            }
        }
    }

    private async Task<AnalysisProviderCapture> CaptureFormatAsync(
        string repositoryRoot,
        DevContextConfig config,
        string resultRoot,
        ICollection<string> logs,
        CancellationToken cancellationToken)
    {
        if (!config.Analysis.DotnetFormat)
        {
            return Skipped("dotnet format verification is disabled.");
        }

        var target = BuildService.ResolveTarget(repositoryRoot, config.Solution);
        if (target is null)
        {
            return Skipped("No solution or project was found.");
        }

        var reportDirectory = Path.Combine(resultRoot, "dotnet-format");
        Directory.CreateDirectory(reportDirectory);
        var result = await processRunner.RunAsync(
            "dotnet",
            [
                "format",
                target,
                "--verify-no-changes",
                "--no-restore",
                "--verbosity",
                "minimal",
                "--report",
                reportDirectory
            ],
            repositoryRoot,
            cancellationToken);
        logs.Add(result.Command);
        logs.Add(result.StandardOutput);
        logs.Add(result.StandardError);
        var reportPath = Directory.EnumerateFiles(reportDirectory, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault();
        var diagnostics = reportPath is null
            ? []
            : ParseFormatReport(reportPath, repositoryRoot);
        var providerState = result.State == ExecutionState.Failed && diagnostics.Count > 0
            ? ExecutionState.Succeeded
            : result.State;
        var detail = diagnostics.Count > 0
            ? $"{diagnostics.Count} formatting finding(s)."
            : result.State == ExecutionState.Succeeded
                ? null
                : FirstUsefulLine(result.StandardError, result.StandardOutput);

        return new AnalysisProviderCapture(
            new ProviderResult(providerState, result.DurationMilliseconds, detail),
            diagnostics);
    }

    private async Task<AnalysisProviderCapture> CaptureQodanaAsync(
        string repositoryRoot,
        DevContextConfig config,
        string resultRoot,
        ICollection<string> logs,
        CancellationToken cancellationToken)
    {
        if (!config.Analysis.Qodana)
        {
            return Skipped("Qodana is disabled.");
        }

        var resultDirectory = Path.Combine(resultRoot, "qodana");
        Directory.CreateDirectory(resultDirectory);
        var result = await processRunner.RunAsync(
            config.Analysis.QodanaCommand,
            ["scan", "--results-dir", resultDirectory],
            repositoryRoot,
            cancellationToken);
        logs.Add(result.Command);
        logs.Add(result.StandardOutput);
        logs.Add(result.StandardError);
        var diagnostics = Directory.EnumerateFiles(resultDirectory, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".sarif", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".sarif.json", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => ParseSarif(path, repositoryRoot, "qodana"))
            .DistinctBy(diagnostic => diagnostic.Identity, StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ToArray();
        var providerState = result.State == ExecutionState.Failed && diagnostics.Length > 0
            ? ExecutionState.Succeeded
            : result.State;
        var detail = diagnostics.Length > 0
            ? $"{diagnostics.Length} Qodana finding(s)."
            : result.State == ExecutionState.Succeeded
                ? null
                : FirstUsefulLine(result.StandardError, result.StandardOutput);

        return new AnalysisProviderCapture(
            new ProviderResult(providerState, result.DurationMilliseconds, detail),
            diagnostics);
    }

    internal static IReadOnlyList<DiagnosticRecord> ParseFormatReport(
        string path,
        string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var diagnostics = new List<DiagnosticRecord>();
        foreach (var file in document.RootElement.EnumerateArray())
        {
            var filePath = GetString(file, "FilePath") ?? GetString(file, "FileName");
            if (!TryGetProperty(file, "FileChanges", out var changes)
                || changes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var change in changes.EnumerateArray())
            {
                var rule = GetString(change, "DiagnosticId") ?? "FORMAT";
                var message = GetString(change, "FormatDescription") ?? "Formatting change required.";
                var normalizedFile = NormalizeFile(filePath, repositoryRoot);
                var line = GetInt32(change, "LineNumber");
                var column = GetInt32(change, "CharNumber");
                var identity = Hashing.Text(string.Join('|',
                    "dotnet-format",
                    "warning",
                    rule,
                    normalizedFile,
                    message));
                diagnostics.Add(new DiagnosticRecord(
                    identity,
                    "dotnet-format",
                    "warning",
                    rule,
                    normalizedFile,
                    line,
                    column,
                    message,
                    null));
            }
        }

        return diagnostics
            .DistinctBy(diagnostic => diagnostic.Identity, StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Line)
            .ToArray();
    }

    internal static IReadOnlyList<DiagnosticRecord> ParseSarif(
        string path,
        string repositoryRoot,
        string provider)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!TryGetProperty(document.RootElement, "runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var diagnostics = new List<DiagnosticRecord>();
        foreach (var run in runs.EnumerateArray())
        {
            if (!TryGetProperty(run, "results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var result in results.EnumerateArray())
            {
                var rule = GetString(result, "ruleId") ?? "QODANA";
                var severity = NormalizeSarifLevel(GetString(result, "level"));
                var message = ReadSarifMessage(result);
                ReadSarifLocation(result, repositoryRoot, out var file, out var line, out var column);
                var identity = Hashing.Text(string.Join('|', provider, severity, rule, file, message));
                diagnostics.Add(new DiagnosticRecord(
                    identity,
                    provider,
                    severity,
                    rule,
                    file,
                    line,
                    column,
                    message,
                    null));
            }
        }

        return diagnostics;
    }

    private static string ReadSarifMessage(JsonElement result)
    {
        if (!TryGetProperty(result, "message", out var message))
        {
            return "Analysis finding.";
        }

        return GetString(message, "text")
               ?? GetString(message, "markdown")
               ?? "Analysis finding.";
    }

    private static void ReadSarifLocation(
        JsonElement result,
        string repositoryRoot,
        out string? file,
        out int? line,
        out int? column)
    {
        file = null;
        line = null;
        column = null;
        if (!TryGetProperty(result, "locations", out var locations)
            || locations.ValueKind != JsonValueKind.Array
            || locations.GetArrayLength() == 0)
        {
            return;
        }

        var location = locations[0];
        if (!TryGetProperty(location, "physicalLocation", out var physical))
        {
            return;
        }

        if (TryGetProperty(physical, "artifactLocation", out var artifact))
        {
            file = NormalizeFile(GetString(artifact, "uri"), repositoryRoot);
        }

        if (TryGetProperty(physical, "region", out var region))
        {
            line = GetInt32(region, "startLine");
            column = GetInt32(region, "startColumn");
        }
    }

    private static string NormalizeSarifLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "error" => "error",
        "warning" => "warning",
        "note" or "none" => "info",
        _ => "warning"
    };

    private static string? NormalizeFile(string? path, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var decoded = Uri.UnescapeDataString(path);
        if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            decoded = uri.LocalPath;
        }
        decoded = decoded.Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(decoded))
        {
            decoded = Path.GetFullPath(Path.Combine(repositoryRoot, decoded));
        }

        var relative = Path.GetRelativePath(repositoryRoot, decoded);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? decoded.Replace('\\', '/')
            : relative.Replace('\\', '/');
    }

    private static AnalysisProviderCapture Skipped(string detail) => new(
        new ProviderResult(ExecutionState.Skipped, 0, detail),
        []);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault()
        ?? "Provider failed without output.";
}
