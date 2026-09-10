using AssetRipper.Primitives;
using Iced.Intel;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests.Spikes;

/// <summary>
/// Task 1.2 (AT-39) spike: measures field-offset&#8594;field-name resolution fidelity for
/// <c>Customer</c> against the decoded body of <c>EvaluateCounteroffer</c>, and gathers evidence
/// for the decode-termination policy (ret vs. instruction/byte budget).
///
/// Read-only: only reads bytes from <c>GameAssembly.dll</c> and <c>global-metadata.dat</c>;
/// never launches or mutates the game. Skips (does not fail) when the game is not installed.
/// </summary>
[Collection(LibCpp2IlGlobalStateCollection.Name)]
[Trait("Category", "LocalGameRequired")]
public sealed class FieldLayoutSpike(ITestOutputHelper output)
{
    // The Microsoft x64 calling convention passes the first integer/pointer argument (the
    // implicit `this` for an instance method) in RCX. This heuristic tracks RCX plus any
    // register that is later loaded from an existing alias via a plain `mov reg, reg`.
    private static readonly Register[] InitialThisAliases = [Register.RCX];

    [Fact]
    public void EvaluateCounteroffer_FieldAccesses_ResolveAgainstCustomerFieldLayout()
    {
        LocalGameFixture.SkipUnlessAvailable();

        var (binaryBytes, metadataBytes) = LocalGameFixture.ReadImageBytes();
        LibCpp2IlMain.Reset();
        Assert.True(LibCpp2IlMain.Initialize(
            binaryBytes,
            metadataBytes,
            UnityVersion.Parse(CustomerLookup.UnitySupportedVersion)));

        var customerType = CustomerLookup.FindCustomerType();
        Assert.NotNull(customerType);

        var fieldsByOffset = BuildFieldOffsetIndex(customerType!, output);

        var evaluateCounteroffer = CustomerLookup.FindMethod(customerType!, "EvaluateCounteroffer");
        Assert.NotNull(evaluateCounteroffer);

        var decoded = DecodeFieldAccesses(binaryBytes, evaluateCounteroffer!, fieldsByOffset);
        output.WriteLine(
            $"Decoded {decoded.InstructionCount} instructions (terminatedByRet={decoded.TerminatedByRet}).");
        output.WriteLine($"Observed {decoded.Observations.Count} candidate this-relative memory accesses.");

        foreach (var observation in decoded.Observations)
        {
            var evidence = observation.ResolvedFieldName is not null
                ? $"this.{observation.ResolvedFieldName} @ 0x{observation.Displacement:x}"
                : $"field @ 0x{observation.Displacement:x}";
            output.WriteLine(
                $"  0x{observation.InstructionAddress:x} [{observation.BaseRegister}+0x{observation.Displacement:x}] -> {evidence}");
        }

        var resolved = decoded.Observations.Count(o => o.ResolvedFieldName is not null);
        if (decoded.Observations.Count > 0)
        {
            var hitRate = 100.0 * resolved / decoded.Observations.Count;
            output.WriteLine(
                $"Field-offset resolution hit rate: {resolved}/{decoded.Observations.Count} ({hitRate:F1}%).");
        }
        else
        {
            output.WriteLine(
                "No this-relative memory accesses observed with the current register-aliasing heuristic " +
                "(the method may be too short, or the compiler kept `this` only in RCX/volatile registers).");
        }
    }

