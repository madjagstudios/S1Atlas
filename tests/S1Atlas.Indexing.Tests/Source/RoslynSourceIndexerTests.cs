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
    public void Captures_precise_multiline_method_source_span()
    {
        var source = """
            namespace Demo;
            public class Worker
            {
                public int Add(int x)
                {
                    return x + 1;
                }
            }
            """;

        var method = Assert.Single(
            new RoslynSourceIndexer().Index(source, CodebaseKind.S1Api, CodeChannel.Release),
            symbol => symbol.Kind == SymbolKind.Method);

        Assert.Equal(4, method.SourceLine);
        Assert.Equal(5, method.SourceColumn);
        Assert.Equal(7, method.SourceEndLine);
        Assert.Equal(6, method.SourceEndColumn);
    }

    [Fact]
    public void Captures_precise_type_and_single_line_member_source_spans()
    {
        var source = """
            namespace Demo;
            public class Worker
            {
                public int Value { get; set; }
            }
            """;

        var symbols = new RoslynSourceIndexer().Index(source, CodebaseKind.S1Api, CodeChannel.Release);
        var type = Assert.Single(symbols, symbol => symbol.Kind == SymbolKind.Type);
        var property = Assert.Single(symbols, symbol => symbol.Kind == SymbolKind.Property);

        Assert.Equal(2, type.SourceLine);
        Assert.Equal(1, type.SourceColumn);
        Assert.Equal(5, type.SourceEndLine);
        Assert.Equal(2, type.SourceEndColumn);

        Assert.Equal(4, property.SourceLine);
        Assert.Equal(5, property.SourceColumn);
        Assert.Equal(4, property.SourceEndLine);
        Assert.Equal(35, property.SourceEndColumn);
    }

    [Fact]
    public void Skips_parser_recovery_type_nodes_without_identifiers()
    {
        var symbols = new RoslynSourceIndexer().Index("namespace Demo { public class Outer { private sealed class } }", CodebaseKind.S1Api, CodeChannel.Release);

        Assert.DoesNotContain(symbols, symbol => string.IsNullOrWhiteSpace(symbol.QualifiedName));
    }

    [Fact]
    public void Recovers_outer_type_members_when_decompiler_generated_names_are_not_csharp_identifiers()
    {
        var source = """
            namespace Demo
            {
                public class MotelRoom
                {
                    private sealed class <>c
                    {
                        public static readonly <>c <>9;
                        internal bool <UpdateVariables>b__4_0(AdditiveDefinition x) { throw null; }
                    }
                    private bool NetworkInitialize___EarlyScheduleOne.Property.MotelRoom_Assembly-CSharp.dll_Excuted;
                }
                public class Worker
                {
                    public void Run() { }
                }
            }
            """;

        var symbols = new RoslynSourceIndexer().Index(source, CodebaseKind.S1Api, CodeChannel.Release);

        var method = Assert.Single(symbols, symbol => symbol.QualifiedName == "Demo.Worker::Run():System.Void");
        Assert.Equal(14, method.SourceLine);
        Assert.Equal(9, method.SourceColumn);
    }
}
