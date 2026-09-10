using AssetRipper.Primitives;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests.Spikes;

/// <summary>
/// Task 1.1 (AT-39) spike: proves the LibCpp2IL symbol&#8594;address and address&#8594;symbol mapping
/// against the real installed Schedule I build for <c>Customer.EvaluateCounteroffer</c> and
/// <c>Customer.GetValueProposition</c>, and empirically determines whether
/// <c>LibCpp2IlMain.Reset()</c> is required between successive <c>Initialize</c> calls.
///
/// Read-only: only reads bytes from <c>GameAssembly.dll</c> and <c>global-metadata.dat</c>;
/// never launches or mutates the game. Skips (does not fail) when the game is not installed,
/// so CI without the game still passes.
/// </summary>
[Collection(LibCpp2IlGlobalStateCollection.Name)]
[Trait("Category", "LocalGameRequired")]
public sealed class LibCpp2IlMappingSpike(ITestOutputHelper output)
{
    [Fact]
    public void Initialize_ResolvesTargetMethods_WithDecodableCallTargets()
    {
        LocalGameFixture.SkipUnlessAvailable();

        var (binaryBytes, metadataBytes) = LocalGameFixture.ReadImageBytes();
        output.WriteLine($"GameAssembly.dll: {binaryBytes.Length:N0} bytes");
        output.WriteLine($"global-metadata.dat: {metadataBytes.Length:N0} bytes");

        var unityVersion = UnityVersion.Parse(CustomerLookup.UnitySupportedVersion);

        LibCpp2IlMain.Reset();
        var loadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var initialized = LibCpp2IlMain.Initialize(binaryBytes, metadataBytes, unityVersion);
        loadStopwatch.Stop();
        output.WriteLine($"LibCpp2IlMain.Initialize completed in {loadStopwatch.ElapsedMilliseconds} ms.");
        Assert.True(initialized, "LibCpp2IlMain.Initialize returned false against the installed build.");

        var customerType = CustomerLookup.FindCustomerType();
        Assert.NotNull(customerType);

        var evaluateCounteroffer = CustomerLookup.FindMethod(customerType!, "EvaluateCounteroffer");
        var getValueProposition = CustomerLookup.FindMethod(customerType!, "GetValueProposition");

        AssertResolvedAddresses(evaluateCounteroffer, "EvaluateCounteroffer");
        AssertResolvedAddresses(getValueProposition, "GetValueProposition");

        LogMethodAddresses("EvaluateCounteroffer", evaluateCounteroffer!);
        LogMethodAddresses("GetValueProposition", getValueProposition!);

        // Empirical finding (see the report): LibCpp2IlMain.GetMethodDefinitionByGlobalAddress
        // resolves entries in the IL2CPP *metadata usage* table (ldtoken/reflection-style global
        // references), not the entry-point address of a `call` target - it returns null even for
        // a target method's own MethodPointer. Demonstrate that here rather than asserting on it,
        // since a documented negative is still useful evidence for Task 2.x.
        var globalAddressSelfProbe = LibCpp2IlMain.GetMethodDefinitionByGlobalAddress(evaluateCounteroffer!.MethodPointer);
        output.WriteLine(
            "GetMethodDefinitionByGlobalAddress(EvaluateCounteroffer.MethodPointer) => " +
            (globalAddressSelfProbe is null ? "null (confirms it is not a code-address lookup)" : "NON-NULL (unexpected)"));

        var decodeStopwatch = System.Diagnostics.Stopwatch.StartNew();
        var decoded = DecodeCallTargets(binaryBytes, evaluateCounteroffer);
        decodeStopwatch.Stop();
        output.WriteLine(
            $"Decoded {decoded.InstructionCount} instructions from EvaluateCounteroffer in " +
            $"{decodeStopwatch.Elapsed.TotalMilliseconds:F2} ms (terminatedByRet={decoded.TerminatedByRet}).");

        Assert.NotEmpty(decoded.CallTargets);

        var resolved = decoded.CallTargets.Where(edge => edge.ResolvedManagedName is not null).ToList();
        var unresolved = decoded.CallTargets.Where(edge => edge.ResolvedManagedName is null).ToList();

        output.WriteLine(
            $"Call targets found: {decoded.CallTargets.Count}; resolved to managed methods " +
            $"(via GetManagedMethodImplementationsAtAddress): {resolved.Count}; unresolved " +
            $"(native/CRT helper thunks): {unresolved.Count}.");

        foreach (var edge in resolved)
        {
            var ambiguity = edge.ImplementationCount > 1 ? $" ({edge.ImplementationCount} implementations)" : string.Empty;
            output.WriteLine($"  resolved   0x{edge.Target:x} -> {edge.ResolvedManagedName}{ambiguity}");
        }

        foreach (var edge in unresolved)
        {
            output.WriteLine($"  unresolved 0x{edge.Target:x} (thunk/native helper - no managed implementation)");
        }

        Assert.True(
            resolved.Count >= 1,
            "Expected at least one call target to resolve to a managed method via GetManagedMethodImplementationsAtAddress.");
    }

