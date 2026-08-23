using System.Reflection;
using System.Text.Json;
using DevContext;
using DevContext.Core;
using DevContext.Infrastructure;
using DevContext.Services;

namespace DevContext.Cli;

internal static class DevContextApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var arguments = CliArguments.Parse(args);
            if (arguments.Version)
            {
                Console.WriteLine(GetVersion());
                return ExitCodes.Success;
            }

            if (arguments.Help || arguments.Command is null)
            {
                PrintHelp();
                return ExitCodes.Success;
            }

            if (arguments.Command is not ("baseline" or "status" or "verify" or "affected" or "doctor" or "explain" or "context" or "query" or "benchmark" or "report" or "clean" or "reset"))
            {
                throw new CliUsageException($"Unknown command: {arguments.Command}");
            }

            if (arguments.Command == "explain" && string.IsNullOrWhiteSpace(arguments.Target))
            {
                throw new CliUsageException("explain requires a repository-relative or absolute path.");
            }

            if (arguments.Command == "query" && string.IsNullOrWhiteSpace(arguments.Target))
            {
                throw new CliUsageException("query requires a quoted task or question.");
            }

            if (arguments.Command == "benchmark" && string.IsNullOrWhiteSpace(arguments.Target))
            {
                throw new CliUsageException("benchmark requires the path to a JSON benchmark corpus.");
            }

            if (arguments.Command is not ("explain" or "context" or "query" or "benchmark" or "report") && arguments.Target is not null)
            {
                throw new CliUsageException($"Unexpected argument: {arguments.Target}");
            }

            if (arguments.Command is "context" or "report"
                && arguments.Target is not null
                && !TryPurpose(arguments.Target, out _))
            {
                throw new CliUsageException(
                    $"Unexpected context purpose '{arguments.Target}'. Use change, architecture, build, or risk.");
            }

            var api = await DevContextApi.OpenAsync(
                Directory.GetCurrentDirectory(),
                cancellationToken: cancellation.Token);
            Log(arguments, $"repository root: {api.RepositoryRoot}");
            Log(arguments, $"command: {arguments.Command}");

            return arguments.Command switch
            {
                "baseline" => await BaselineAsync(api, arguments, cancellation.Token),
                "status" => await StatusAsync(api, arguments, cancellation.Token),
                "verify" => await VerifyAsync(api, arguments, cancellation.Token),
                "affected" => await AffectedAsync(api, arguments, cancellation.Token),
                "doctor" => await DoctorAsync(api, arguments, cancellation.Token),
                "explain" => await ExplainAsync(api, arguments, cancellation.Token),
                "context" => await ContextAsync(api, arguments, cancellation.Token),
                "query" => await QueryAsync(api, arguments, cancellation.Token),
                "benchmark" => await BenchmarkAsync(api, arguments, cancellation.Token),
                "report" => await ReportAsync(api, arguments, cancellation.Token),
                "clean" => await CleanAsync(api, arguments, cancellation.Token),
                "reset" => Reset(api, arguments),
                _ => ExitCodes.UsageOrFailure
            };
        }
        catch (CliUsageException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine("Run 'dev-context --help' for usage.");
            return ExitCodes.UsageOrFailure;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("error: operation cancelled.");
            return ExitCodes.UsageOrFailure;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return ExitCodes.UsageOrFailure;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> BaselineAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var status = await api.BaselineAsync(arguments.Replace, cancellationToken);
        foreach (var timing in status.Manifest.Timings)
        {
            Log(arguments, $"{timing.Stage}: {timing.DurationMilliseconds} ms");
        }

        Write(arguments, status, OutputFormatter.FormatStatus(status));
        return ExitCodes.Success;
    }

    private static async Task<int> StatusAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var status = await api.StatusAsync(cancellationToken);
        Write(arguments, status, OutputFormatter.FormatStatus(status));
        return ExitCodes.Success;
    }

    private static async Task<int> VerifyAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.VerifyAsync(cancellationToken);
        Write(arguments, report, OutputFormatter.FormatVerification(report));
        if (report.HasExecutionFailures)
        {
            return ExitCodes.UsageOrFailure;
        }

        return report.HasRegressions ? ExitCodes.Regression : ExitCodes.Success;
    }

    private static async Task<int> AffectedAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.AffectedAsync(cancellationToken);
        Write(arguments, report, OutputFormatter.FormatAffected(report));
        return ExitCodes.Success;
    }

    private static async Task<int> CleanAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.CleanAsync(cancellationToken);
        Write(arguments, report, OutputFormatter.FormatCleanup(report));
        return report.State is ExecutionState.Failed or ExecutionState.Unavailable
            ? ExitCodes.UsageOrFailure
            : ExitCodes.Success;
    }

    private static async Task<int> DoctorAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.DoctorAsync(cancellationToken);
        Write(arguments, report, OutputFormatter.FormatDoctor(report));
        return report.IsHealthy ? ExitCodes.Success : ExitCodes.UsageOrFailure;
    }

    private static async Task<int> ExplainAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.ExplainAsync(arguments.Target!, cancellationToken);
        Write(arguments, report, OutputFormatter.FormatOwnership(report));
        return ExitCodes.Success;
    }

    private static async Task<int> ContextAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.ContextAsync(ContextOptions(arguments), cancellationToken);
        Write(arguments, report, report.Markdown);
        return ExitCodes.Success;
    }

    private static async Task<int> QueryAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var bundle = await api.QueryAsync(
            new EvidenceQueryOptions
            {
                Query = arguments.Target!,
                MaxTokens = arguments.MaxTokens,
                MaxResults = arguments.MaxResults,
                GraphDepth = arguments.GraphDepth
                ,
                ChangedOnly = arguments.ChangedOnly
                ,
                IncludeTests = arguments.IncludeTests
                ,
                Project = arguments.ProjectFilter
                ,
                Kinds = arguments.Kinds
            },
            cancellationToken);
        Write(arguments, bundle, bundle.Prompt);
        return ExitCodes.Success;
    }

    private static async Task<int> BenchmarkAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(arguments.Target!, api.RepositoryRoot);
        await using var stream = File.OpenRead(path);
        var cases = await JsonSerializer.DeserializeAsync<IReadOnlyList<EvidenceBenchmarkCase>>(
                        stream,
                        JsonDefaults.Options,
                        cancellationToken)
                    ?? throw new JsonException("The benchmark corpus must contain a JSON array of cases.");
        var report = await api.BenchmarkAsync(cases, cancellationToken);
        var text = $"Evidence benchmark{Environment.NewLine}" +
                   $"  Cases: {report.Cases.Count}{Environment.NewLine}" +
                   $"  Recall: {report.MeanFileRecall:P1}{Environment.NewLine}" +
                   $"  Precision: {report.MeanFilePrecision:P1}{Environment.NewLine}" +
                   $"  Approximate tokens: {report.TotalApproximateTokens:N0}{Environment.NewLine}" +
                   $"  Result: {(report.Passed ? "PASSED" : "FAILED")}{Environment.NewLine}" +
                   string.Join(Environment.NewLine, report.Cases.Select(result =>
                       $"  - {result.Name}: {(result.Passed ? "PASS" : "FAIL")}, " +
                       $"recall {result.FileRecall:P0}, precision {result.FilePrecision:P0}, " +
                       $"evidence {result.Sufficiency}, abstain {(result.ShouldAbstain ? "yes" : "no")}, " +
                       $"tokens {result.ApproximateTokens:N0}, cold/warm {result.ColdMilliseconds}/{result.WarmMilliseconds} ms")) +
                   Environment.NewLine;
        Write(arguments, report, text);
        return report.Passed ? ExitCodes.Success : ExitCodes.Regression;
    }

    private static async Task<int> ReportAsync(
        DevContextApi api,
        CliArguments arguments,
        CancellationToken cancellationToken)
    {
        var report = await api.ContextAsync(ContextOptions(arguments), cancellationToken);
        var artifact = await api.SaveReportAsync(
            report,
            arguments.OutputPath,
            arguments.RetainReports,
            cancellationToken);
        Write(
            arguments,
            artifact,
            $"Report: {artifact.Path}{Environment.NewLine}" +
            $"Characters: {artifact.Characters:N0}{Environment.NewLine}" +
            $"Approximate tokens: {artifact.ApproximateTokens:N0}{Environment.NewLine}");
        return ExitCodes.Success;
    }

    private static RepositoryContextOptions ContextOptions(CliArguments arguments)
    {
        var purposeText = arguments.Target ?? arguments.Purpose;
        if (!TryPurpose(purposeText, out var purpose))
        {
            throw new CliUsageException(
                $"Unknown context purpose '{purposeText}'. Use change, architecture, build, or risk.");
        }

        if (!Enum.TryParse<ContextScope>(arguments.Scope, true, out var scope))
        {
            scope = arguments.Scope switch
            {
                "full" => ContextScope.FullRepository,
                "changed" => ContextScope.ChangedFiles,
                _ => throw new CliUsageException(
                    $"Unknown context scope '{arguments.Scope}'. Use automatic, full, changed, project, or path.")
            };
        }

        return new RepositoryContextOptions
        {
            Purpose = purpose,
            Scope = scope,
            Target = arguments.AnalysisTarget,
            CoberturaPath = arguments.CoveragePath,
            MaxHotspots = arguments.MaxHotspots,
            MaxSymbols = arguments.MaxSymbols,
            GitHistoryMonths = arguments.HistoryMonths
        };
    }

    private static bool TryPurpose(string value, out ContextPurpose purpose) =>
        Enum.TryParse(value, true, out purpose);

    private static int Reset(DevContextApi api, CliArguments arguments)
    {
        api.Reset();
        var result = new { schemaVersion = SchemaVersions.Current, reset = true, configurationRetained = true };
        Write(arguments, result, "Generated baseline, current results, indexes, cache, logs, and summary were deleted.\n" +
                                 "Configuration was retained.\n");
        return ExitCodes.Success;
    }

    private static void Write<T>(CliArguments arguments, T value, string text)
    {
        Console.Write(arguments.Format == "json"
            ? JsonSerializer.Serialize(value, JsonDefaults.Options) + Environment.NewLine
            : text);
    }

    private static string GetVersion() =>
        typeof(DevContextApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "1.0.0";

    private static void PrintHelp() => Console.WriteLine(
        """
        dev-context - deterministic repository baselines and verification

        Usage:
          dev-context <command> [options]

        Commands:
          baseline   Capture immutable Git, build, test, analysis, and repository state
          status     Print the concise stored baseline summary
          verify     Re-run checks and report only deltas from the baseline
          affected   Identify projects, symbols, and tests affected by current changes
          doctor     Check SDK, NuGet, solution, project discovery, and optional providers
          explain    Explain project ownership for a path without requiring a baseline
          context    Render bounded change, architecture, build, or risk context
          query      Retrieve source evidence for a quoted task within a token budget
          benchmark  Measure retrieval quality, determinism, token use, and latency
          report     Save context as a retained Markdown report
          clean      Run only explicitly configured deterministic cleanup
          reset      Delete generated context data while retaining configuration

        Explain usage:
          dev-context explain <path> [--format text|json]

        Context usage:
          dev-context context [change|architecture|build|risk] [context options]
          dev-context report [change|architecture|build|risk] [context options]

        Query usage:
          dev-context query "<task or question>" [--max-tokens <n>] [--max-results <n>] [query options]
          dev-context benchmark <corpus.json> [--format text|json]

        Options:
          --format text|json   Select human/LLM text or machine-readable JSON
          --replace            Replace an existing baseline (baseline command only)
          --purpose <purpose>  Alternative to the context purpose operand
          --scope <scope>      automatic, full, changed, project, or path
          --target <path>      Required by project/path scopes
          --coverage <path>    Optional Cobertura XML used by hotspot ranking
          --max-hotspots <n>   Bound hotspot output (default: 10)
          --max-symbols <n>    Bound declaration output (default: 200)
          --max-tokens <n>     Bound query prompt tokens, minimum 256 (default: 3000)
          --max-results <n>    Bound query evidence blocks (default: 20)
          --graph-depth <n>    Follow semantic relationships 0-3 hops (default: 1)
          --changed            Seed query evidence only from files changed since baseline
          --exclude-tests      Exclude symbols declared in test projects from query seeds
          --project <name>     Restrict query seeds to a project name or path
          --kind <kind,...>    Restrict query seeds by declaration kind; repeatable
          --history-months <n> Bound Git churn history (default: 12)
          --output <path>      Report destination (default: .dev-context/reports)
          --retain <n>         Default report-history retention (default: 20)
          -v, --verbose        Write stage and repository details to standard error
          --version            Print the application version
          -h, --help           Print this help
        """);

    private static void Log(CliArguments arguments, string message)
    {
        if (arguments.Verbose)
        {
            Console.Error.WriteLine($"dev-context: {message}");
        }
    }

}
