using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Source;
using Xunit;

namespace S1Atlas.Indexing.Tests.Source;

public sealed class RoslynSourceIndexerTests
{
    [Fact]
    public void Equivalent_source_declarations_share_canonical_keys()
    {
        var source = "namespace Demo { public class Widget { public void Run(int value) {} } }";
        var symbols = new RoslynSourceIndexer().Index(source, CodebaseKind.S1Api, CodeChannel.Release);

        Assert.Contains(symbols, symbol => symbol.Kind == SymbolKind.Type && symbol.Signature == "Demo.Widget");
        var method = Assert.Single(symbols, symbol => symbol.Kind == SymbolKind.Method);
        Assert.Equal(
            CanonicalSignatureRenderer.RenderMethod("Demo.Widget", "Run", "void", ["System.Int32"]),
            method.Signature);
        Assert.Equal(method.Signature, method.QualifiedName);
        Assert.Equal("source.cs", method.SourceFile);
        Assert.All(symbols, symbol => Assert.True(symbol.IsBestEffort));
    }

    [Fact]
    public void Skips_parser_recovery_type_nodes_without_identifiers()
    {
        var symbols = new RoslynSourceIndexer().Index("namespace Demo { public class Outer { private sealed class } }", CodebaseKind.S1Api, CodeChannel.Release);

        Assert.DoesNotContain(symbols, symbol => string.IsNullOrWhiteSpace(symbol.QualifiedName));
    }
}
