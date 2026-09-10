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

    public static DecodedEvidence Decode(
        ReadOnlySpan<byte> code,
        ulong startVirtualAddress,
        string sourcePointer,
        int maxEdges,
        IAddressResolver addresses,
        IFieldResolver fields)
    {
        var boundedLength = Math.Min(code.Length, MaxDecodeBytes);
        var bounded = code[..boundedLength].ToArray();
        var decoder = Decoder.Create(64, bounded, startVirtualAddress, DecoderOptions.None);
        var endAddress = startVirtualAddress + (ulong)boundedLength;

        var edges = new List<NativeEvidenceEdge>();
        var fieldAccesses = new List<string>();
        var isComplete = false;

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

            CollectFieldAccesses(instruction, fields, fieldAccesses);
        }

        return new DecodedEvidence(edges, fieldAccesses, isComplete);
    }

    private static NativeEvidenceEdge ClassifyCall(
        in Instruction instruction, string sourcePointer, IAddressResolver addresses)
    {
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
                    Evidence: "direct call",
                    IsComplete: true),
                AddressResolutionKind.Ambiguous => new NativeEvidenceEdge(
                    EdgeId: "",
                    SourceMethodPointer: sourcePointer,
                    TargetMethodPointer: targetPointer,
                    TargetText: null,
                    Kind: "DirectCall",
                    Evidence: "direct call ambiguous target",
                    IsComplete: false),
                _ => new NativeEvidenceEdge(
                    EdgeId: "",
                    SourceMethodPointer: sourcePointer,
                    TargetMethodPointer: null,
                    TargetText: null,
                    Kind: "RuntimeDispatch",
                    Evidence: "unresolved direct call target",
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
            Evidence: "indirect call",
            IsComplete: false);
    }

    private static void CollectFieldAccesses(
        in Instruction instruction, IFieldResolver fields, List<string> fieldAccesses)
    {
        for (var operand = 0; operand < instruction.OpCount; operand++)
        {
            if (instruction.GetOpKind(operand) != OpKind.Memory)
            {
                continue;
            }

            var baseRegister = instruction.MemoryBase;
            if (baseRegister == Register.None)
            {
                continue;
            }

            if (instruction.MemoryIndex != Register.None)
            {
                // Indexed/array-style access; not a plain field-relative memory read.
                continue;
            }

            var displacement = instruction.MemoryDisplacement64;
            if (displacement == 0)
            {
                continue;
            }

            var fieldName = fields.ResolveFieldName(displacement);
            fieldAccesses.Add(NativeNameNormalizer.FieldAccess(fieldName, displacement));
        }
    }
}
