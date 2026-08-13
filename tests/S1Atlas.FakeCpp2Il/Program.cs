using System.Diagnostics;
using System.Text.Json;

return await FakeCpp2Il.RunAsync(args);

internal static class FakeCpp2Il
{
    private const string ArgumentRecordEnvironmentVariable =
        "S1ATLAS_FAKE_ARGUMENT_RECORD";

    public static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] == "process")
        {
            return await RunProcessModeAsync(arguments[1..]);
        }

        return await RunCpp2IlModeAsync(
            arguments.Length > 0 && arguments[0] == "cpp2il"
                ? arguments[1..]
                : arguments);
    }

    private static async Task<int> RunProcessModeAsync(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            return 64;
        }

        switch (arguments[0])
        {
            case "success" when arguments.Length == 2:
                return int.Parse(arguments[1], System.Globalization.CultureInfo.InvariantCulture);

            case "emit" when arguments.Length == 3:
                await Task.WhenAll(
                    EmitAsync(Console.OpenStandardOutput(), ParseCount(arguments[1]), (byte)'O'),
                    EmitAsync(Console.OpenStandardError(), ParseCount(arguments[2]), (byte)'E'));
                return 0;

            case "wait" when arguments.Length == 1:
                await Task.Delay(Timeout.InfiniteTimeSpan);
                return 0;

            case "spawn-child" when arguments.Length == 2:
                return await SpawnChildAsync(arguments[1]);

            case "print-args" when arguments.Length >= 2:
                await WriteJsonAsync(arguments[1], arguments[2..]);
                return 0;

            case "print-working-directory" when arguments.Length == 2:
                await File.WriteAllTextAsync(arguments[1], Environment.CurrentDirectory);
                return 0;

            case "print-environment" when arguments.Length == 3:
                await File.WriteAllTextAsync(
                    arguments[1],
                    Environment.GetEnvironmentVariable(arguments[2]) ?? "<null>");
                return 0;

            default:
                return 64;
        }
    }

    private static async Task<int> RunCpp2IlModeAsync(string[] arguments)
    {
        var recordPath = Environment.GetEnvironmentVariable(
            ArgumentRecordEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(recordPath))
        {
            await WriteJsonAsync(recordPath, arguments);
        }

        if (arguments.SequenceEqual(["--help"]))
        {
            Console.WriteLine("Fake Cpp2IL help");
            return 0;
        }

        if (arguments.SequenceEqual(["--list-output-formats"]))
        {
            Console.WriteLine("dll_il_recovery");
            return 0;
        }

        var outputArgument = arguments.SingleOrDefault(
            argument => argument.StartsWith("--output-to=", StringComparison.Ordinal));
        if (outputArgument is null)
        {
            return 64;
        }

        var outputPath = Path.GetFullPath(outputArgument["--output-to=".Length..]);
        Directory.CreateDirectory(outputPath);
        await File.WriteAllBytesAsync(
            Path.Combine(outputPath, "S1Atlas.FakeAssembly.dll"),
            "S1Atlas deterministic fake Cpp2IL output"u8.ToArray());
        return 0;
    }

    private static async Task<int> SpawnChildAsync(string childPidPath)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The fake executable path is unavailable.");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("process");
        startInfo.ArgumentList.Add("wait");

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The fake child process did not start.");
        await File.WriteAllTextAsync(
            childPidPath,
            child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    private static async Task EmitAsync(Stream destination, long count, byte value)
    {
        var buffer = new byte[16 * 1024];
        Array.Fill(buffer, value);
        while (count > 0)
        {
            var length = (int)Math.Min(count, buffer.Length);
            await destination.WriteAsync(buffer.AsMemory(0, length));
            count -= length;
        }

        await destination.FlushAsync();
    }

    private static long ParseCount(string value) =>
        long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static Task WriteJsonAsync(string path, string[] values) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(values));
}
