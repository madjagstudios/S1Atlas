using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class ManagedSymbolResolverTests
{
    private class FakeIl2CppMethodLookup : IIl2CppMethodLookup
    {
        private readonly Dictionary<(string DeclaringType, string MethodName), List<NativeMethodCandidate>> _candidates;

        public FakeIl2CppMethodLookup()
        {
            _candidates = new();
        }

        public void AddCandidate(string declaringTypeFullName, string methodName, NativeMethodCandidate candidate)
        {
            var key = (declaringTypeFullName, methodName);
            if (!_candidates.ContainsKey(key))
            {
                _candidates[key] = new List<NativeMethodCandidate>();
            }
            _candidates[key].Add(candidate);
        }

        public IReadOnlyList<NativeMethodCandidate> FindByTypeAndName(string declaringTypeFullName, string methodName)
        {
            var key = (declaringTypeFullName, methodName);
            return _candidates.ContainsKey(key) ? _candidates[key].AsReadOnly() : new List<NativeMethodCandidate>().AsReadOnly();
        }
    }

    private static NativeMethodCandidate CreateCandidate(
        string declaringTypeFullName = "TestNamespace.TestType",
        string methodName = "TestMethod",
        IReadOnlyList<string>? parameterTypeFullNames = null,
        ulong methodPointer = 0x1000,
        long methodOffsetInFile = 100,
        ulong rva = 0x2000)
    {
        parameterTypeFullNames ??= new List<string>();
        return new NativeMethodCandidate(methodPointer, methodOffsetInFile, rva, declaringTypeFullName, methodName, parameterTypeFullNames);
    }

    [Fact]
    public void Resolve_WithExactSingleMatch_ReturnsResolved()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Execute",
            parameterTypeFullNames: new[] { "System.String", "System.Int32" },
            methodPointer: 0x1000,
            methodOffsetInFile: 100,
            rva: 0x2000
        );
        lookup.AddCandidate("MyApp.Services", "Execute", candidate);

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_001",
            declaringTypeFullName: "MyApp.Services",
            methodName: "Execute",
            parameterTypeFullNames: new[] { "System.String", "System.Int32" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
        Assert.Equal("sym_001", result.Symbol.SymbolId);
        Assert.Equal(0x1000u, result.Symbol.MethodPointer);
        Assert.Equal(100L, result.Symbol.MethodOffsetInFile);
        Assert.Equal(0x2000u, result.Symbol.Rva);
        // Should produce slash-free name via NativeNameNormalizer
        Assert.DoesNotContain("/", result.Symbol.ManagedName);
        Assert.DoesNotContain("+", result.Symbol.ManagedName);
    }

    [Fact]
    public void Resolve_WithMultipleOverloadsAndDisambiguatingParams_ReturnsResolved()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();

        // Overload 1: no parameters
        var overload1 = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Process",
            parameterTypeFullNames: new List<string>(),
            methodPointer: 0x1000
        );
        lookup.AddCandidate("MyApp.Services", "Process", overload1);

        // Overload 2: with parameters
        var overload2 = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Process",
            parameterTypeFullNames: new[] { "System.String" },
            methodPointer: 0x2000
        );
        lookup.AddCandidate("MyApp.Services", "Process", overload2);

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_002",
            declaringTypeFullName: "MyApp.Services",
            methodName: "Process",
            parameterTypeFullNames: new[] { "System.String" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
        Assert.Equal(0x2000u, result.Symbol.MethodPointer);
    }

    [Fact]
    public void Resolve_WithMultipleIndistinguishableCandidates_ReturnsAmbiguous()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();

        // Two candidates with identical signatures
        var candidate1 = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Duplicate",
            parameterTypeFullNames: new[] { "System.String" },
            methodPointer: 0x1000
        );
        lookup.AddCandidate("MyApp.Services", "Duplicate", candidate1);

        var candidate2 = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Duplicate",
            parameterTypeFullNames: new[] { "System.String" },
            methodPointer: 0x2000
        );
        lookup.AddCandidate("MyApp.Services", "Duplicate", candidate2);

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_003",
            declaringTypeFullName: "MyApp.Services",
            methodName: "Duplicate",
            parameterTypeFullNames: new[] { "System.String" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Ambiguous, result.Kind);
        Assert.Null(result.Symbol);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public void Resolve_WithNoCandidates_ReturnsNotFound()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_004",
            declaringTypeFullName: "NonExistent.Type",
            methodName: "NonExistent",
            parameterTypeFullNames: new[] { "System.String" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.NotFound, result.Kind);
        Assert.Null(result.Symbol);
    }

    [Fact]
    public void Resolve_WithCandidateButNoParameterMatch_ReturnsNotFound()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Execute",
            parameterTypeFullNames: new[] { "System.String" }
        );
        lookup.AddCandidate("MyApp.Services", "Execute", candidate);

        // Act - request different parameters
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_005",
            declaringTypeFullName: "MyApp.Services",
            methodName: "Execute",
            parameterTypeFullNames: new[] { "System.Int32" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.NotFound, result.Kind);
        Assert.Null(result.Symbol);
    }

    [Fact]
    public void Resolve_NormalizesNestedTypeSepatorsInCandidates()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        // Candidate has forward slash nested type separator in parameter type
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Nested",
            methodName: "Method",
            parameterTypeFullNames: new[] { "TypeA/NestedB" }
        );
        lookup.AddCandidate("MyApp.Nested", "Method", candidate);

        // Act - request with dot separators in parameter type
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_006",
            declaringTypeFullName: "MyApp.Nested",
            methodName: "Method",
            parameterTypeFullNames: new[] { "TypeA.NestedB" },
            lookup: lookup
        );

        // Assert - should match because parameter separators are normalized
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
    }

    [Fact]
    public void Resolve_NormalizesNestedTypeSeparatorsPlusSign()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        // Candidate has plus nested type separator in parameter type
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Nested",
            methodName: "Method",
            parameterTypeFullNames: new[] { "TypeA+NestedB" }
        );
        lookup.AddCandidate("MyApp.Nested", "Method", candidate);

        // Act - request with dot separators in parameter type
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_007",
            declaringTypeFullName: "MyApp.Nested",
            methodName: "Method",
            parameterTypeFullNames: new[] { "TypeA.NestedB" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
    }

    [Fact]
    public void Resolve_TrimsWhitespaceFromParameterTypes()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "Method",
            parameterTypeFullNames: new[] { "  System.String  " }
        );
        lookup.AddCandidate("MyApp.Services", "Method", candidate);

        // Act - request with different whitespace
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_008",
            declaringTypeFullName: "MyApp.Services",
            methodName: "Method",
            parameterTypeFullNames: new[] { "System.String" },
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
    }

    [Fact]
    public void Resolve_ProducesManagedNameWithoutSlashes()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        // Candidate has slashes in the declaring type (as it might come from IL2CPP)
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp/Nested/Deep",
            methodName: "MyMethod"
        );
        lookup.AddCandidate("MyApp/Nested/Deep", "MyMethod", candidate);

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_009",
            declaringTypeFullName: "MyApp/Nested/Deep",
            methodName: "MyMethod",
            parameterTypeFullNames: new List<string>(),
            lookup: lookup
        );

        // Assert
        Assert.NotNull(result.Symbol);
        // ManagedName should be slash-free because NativeNameNormalizer normalizes it
        Assert.DoesNotContain("/", result.Symbol.ManagedName);
        Assert.DoesNotContain("+", result.Symbol.ManagedName);
        // Should contain expected components
        Assert.Contains("MyApp", result.Symbol.ManagedName);
        Assert.Contains("Nested", result.Symbol.ManagedName);
        Assert.Contains("Deep", result.Symbol.ManagedName);
        Assert.Contains("MyMethod", result.Symbol.ManagedName);
    }

    [Fact]
    public void Resolve_EmptyParameterListMatchesEmptyParameterCandidate()
    {
        // Arrange
        var lookup = new FakeIl2CppMethodLookup();
        var candidate = CreateCandidate(
            declaringTypeFullName: "MyApp.Services",
            methodName: "NoArgs",
            parameterTypeFullNames: new List<string>()
        );
        lookup.AddCandidate("MyApp.Services", "NoArgs", candidate);

        // Act
        var result = ManagedSymbolResolver.Resolve(
            symbolId: "sym_010",
            declaringTypeFullName: "MyApp.Services",
            methodName: "NoArgs",
            parameterTypeFullNames: new List<string>(),
            lookup: lookup
        );

        // Assert
        Assert.Equal(SymbolResolution.Resolved, result.Kind);
        Assert.NotNull(result.Symbol);
    }
}
