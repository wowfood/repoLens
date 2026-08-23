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

internal sealed class ProcessRunner : IProcessRunner
{
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

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between cancellation and the kill request.
            }

            throw;
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

    private static string FormatCommand(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) =>
        value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
}