    [Fact]
    public void DecodeLoop_TerminatesAtRetOrBudget_WhicheverComesFirst()
    {
        LocalGameFixture.SkipUnlessAvailable();

        var (binaryBytes, metadataBytes) = LocalGameFixture.ReadImageBytes();
        LibCpp2IlMain.Reset();
        Assert.True(LibCpp2IlMain.Initialize(
            binaryBytes,
            metadataBytes,
            UnityVersion.Parse(CustomerLookup.UnitySupportedVersion)));

        var customerType = CustomerLookup.FindCustomerType();
        Assert.NotNull(customerType);
        var fieldsByOffset = BuildFieldOffsetIndex(customerType!, output: null);

        var evaluateCounteroffer = CustomerLookup.FindMethod(customerType!, "EvaluateCounteroffer");
        Assert.NotNull(evaluateCounteroffer);

        var generous = DecodeFieldAccesses(
            binaryBytes, evaluateCounteroffer!, fieldsByOffset, maxBytes: 8192, maxInstructions: 100_000);
        output.WriteLine(
            $"Generous budget (100,000 instrs / 8,192 bytes): {generous.InstructionCount} instructions " +
            $"decoded before terminating; terminatedByRet={generous.TerminatedByRet}.");
        Assert.True(generous.TerminatedByRet, "Expected a ret within a generous instruction/byte budget.");

        const int truncatedBudget = 5;
        var truncated = DecodeFieldAccesses(
            binaryBytes, evaluateCounteroffer!, fieldsByOffset, maxBytes: 8192, maxInstructions: truncatedBudget);
        output.WriteLine(
            $"Truncated budget ({truncatedBudget} instrs): {truncated.InstructionCount} instructions decoded; " +
            $"terminatedByRet={truncated.TerminatedByRet}.");
        Assert.Equal(truncatedBudget, truncated.InstructionCount);
        Assert.False(truncated.TerminatedByRet, "A 5-instruction budget should truncate before reaching ret.");
    }

    private static Dictionary<long, string> BuildFieldOffsetIndex(
        Il2CppTypeDefinition customerType,
        ITestOutputHelper? output)
    {
        var fieldsByOffset = new Dictionary<long, string>();
        var fieldInfos = customerType.FieldInfos ?? [];
        output?.WriteLine($"Customer declares {fieldInfos.Length} fields:");
        foreach (var field in fieldInfos.OrderBy(f => f.FieldOffset))
        {
            var fieldName = field.Field?.Name ?? "<unnamed>";
            output?.WriteLine($"  0x{field.FieldOffset:x} {fieldName}");
            // Multiple Il2CppFieldReflectionData entries can share an offset (e.g. explicit
            // layout, or static/const fields reported at offset 0); keep the first instance field.
            fieldsByOffset.TryAdd(field.FieldOffset, fieldName);
        }

        return fieldsByOffset;
    }

    private static DecodedFieldAccesses DecodeFieldAccesses(
        byte[] binaryBytes,
        Il2CppMethodDefinition method,
        IReadOnlyDictionary<long, string> fieldsByOffset,
        int maxBytes = 4096,
        int maxInstructions = 2000)
    {
        var offset = checked((int)method.MethodOffsetInFile);
        var length = Math.Min(maxBytes, binaryBytes.Length - offset);
        var code = binaryBytes.AsSpan(offset, length).ToArray();
        var decoder = Decoder.Create(64, code, method.MethodPointer, DecoderOptions.None);

        var endAddress = method.MethodPointer + (ulong)length;
        var thisAliases = new HashSet<Register>(InitialThisAliases);
        var observations = new List<FieldAccessObservation>();
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

            if (instruction.Mnemonic == Mnemonic.Mov &&
                instruction.Op0Kind == OpKind.Register &&
                instruction.Op1Kind == OpKind.Register &&
                thisAliases.Contains(instruction.Op1Register))
            {
                thisAliases.Add(instruction.Op0Register);
            }

            for (var operand = 0; operand < instruction.OpCount; operand++)
            {
                if (instruction.GetOpKind(operand) != OpKind.Memory)
                {
                    continue;
                }

                var baseRegister = instruction.MemoryBase;
                if (baseRegister == Register.None || !thisAliases.Contains(baseRegister))
                {
                    continue;
                }

                if (instruction.MemoryIndex != Register.None)
                {
                    // Indexed/array-style access; not a plain `this`-relative field read.
                    continue;
                }

                var displacement = unchecked((long)instruction.MemoryDisplacement64);
                fieldsByOffset.TryGetValue(displacement, out var resolvedFieldName);
                observations.Add(new FieldAccessObservation(
                    instruction.IP, baseRegister, displacement, resolvedFieldName));
            }
        }

        return new DecodedFieldAccesses(observations, instructionCount, terminatedByRet);
    }

    private sealed record FieldAccessObservation(
        ulong InstructionAddress,
        Register BaseRegister,
        long Displacement,
        string? ResolvedFieldName);

    private sealed record DecodedFieldAccesses(
        IReadOnlyList<FieldAccessObservation> Observations,
        int InstructionCount,
        bool TerminatedByRet);
}
