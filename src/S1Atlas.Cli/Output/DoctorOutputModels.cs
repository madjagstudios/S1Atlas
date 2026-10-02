using System.Text.Json.Serialization;
using S1Atlas.Application.Readiness;

namespace S1Atlas.Cli.Output;

internal sealed record DoctorOutput(
    bool IsReady,
    DoctorNextStepOutput NextStep,
    IReadOnlyList<DoctorItemOutput> Items)
{
    public static DoctorOutput FromReport(ReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new DoctorOutput(
            report.IsReady,
            new DoctorNextStepOutput(report.NextStep.Summary, report.NextStep.Command),
            report.Items.Select(DoctorItemOutput.FromItem).ToArray());
    }
}

internal sealed record DoctorNextStepOutput(
    string Summary,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Command);

internal sealed record DoctorItemOutput(
    string Id,
    string Title,
    string State,
    string Detail,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? FixCommand,
    bool Optional)
{
    public static DoctorItemOutput FromItem(ReadinessItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DoctorItemOutput(
            item.Id,
            item.Title,
            item.State switch
            {
                ReadinessState.Ok => "ok",
                ReadinessState.Missing => "missing",
                ReadinessState.Stale => "stale",
                _ => "not-applicable"
            },
            item.Detail,
            item.FixCommand,
            item.IsOptional);
    }
}
