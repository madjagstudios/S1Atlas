using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using S1Atlas.Core.Tools;

namespace S1Atlas.Extraction.Tools;

internal sealed class ToolProbeRunner
{
    internal const int MaximumRetainedBytesPerStream = 1024 * 1024;

    private static readonly UTF8Encoding Utf8WithReplacement = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public async Task<ToolProbeResult> RunAsync(
        string executablePath,
        string workingDirectory,
        ToolProbeDefinition probe,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(probe);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = CreateStartInfo(executablePath, workingDirectory, probe)
        };

        try
        {
            if (!process.Start())
            {
                return StartFailure(probe, "The capability probe process did not start.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception)
        {
            return StartFailure(
                probe,
                $"The capability probe process could not start: {exception.Message}");
        }

        var standardOutputTask = DrainAsync(process.StandardOutput.BaseStream);
        var standardErrorTask = DrainAsync(process.StandardError.BaseStream);
        using var timeout = new CancellationTokenSource(probe.Timeout);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            await process.WaitForExitAsync(waitCancellation.Token);
        }
        catch (OperationCanceledException) when (waitCancellation.IsCancellationRequested)
        {
            await TerminateProcessTreeAsync(process);
            var canceledOutput = await standardOutputTask;
            var canceledError = await standardErrorTask;

            if (cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new ToolProbeResult(
                probe.ProbeId,
                Succeeded: false,
                ExitCode: null,
                TimedOut: true,
                canceledOutput.Truncated,
                canceledError.Truncated,
                FailureCode: "ToolProbeTimedOut",
                FailureMessage:
                    $"Capability probe '{probe.ProbeId}' exceeded its timeout.");
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        var exitCode = process.ExitCode;

        if (!probe.AcceptedExitCodes.Contains(exitCode))
        {
            return new ToolProbeResult(
                probe.ProbeId,
                Succeeded: false,
                exitCode,
                TimedOut: false,
                standardOutput.Truncated,
                standardError.Truncated,
                FailureCode: "ToolProbeExitCodeRejected",
                FailureMessage:
                    $"Capability probe '{probe.ProbeId}' returned rejected exit code {exitCode}.");
        }

        var combinedOutput = string.Concat(
            standardOutput.Text,
            Environment.NewLine,
            standardError.Text);
        var missingOutput = probe.RequiredOutputSubstrings.FirstOrDefault(
            required => !combinedOutput.Contains(required, StringComparison.Ordinal));
        if (missingOutput is not null)
        {
            return new ToolProbeResult(
                probe.ProbeId,
                Succeeded: false,
                exitCode,
                TimedOut: false,
                standardOutput.Truncated,
                standardError.Truncated,
                FailureCode: "ToolProbeOutputMissing",
                FailureMessage:
                    $"Capability probe '{probe.ProbeId}' did not report required output '{missingOutput}'.");
        }

        return new ToolProbeResult(
            probe.ProbeId,
            Succeeded: true,
            exitCode,
            TimedOut: false,
            standardOutput.Truncated,
            standardError.Truncated,
            FailureCode: null,
            FailureMessage: null);
    }

    private static ProcessStartInfo CreateStartInfo(
        string executablePath,
        string workingDirectory,
        ToolProbeDefinition probe)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8WithReplacement,
            StandardErrorEncoding = Utf8WithReplacement
        };
        startInfo.Environment["NO_COLOR"] = "true";
        foreach (var argument in probe.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<BoundedCapture> DrainAsync(Stream stream)
    {
        var buffer = new byte[16 * 1024];
        using var retained = new MemoryStream(MaximumRetainedBytesPerStream);
        var truncated = false;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer);
            if (bytesRead == 0)
            {
                break;
            }

            var remaining = MaximumRetainedBytesPerStream - (int)retained.Length;
            var bytesToRetain = Math.Min(remaining, bytesRead);
            if (bytesToRetain > 0)
            {
                await retained.WriteAsync(buffer.AsMemory(0, bytesToRetain));
            }

            truncated |= bytesToRetain < bytesRead;
        }

        return new BoundedCapture(
            Utf8WithReplacement.GetString(retained.GetBuffer(), 0, (int)retained.Length),
            truncated);
    }

    private static async Task TerminateProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
        }

        try
        {
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ToolProbeResult StartFailure(
        ToolProbeDefinition probe,
        string message) =>
        new(
            probe.ProbeId,
            Succeeded: false,
            ExitCode: null,
            TimedOut: false,
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            FailureCode: "ToolProbeStartFailed",
            FailureMessage: message);

    private sealed record BoundedCapture(string Text, bool Truncated);
}
