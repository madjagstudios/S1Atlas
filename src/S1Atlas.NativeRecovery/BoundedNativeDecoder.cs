using Iced.Intel;
using S1Atlas.Core.Storage;

namespace S1Atlas.NativeRecovery;

/// <summary>
/// Classifies how a native virtual address resolved against the managed method table.
/// An address can map to zero, one, or more than one managed method implementation
/// (the last case observed in AT-39 for shared generic-instantiation native code).
/// </summary>
public enum AddressResolutionKind
{
    None,
    Single,
    Ambiguous
}

/// <summary>
/// The result of resolving a native virtual address to a managed method name.
/// </summary>
/// <param name="Kind">Whether the address resolved to zero, one, or more than one managed method.</param>
/// <param name="ManagedName">The resolved managed method name, present only when <see cref="Kind"/> is <see cref="AddressResolutionKind.Single"/>.</param>
public readonly record struct AddressResolution(AddressResolutionKind Kind, string? ManagedName);

/// <summary>
/// Resolves a native call-target virtual address to zero, one, or more than one managed method.
/// </summary>
public interface IAddressResolver
{
    AddressResolution Resolve(ulong virtualAddress);
}

/// <summary>
/// Resolves a this-relative field offset to a field name, when known.
/// </summary>
public interface IFieldResolver
{
    /// <returns>The field name, or null when the offset is unresolvable (offset-only evidence).</returns>
    string? ResolveFieldName(ulong offset);
}

/// <summary>
/// The bounded evidence extracted from decoding a single method body: the call-graph edges
/// discovered and the this-relative field accesses observed, plus whether decoding reached a
/// clean <c>ret</c> (true) or was cut short by a budget or byte cap (false).
/// </summary>
public sealed record DecodedEvidence(
    IReadOnlyList<NativeEvidenceEdge> Edges,
    IReadOnlyList<string> FieldAccesses,
    bool IsComplete);

/// <summary>
/// Decodes a method's native x86-64 bytes with Iced into bounded call-graph edges and
/// field-access evidence strings. Never emits raw disassembly text into any field; every
/// emitted string is slash-free and passes <see cref="NativeNameNormalizer.IsSummarySafe"/>.
/// </summary>
public static class BoundedNativeDecoder
{
    /// <summary>
    /// Hard cap on the number of bytes decoded from <c>code</c>, independent of <c>maxEdges</c>.
    /// Bounds worst-case decode time/memory for a pathologically long method body that never
    /// reaches a <c>ret</c> within a reasonable edge budget.
    /// </summary>
    private const int MaxDecodeBytes = 4096;

    /// <param name="thisRegister">
    /// The register holding the incoming <c>this</c> pointer at method entry, per the x64 calling
    /// convention IL2CPP compiles against (the first integer/pointer argument arrives in RCX).
    /// Pass <see cref="Register.None"/> for a static method, which has no <c>this</c> pointer.
    /// Field-access evidence is only ever emitted for a memory operand whose base register is
    /// currently known to alias this register (see <see cref="UpdateThisAliases"/>) — never for a
    /// non-this base (a local struct pointer, another held reference) or a RIP-relative operand
    /// (RIP is never a this-alias).
    /// </param>
    public static DecodedEvidence Decode(
        ReadOnlySpan<byte> code,
        ulong startVirtualAddress,
        string sourcePointer,
        int maxEdges,
        IAddressResolver addresses,
        IFieldResolver fields,
        Register thisRegister)
    {
        var boundedLength = Math.Min(code.Length, MaxDecodeBytes);
        var bounded = code[..boundedLength].ToArray();
        var decoder = Decoder.Create(64, bounded, startVirtualAddress, DecoderOptions.None);
        var endAddress = startVirtualAddress + (ulong)boundedLength;

        var edges = new List<NativeEvidenceEdge>();
        var fieldAccesses = new List<string>();
        var seenFieldAccesses = new HashSet<string>();
        var isComplete = false;

        var thisAliases = new HashSet<Register>();
        if (thisRegister != Register.None)
        {
            thisAliases.Add(thisRegister.GetFullRegister());
        }

        var instructionInfoFactory = new InstructionInfoFactory();

        while (decoder.IP < endAddress)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid)
            {
                break;
            }

            if (instruction.Mnemonic == Mnemonic.Ret)
            {
                isComplete = true;
                break;
            }

            if (instruction.Mnemonic == Mnemonic.Call)
            {
                edges.Add(ClassifyCall(instruction, sourcePointer, addresses));
                if (edges.Count == maxEdges)
                {
                    break;
                }

                continue;
            }

