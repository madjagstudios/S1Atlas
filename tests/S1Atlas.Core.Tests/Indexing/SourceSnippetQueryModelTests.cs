using S1Atlas.Core.Indexing;
using Xunit;

namespace S1Atlas.Core.Tests.Indexing;

public sealed class SourceSnippetQueryModelTests
{
    [Fact]
    public void Runtime_verification_signal_exposes_the_contract_values()
    {
        Assert.Equal(["Physics", "NavMesh", "TriggerState"], Enum.GetNames<RuntimeVerificationSignal>());

        var hint = new RuntimeVerificationHint(
            [RuntimeVerificationSignal.Physics, RuntimeVerificationSignal.TriggerState],
            "Static guidance; verify this behavior in-game.");

        Assert.Equal(
            [RuntimeVerificationSignal.Physics, RuntimeVerificationSignal.TriggerState],
            hint.Signals);
        Assert.Equal("Static guidance; verify this behavior in-game.", hint.Message);
    }

    [Fact]
    public void Source_snippet_query_result_preserves_old_positional_construction_and_defaults_new_fields()
    {
        var symbol = new SymbolQueryResult(
            "index-1",
            "ScheduleI",
            "Installed",
            "symbol-1",
            "Method",
            "Demo.Widget.Run",
            "System.Void Demo.Widget::Run()",
            false);
        var location = new SourceLocationQueryResult("symbol-1", 4, 1, 6, 2);

        var result = new SourceSnippetQueryResult(
            symbol,
            "index-1",
            "Managed/Demo.cs",
            "sha256",
            128,
            location,
            0,
            0,
            "void Run() { }",
            BodyRecoveryStatus.Recovered,
            "Verified");

        Assert.Null(result.RuntimeVerification);
        Assert.Null(result.Neighborhood);
        Assert.Null(result.NeighborhoodNotice);
    }
}
