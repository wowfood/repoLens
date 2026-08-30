using System.ComponentModel;
using System.Diagnostics;
using DevContext.Core;

namespace DevContext.Infrastructure;

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed record ProcessResult(
    ExecutionState State,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    long DurationMilliseconds,
    string Command);

internal sealed class ProcessRunner(TimeSpan? timeout = null) : IProcessRunner
{
    /// <summary>
    /// Matches <see cref="DevContext.Configuration.ExecutionConfig.ProcessTimeoutSeconds"/>, and
    /// applies to callers that construct a runner without configuration — chiefly tests.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(900);

    private readonly TimeSpan timeout = timeout ?? DefaultTimeout;

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var command = FormatCommand(executable, arguments);
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            process.StandardInput.Close();
        }
        catch (Win32Exception exception)
        {
            stopwatch.Stop();
            return new ProcessResult(
                ExecutionState.Unavailable,
                null,
                string.Empty,
                exception.Message,
                stopwatch.ElapsedMilliseconds,
                command);
        }

        // Deliberately not given a cancellation token: killing the process closes its streams, so
        // these complete on their own, and a cancelled read would discard output already produced by
        // a run that timed out — which is the output most worth reporting.
        var standardOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Terminate(process);

            // The caller asking to stop is not the same event as the command overrunning: the first
            // is the user's decision and propagates, the second is a result the run has to report.
            cancellationToken.ThrowIfCancellationRequested();

            stopwatch.Stop();
            var timedOutError = await DrainAsync(standardError);
            return new ProcessResult(
                ExecutionState.TimedOut,
                null,
                await DrainAsync(standardOutput),
                string.IsNullOrWhiteSpace(timedOutError)
                    ? TimeoutDetail(timeout)
                    : $"{timedOutError.TrimEnd()}{Environment.NewLine}{TimeoutDetail(timeout)}",
                stopwatch.ElapsedMilliseconds,
                command);
        }

        stopwatch.Stop();
        return new ProcessResult(
            process.ExitCode == 0 ? ExecutionState.Succeeded : ExecutionState.Failed,
            process.ExitCode,
            await standardOutput,
            await standardError,
            stopwatch.ElapsedMilliseconds,
            command);
    }

    private static void Terminate(Process process)
    {
        try
        {
            process.Kill(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            // The process exited between the deadline firing and the kill request.
        }
    }

    /// <summary>
    /// Reads whatever a stream produced before its process was killed. The wait is bounded because a
    /// timeout handler that can itself hang would defeat the purpose of having a timeout.
    /// </summary>
    private static async Task<string> DrainAsync(Task<string> read)
    {
        try
        {
            return await read.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static string TimeoutDetail(TimeSpan timeout) =>
        $"The command exceeded its {timeout.TotalSeconds:N0}s timeout and was terminated. "
        + "Raise execution.processTimeoutSeconds if this command is legitimately slower.";

    private static string FormatCommand(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
