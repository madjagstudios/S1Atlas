using S1Atlas.Core.Indexing;
using S1Atlas.Core.Metadata;
using S1Atlas.Core.Storage;
using S1Atlas.Indexing.Workflow;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class AttributeIndexingWorkflowTests
{
    private const string SnapshotId = "snapshot-attributes";
    private const string TypeName = "Demo.Widget";

    [Fact]
    public void Game_workflow_maps_attribute_facts_to_records()
    {
        var fixture = CreateFixture();

        var symbols = IndexingWorkflow.BuildSymbols(fixture.Decompilation, SnapshotId);
        var writeSet = new IndexWriteSet(
            symbols,
            [],
            [],
            [],
            [],
            CustomAttributes: IndexingWorkflow.BuildAttributes(fixture.Decompilation, symbols, SnapshotId));

        AssertAttributeRows(symbols, writeSet, fixture, CodebaseKind.ScheduleI);
    }

    [Fact]
    public void Installed_api_workflow_maps_attribute_facts_to_records()
    {
        var fixture = CreateFixture();

        var symbols = ApiIndexingWorkflow.BuildSymbols(fixture.Decompilation, SnapshotId, CodebaseKind.S1Api);
        var writeSet = new IndexWriteSet(
            symbols,
            [],
            [],
            [],
            [],
            CustomAttributes: ApiIndexingWorkflow.BuildAttributes(fixture.Decompilation, symbols, SnapshotId, CodebaseKind.S1Api));

        AssertAttributeRows(symbols, writeSet, fixture, CodebaseKind.S1Api);
    }

    private static void AssertAttributeRows(
        IReadOnlyList<IndexSymbolRecord> symbols,
        IndexWriteSet writeSet,
        AttributeFixture fixture,
        CodebaseKind codebase)
    {
        Assert.NotNull(writeSet.CustomAttributes);
        var rows = writeSet.CustomAttributes!;
        Assert.Equal(6, rows.Count);
        Assert.DoesNotContain(rows, row => row.AttributeType.StartsWith("Cpp2ILInjected.", StringComparison.Ordinal));
        Assert.All(rows, row =>
        {
            Assert.Equal(SnapshotId, row.SnapshotId);
            Assert.Equal(64, row.AttributeId.Length);
            Assert.DoesNotContain(row.AttributeId, char.IsUpper);
        });
        Assert.Equal(6, rows.Select(row => row.AttributeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            rows.OrderBy(row => row.AttributeId, StringComparer.Ordinal).Select(row => row.AttributeId),
            rows.Select(row => row.AttributeId));

        var byType = rows.ToDictionary(row => row.AttributeType, StringComparer.Ordinal);
        AssertMemberRow(symbols, byType["Demo.TypeMarkerAttribute"], "Type", TypeName, null, null, fixture.TypeAttribute, codebase);
        AssertMemberRow(symbols, byType["FishNet.Object.ServerRpcAttribute"], "Member", TypeName + "::" + fixture.MethodSignature, null, null, fixture.MethodAttribute, codebase);
        AssertMemberRow(symbols, byType["UnityEngine.SerializeField"], "Member", TypeName + "::" + fixture.FieldSignature, null, null, fixture.FieldAttribute, codebase);
        AssertMemberRow(symbols, byType["Demo.PropertyMarkerAttribute"], "Member", TypeName + "::" + fixture.PropertySignature, null, null, fixture.PropertyAttribute, codebase);
        AssertMemberRow(symbols, byType["Demo.EventMarkerAttribute"], "Member", TypeName + "::" + fixture.EventSignature, null, null, fixture.EventAttribute, codebase);
        AssertMemberRow(symbols, byType["Demo.RangeAttribute"], "Parameter", TypeName + "::" + fixture.MethodSignature, 1, "count", fixture.ParameterAttribute, codebase);

        Assert.Equal(
            """{"type":"Demo.RangeAttribute","fixed":[{"kind":"Primitive","type":"System.Int32","value":0},{"kind":"Primitive","type":"System.Int32","value":100}],"named":[]}""",
            byType["Demo.RangeAttribute"].ArgsJson);
    }

    private static void AssertMemberRow(
        IReadOnlyList<IndexSymbolRecord> symbols,
        IndexCustomAttributeRecord row,
        string target,
        string qualifiedName,
        int? parameterIndex,
        string? parameterName,
        DecodedCustomAttribute expected,
        CodebaseKind codebase)
    {
        Assert.Equal(target, row.Target);
        Assert.Equal(parameterIndex, row.ParameterIndex);
        Assert.Equal(parameterName, row.ParameterName);
        Assert.Equal(CustomAttributeJson.ToJson(expected), row.ArgsJson);
        var symbol = Assert.Single(symbols, symbol => symbol.SymbolId == row.SymbolId);
        Assert.Equal(qualifiedName, symbol.QualifiedName);
        Assert.StartsWith(codebase + ":" + CodeChannel.Installed + ":", symbol.CanonicalKey, StringComparison.Ordinal);
    }

    private static AttributeFixture CreateFixture()
    {
        const string methodSignature = "System.Void Run(System.String, System.Int32)";
        const string fieldSignature = "System.Int32 _count";
        const string propertySignature = "System.String Name";
        const string eventSignature = "System.EventHandler Changed";
        var typeAttribute = new DecodedCustomAttribute(
            "Demo.TypeMarkerAttribute",
            [new(CustomAttributeValueKind.String, "widget", "System.String", null)],
            [new("Rank", new(CustomAttributeValueKind.Primitive, 7, "System.Int32", null))]);
        var methodAttribute = new DecodedCustomAttribute(
            "FishNet.Object.ServerRpcAttribute",
            [],
            [new("RequireOwnership", new(CustomAttributeValueKind.Primitive, false, "System.Boolean", null))]);
        var fieldAttribute = new DecodedCustomAttribute("UnityEngine.SerializeField", [], []);
        var propertyAttribute = new DecodedCustomAttribute(
            "Demo.PropertyMarkerAttribute",
            [new(CustomAttributeValueKind.Primitive, 42, "System.Int32", null)],
            []);
        var eventAttribute = new DecodedCustomAttribute(
            "Demo.EventMarkerAttribute",
            [],
            [new("Topic", new(CustomAttributeValueKind.String, "change", "System.String", null))]);
        var parameterAttribute = new DecodedCustomAttribute(
            "Demo.RangeAttribute",
            [
                new(CustomAttributeValueKind.Primitive, 0, "System.Int32", null),
                new(CustomAttributeValueKind.Primitive, 100, "System.Int32", null),
            ],
            []);
        // A stale or synthetic fact set can still carry tool-injected analysis
        // attributes the metadata reader would have dropped; the workflow must
        // not persist them as game attributes.
        var injectedAttribute = new DecodedCustomAttribute("Cpp2ILInjected.AddressAttribute", [], []);
        var decompilation = new ManagedDecompilation(
            "synthetic.dll",
            "// synthetic",
            [
                new ManagedTypeFacts(
                    TypeName,
                    "Demo",
                    "Widget",
                    "System.Object",
                    [],
                    [
                        new ManagedMemberFacts(
                            "Run",
                            ManagedMemberKind.Method,
                            methodSignature,
                            false,
                            [],
                            Attributes: [methodAttribute],
                            ParameterAttributes: [new(1, "count", [parameterAttribute])]),
                        new ManagedMemberFacts(
                            "_count",
                            ManagedMemberKind.Field,
                            fieldSignature,
                            false,
                            [],
                            Attributes: [fieldAttribute, injectedAttribute]),
                        new ManagedMemberFacts(
                            "Name",
                            ManagedMemberKind.Property,
                            propertySignature,
                            false,
                            [],
                            Attributes: [propertyAttribute]),
                        new ManagedMemberFacts(
                            "Changed",
                            ManagedMemberKind.Event,
                            eventSignature,
                            false,
                            [],
                            Attributes: [eventAttribute]),
                        new ManagedMemberFacts(".ctor", ManagedMemberKind.Constructor, "System.Void .ctor()", false, []),
                    ],
                    Attributes: [typeAttribute]),
            ]);
        return new AttributeFixture(
            decompilation,
            methodSignature,
            fieldSignature,
            propertySignature,
            eventSignature,
            typeAttribute,
            methodAttribute,
            fieldAttribute,
            propertyAttribute,
            eventAttribute,
            parameterAttribute);
    }

    private sealed record AttributeFixture(
        ManagedDecompilation Decompilation,
        string MethodSignature,
        string FieldSignature,
        string PropertySignature,
        string EventSignature,
        DecodedCustomAttribute TypeAttribute,
        DecodedCustomAttribute MethodAttribute,
        DecodedCustomAttribute FieldAttribute,
        DecodedCustomAttribute PropertyAttribute,
        DecodedCustomAttribute EventAttribute,
        DecodedCustomAttribute ParameterAttribute);
}
