using System.CommandLine;
using System.Text;
using S1Atlas.Application.Readiness;
using S1Atlas.Cli.Output;

namespace S1Atlas.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create(
        IAtlasReadinessService readiness,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var jsonOption = CommandOutput.CreateJsonOption();
        var command = new Command("doctor", "Check atlas pipeline readiness.");
        command.Options.Add(jsonOption);
        command.SetAction(parseResult =>
        {
            var commandOutput = new CommandOutput(
                "doctor",
                parseResult.GetValue(jsonOption),
                output,
                error);
            return CommandExecution.Run(
                () =>
                {
                    var report = readiness.EvaluateAsync(cancellationToken).GetAwaiter().GetResult();
                    return commandOutput.Complete(
                        report.IsReady ? 0 : 1,
                        DoctorOutput.FromReport(report),
                        writer => WriteHuman(report, writer));
                },
                commandOutput,
                cancellationToken);
        });
        return command;
    }

    internal static void WriteHuman(ReadinessReport report, TextWriter writer)
    {
        var marks = DoctorMarks.For(writer);
        foreach (var item in report.Items)
        {
            writer.WriteLine($"{marks.For(item.State)} {item.Title}: {item.Detail}");
        }

        writer.WriteLine(report.NextStep.Summary);
        if (report.NextStep.IsReady && report.NextStep.Command is not null)
        {
            writer.WriteLine($"Example query: {report.NextStep.Command}");
        }
    }
}

internal static class DoctorMarks
{
    public static DoctorMarkSet For(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return writer.Encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding
            ? new DoctorMarkSet("✓", "✗", "✗", "-")
            : new DoctorMarkSet("[ok]", "[missing]", "[stale]", "[n/a]");
    }
}

internal sealed record DoctorMarkSet(string Ok, string Missing, string Stale, string NotApplicable)
{
    public string For(ReadinessState state) =>
        state switch
        {
            ReadinessState.Ok => Ok,
            ReadinessState.Missing => Missing,
            ReadinessState.Stale => Stale,
            _ => NotApplicable
        };
}