    [Fact]
    public void Initialize_CalledTwiceWithoutReset_ThenWithReset_DeterminesReentrancyBehavior()
    {
        LocalGameFixture.SkipUnlessAvailable();

        var (binaryBytes, metadataBytes) = LocalGameFixture.ReadImageBytes();
        var unityVersion = UnityVersion.Parse(CustomerLookup.UnitySupportedVersion);

        LibCpp2IlMain.Reset();
        Assert.True(LibCpp2IlMain.Initialize(binaryBytes, metadataBytes, unityVersion));
        var firstMethod = CustomerLookup.FindMethod(CustomerLookup.FindCustomerType()!, "EvaluateCounteroffer");
        Assert.NotNull(firstMethod);
        var firstPointer = firstMethod!.MethodPointer;
        output.WriteLine($"First Initialize(): EvaluateCounteroffer.MethodPointer=0x{firstPointer:x}");

        Exception? secondInitException = null;
        var secondInitReturned = false;
        try
        {
            // Deliberately no Reset() call here: this is the reentrancy case under test.
            secondInitReturned = LibCpp2IlMain.Initialize(binaryBytes, metadataBytes, unityVersion);
        }
        catch (Exception exception)
        {
            secondInitException = exception;
        }

        if (secondInitException is not null)
        {
            output.WriteLine(
                $"Second Initialize() WITHOUT Reset() threw: {secondInitException.GetType().FullName}: " +
                secondInitException.Message);
        }
        else
        {
            output.WriteLine($"Second Initialize() WITHOUT Reset() returned {secondInitReturned} (no exception).");
            var secondCustomerType = CustomerLookup.FindCustomerType();
            var secondMethod = secondCustomerType is null
                ? null
                : CustomerLookup.FindMethod(secondCustomerType, "EvaluateCounteroffer");
            output.WriteLine(
                secondMethod is null
                    ? "  Customer.EvaluateCounteroffer could not be re-resolved after the un-reset re-Initialize."
                    : $"  Re-resolved EvaluateCounteroffer.MethodPointer=0x{secondMethod.MethodPointer:x} " +
                      $"(same as first load: {secondMethod.MethodPointer == firstPointer}).");
        }

        // Recovery path: an explicit Reset() before re-Initialize must always leave the mapping usable.
        LibCpp2IlMain.Reset();
        var thirdInitReturned = LibCpp2IlMain.Initialize(binaryBytes, metadataBytes, unityVersion);
        Assert.True(thirdInitReturned, "Initialize after an explicit Reset() should succeed.");
        var thirdMethod = CustomerLookup.FindMethod(CustomerLookup.FindCustomerType()!, "EvaluateCounteroffer");
        Assert.NotNull(thirdMethod);
        Assert.Equal(firstPointer, thirdMethod!.MethodPointer);
        output.WriteLine(
            $"Third Initialize() WITH an explicit Reset() first: recovered the same MethodPointer=0x{thirdMethod.MethodPointer:x}.");
    }

    private static void AssertResolvedAddresses(Il2CppMethodDefinition? method, string methodName)
    {
        Assert.True(method is not null, $"Customer.{methodName} was not found via LibCpp2IL metadata.");
        Assert.NotEqual(0UL, method!.MethodPointer);
        Assert.True(method.MethodOffsetInFile > 0, $"{methodName}.MethodOffsetInFile was not positive.");
        Assert.NotEqual(0UL, method.Rva);
    }

    private void LogMethodAddresses(string methodName, Il2CppMethodDefinition method)
    {
        output.WriteLine(
            $"Customer.{methodName}: MethodPointer=0x{method.MethodPointer:x}, Rva=0x{method.Rva:x}, " +
            $"MethodOffsetInFile=0x{method.MethodOffsetInFile:x} ({method.MethodOffsetInFile}), " +
            $"GlobalKey={method.GlobalKey}");
    }

    private static DecodedCallTargets DecodeCallTargets(
        byte[] binaryBytes,
        Il2CppMethodDefinition method,
        int maxBytes = 4096,
        int maxInstructions = 2000)
    {
        var offset = checked((int)method.MethodOffsetInFile);
        var length = Math.Min(maxBytes, binaryBytes.Length - offset);
        var code = binaryBytes.AsSpan(offset, length).ToArray();
        var decoder = Decoder.Create(64, code, method.MethodPointer, DecoderOptions.None);

        var endAddress = method.MethodPointer + (ulong)length;
        var callTargets = new List<CallTargetEdge>();
        var instructionCount = 0;
        var terminatedByRet = false;

        while (decoder.IP < endAddress && instructionCount < maxInstructions)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid)
            {
                break;
            }

            instructionCount++;

            if (instruction.Mnemonic == Mnemonic.Ret)
            {
                terminatedByRet = true;
                break;
            }

            if (instruction.FlowControl != FlowControl.Call)
            {
                continue;
            }

            var target = instruction.NearBranchTarget;

            // Empirical finding: GetManagedMethodImplementationsAtAddress(address) - not
            // GetMethodDefinitionByGlobalAddress - is what actually maps a native call target
            // to its managed method definition(s). It can return more than one implementation
            // for one address (e.g. two distinct generic instantiations sharing native code),
            // which is the ambiguous case a real provider must handle explicitly.
            var implementations = LibCpp2IlMain.GetManagedMethodImplementationsAtAddress(target);
            var resolvedName = implementations is { Count: > 0 }
                ? string.Join(" | ", implementations.Select(m => $"{m.DeclaringType?.FullName}::{m.Name}"))
                : null;
            callTargets.Add(new CallTargetEdge(target, resolvedName, implementations?.Count ?? 0));
        }

        return new DecodedCallTargets(callTargets, instructionCount, terminatedByRet);
    }

    private sealed record CallTargetEdge(ulong Target, string? ResolvedManagedName, int ImplementationCount);

    private sealed record DecodedCallTargets(
        IReadOnlyList<CallTargetEdge> CallTargets,
        int InstructionCount,
        bool TerminatedByRet);
}
