namespace DevContext.Cli;

internal sealed record CliArguments(
    string? Command,
    string? Target,
    string Format,
    string Purpose,
    string Scope,
    string? AnalysisTarget,
    string? CoveragePath,
    string? OutputPath,
    int MaxHotspots,
    int MaxSymbols,
    int MaxTokens,
    int MaxResults,
    int GraphDepth,
    int HistoryMonths,
    int RetainReports,
    bool ChangedOnly,
    bool IncludeTests,
    string? ProjectFilter,
    IReadOnlyList<string> Kinds,
    bool Replace,
    bool Verbose,
    bool Help,
    bool Version)
{
    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        string? command = null;
        string? target = null;
        var format = "text";
        var purpose = "change";
        var scope = "automatic";
        string? analysisTarget = null;
        string? coveragePath = null;
        string? outputPath = null;
        var maxHotspots = 10;
        var maxSymbols = 200;
        var maxTokens = 3000;
        var maxResults = 20;
        var graphDepth = 1;
        var historyMonths = 12;
        var retainReports = 20;
        var changedOnly = false;
        var includeTests = true;
        string? projectFilter = null;
        var kinds = new List<string>();
        var replace = false;
        var verbose = false;
        var help = false;
        var version = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "-h" or "--help":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                case "--replace":
                    replace = true;
                    break;
                case "--changed":
                    changedOnly = true;
                    break;
                case "--exclude-tests":
                    includeTests = false;
                    break;
                case "-v" or "--verbose":
                    verbose = true;
                    break;
                case "--format":
                    if (++index >= args.Count)
                    {
                        throw new CliUsageException("--format requires either 'text' or 'json'.");
                    }

                    format = args[index].ToLowerInvariant();
                    if (format is not ("text" or "json"))
                    {
                        throw new CliUsageException("--format must be either 'text' or 'json'.");
                    }

                    break;
                case "--purpose":
                    purpose = NextValue(args, ref index, "--purpose").ToLowerInvariant();
                    break;
                case "--scope":
                    scope = NextValue(args, ref index, "--scope").ToLowerInvariant();
                    break;
                case "--target":
                    analysisTarget = NextValue(args, ref index, "--target");
                    break;
                case "--coverage":
                    coveragePath = NextValue(args, ref index, "--coverage");
                    break;
                case "--output":
                    outputPath = NextValue(args, ref index, "--output");
                    break;
                case "--project":
                    projectFilter = NextValue(args, ref index, "--project");
                    break;
                case "--kind":
                    kinds.AddRange(NextValue(args, ref index, "--kind")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--max-hotspots":
                    maxHotspots = PositiveInteger(NextValue(args, ref index, "--max-hotspots"), "--max-hotspots");
                    break;
                case "--max-symbols":
                    maxSymbols = PositiveInteger(NextValue(args, ref index, "--max-symbols"), "--max-symbols");
                    break;
                case "--max-tokens":
                    maxTokens = IntegerInRange(
                        NextValue(args, ref index, "--max-tokens"),
                        "--max-tokens",
                        256,
                        int.MaxValue);
                    break;
                case "--max-results":
                    maxResults = PositiveInteger(NextValue(args, ref index, "--max-results"), "--max-results");
                    break;
                case "--graph-depth":
                    graphDepth = IntegerInRange(
                        NextValue(args, ref index, "--graph-depth"),
                        "--graph-depth",
                        0,
                        3);
                    break;
                case "--history-months":
                    historyMonths = PositiveInteger(NextValue(args, ref index, "--history-months"), "--history-months");
                    break;
                case "--retain":
                    retainReports = PositiveInteger(NextValue(args, ref index, "--retain"), "--retain");
                    break;
                default:
                    if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new CliUsageException($"Unknown option: {argument}");
                    }

                    if (command is not null && target is not null)
                    {
                        throw new CliUsageException($"Unexpected argument: {argument}");
                    }

                    if (command is null)
                    {
                        command = argument.ToLowerInvariant();
                    }
                    else
                    {
                        target = argument;
                    }
                    break;
            }
        }

        return new CliArguments(
            command,
            target,
            format,
            purpose,
            scope,
            analysisTarget,
            coveragePath,
            outputPath,
            maxHotspots,
            maxSymbols,
            maxTokens,
            maxResults,
            graphDepth,
            historyMonths,
            retainReports,
            changedOnly,
            includeTests,
            projectFilter,
            kinds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            replace,
            verbose,
            help,
            version);
    }

    private static string NextValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count)
        {
            throw new CliUsageException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int PositiveInteger(string value, string option) =>
        int.TryParse(value, out var parsed) && parsed > 0
            ? parsed
            : throw new CliUsageException($"{option} requires a positive integer.");

    private static int IntegerInRange(string value, string option, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum
            ? parsed
            : throw new CliUsageException($"{option} requires an integer from {minimum} to {maximum}.");
}

internal sealed class CliUsageException(string message) : Exception(message);