            // Evaluate memory operands against the alias set as it stood BEFORE this instruction
            // executes, then apply this instruction's effect on the alias set. This mirrors the
            // real execution order: a read/write addressed via a this-alias register is genuine
            // evidence regardless of what this same instruction later does to other registers.
            CollectFieldAccesses(instruction, fields, thisAliases, fieldAccesses, seenFieldAccesses);
            UpdateThisAliases(instruction, thisAliases, instructionInfoFactory);
        }

        return new DecodedEvidence(edges, fieldAccesses, isComplete);
    }

    private static NativeEvidenceEdge ClassifyCall(
        in Instruction instruction, string sourcePointer, IAddressResolver addresses)
    {
        // Every call edge from the same enclosing method carries the same SourceMethodPointer (the
        // method entry, not the call site), so two distinct call instructions in one method body
        // would otherwise produce byte-identical edge tuples that canonicalize to the same EdgeId
        // and collide (AT-37 native-body-recovery regression). Threading the call instruction's own
        // address (its IP -- the call site, never the method entry and never the target) into the
        // Evidence string keeps every edge distinguishable, even when two sites call the same
        // target. The formatted pointer is lowercase 0x-hex via NativeNameNormalizer.Pointer, which
        // is always NativeNameNormalizer.IsSummarySafe (no '/', '\', "://", ".bin", "disassembly").
        var callSitePointer = NativeNameNormalizer.Pointer(instruction.IP);

        if (instruction.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32 or OpKind.NearBranch16)
        {
            var target = instruction.NearBranchTarget;
            var resolution = addresses.Resolve(target);
            var targetPointer = NativeNameNormalizer.Pointer(target);

            return resolution.Kind switch
            {
                AddressResolutionKind.Single => new NativeEvidenceEdge(
                    EdgeId: "",
                    SourceMethodPointer: sourcePointer,
                    TargetMethodPointer: targetPointer,
                    TargetText: resolution.ManagedName,
                    Kind: "DirectCall",
                    Evidence: $"direct call at {callSitePointer}",
                    IsComplete: true),
                AddressResolutionKind.Ambiguous => new NativeEvidenceEdge(
                    EdgeId: "",
                    SourceMethodPointer: sourcePointer,
                    TargetMethodPointer: targetPointer,
                    TargetText: null,
                    Kind: "DirectCall",
                    Evidence: $"direct call (ambiguous target) at {callSitePointer}",
                    IsComplete: false),
                _ => new NativeEvidenceEdge(
                    EdgeId: "",
                    SourceMethodPointer: sourcePointer,
                    TargetMethodPointer: null,
                    TargetText: null,
                    Kind: "RuntimeDispatch",
                    Evidence: $"unresolved call at {callSitePointer}",
                    IsComplete: false),
            };
        }

        // Indirect call through a register or memory operand (call reg / call [reg+disp]):
        // the target is only known at runtime, so it can never resolve to a concrete pointer.
        return new NativeEvidenceEdge(
            EdgeId: "",
            SourceMethodPointer: sourcePointer,
            TargetMethodPointer: null,
            TargetText: null,
            Kind: "RuntimeDispatch",
            Evidence: $"unresolved call at {callSitePointer}",
            IsComplete: false);
    }

    /// <summary>
    /// Emits FieldAccess evidence only for a memory operand whose base register is currently a
    /// this-alias, with no index register and a positive displacement. This excludes: a non-this
    /// base (a local struct pointer, another held reference — not a this-alias), RIP-relative
    /// operands (RIP is never a this-alias), indexed/array-style accesses, and non-positive
    /// displacements. Both reads and writes of a genuine this-field are recorded (a write to
    /// <c>this.field</c> is still real evidence of that field's existence), deduped within this
    /// method body so the decoder's own output stays clean.
    /// </summary>
    private static void CollectFieldAccesses(
        in Instruction instruction,
        IFieldResolver fields,
        HashSet<Register> thisAliases,
        List<string> fieldAccesses,
        HashSet<string> seenFieldAccesses)
    {
        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            if (instruction.GetOpKind(operand) != OpKind.Memory)
            {
                continue;
            }

            var baseRegister = instruction.MemoryBase;
            if (baseRegister == Register.None || !thisAliases.Contains(baseRegister.GetFullRegister()))
            {
                // No base register at all (rare absolute-address form), a non-this base, or a
                // RIP-relative operand (MemoryBase == Register.RIP, which is never a this-alias).
                continue;
            }

            if (instruction.MemoryIndex != Register.None)
            {
                // Indexed/array-style access; not a plain field-relative memory read.
                continue;
            }

            var rawDisplacement = instruction.MemoryDisplacement64;
            var signedDisplacement = unchecked((long)rawDisplacement);
            if (signedDisplacement <= 0)
            {
                continue;
            }

            var fieldName = fields.ResolveFieldName(rawDisplacement);
            var access = NativeNameNormalizer.FieldAccess(fieldName, rawDisplacement);
            if (seenFieldAccesses.Add(access))
            {
                fieldAccesses.Add(access);
            }
        }
    }

    /// <summary>
    /// Maintains the set of registers currently known to alias the incoming <c>this</c> pointer.
    /// A plain <c>mov regDest, regSrc</c> propagates the alias from <c>regSrc</c> to
    /// <c>regDest</c> when <c>regSrc</c> is currently an alias. Any other instruction that writes
    /// a register removes that register from the alias set — including <c>mov regDest, X</c> where
    /// <c>X</c> is not currently an alias (a load from memory, an immediate, or a non-alias
    /// register). This is intentionally conservative: when in doubt, drop the alias, since a false
    /// negative (missed field-access evidence) is safer than a false positive (mislabeled evidence).
    /// </summary>
    private static void UpdateThisAliases(
        in Instruction instruction, HashSet<Register> thisAliases, InstructionInfoFactory instructionInfoFactory)
    {
        if (instruction.Mnemonic == Mnemonic.Mov &&
            instruction.Op0Kind == OpKind.Register &&
            instruction.Op1Kind == OpKind.Register)
        {
            var destination = instruction.Op0Register.GetFullRegister();
            var source = instruction.Op1Register.GetFullRegister();
            if (thisAliases.Contains(source))
            {
                thisAliases.Add(destination);
            }
            else
            {
                thisAliases.Remove(destination);
            }

            return;
        }

        var info = instructionInfoFactory.GetInfo(instruction);
        foreach (var usedRegister in info.GetUsedRegisters())
        {
            if (!IsWriteAccess(usedRegister.Access))
            {
                continue;
            }

            thisAliases.Remove(usedRegister.Register.GetFullRegister());
        }
    }

    private static bool IsWriteAccess(OpAccess access) => access switch
    {
        OpAccess.Write or OpAccess.CondWrite or OpAccess.ReadWrite or OpAccess.ReadCondWrite => true,
        _ => false,
    };
}
