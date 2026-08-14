using S1Atlas.Core.Indexing;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Fingerprints;
using Xunit;

namespace S1Atlas.Indexing.Tests.Fingerprints;

public sealed class SymbolFingerprintServiceTests
{
    [Fact]
    public void Creates_method_body_and_source_layers_when_evidence_is_available()
    {
        var symbol = new IndexSymbolRecord("symbol-1", "snapshot-1", "S1Api:Release:Method:Demo.Widget::Run", "Method", "Demo.Widget::Run", "Run", false);
        var fingerprints = new SymbolFingerprintService().Create(
            [symbol],
            new Dictionary<string, IReadOnlyList<string>> { [symbol.SymbolId] = ["Calls:Demo.Service::Do"] },
            new Dictionary<string, IReadOnlyList<string>> { [symbol.SymbolId] = ["public void Run()"] });

        Assert.Contains(fingerprints, fingerprint => fingerprint.Kind == "method-body");
        Assert.Contains(fingerprints, fingerprint => fingerprint.Kind == "source");
    }
}
