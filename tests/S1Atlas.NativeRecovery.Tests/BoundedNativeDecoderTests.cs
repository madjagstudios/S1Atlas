using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class BoundedNativeDecoderTests
{
    private const ulong StartAddress = 0x1000;

    // Assembles `call rel32` (opcode 0xE8 + 4-byte little-endian relative displacement)
    // targeting the given absolute virtual address, given the instruction starts at `at`.
    private static byte[] CallRel32(ulong at, ulong target)
    {
        var nextIp = at + 5; // E8 + 4-byte rel32
        var rel32 = unchecked((int)(target - nextIp));
        var bytes = new byte[5];
        bytes[0] = 0xE8;
        BitConverter.GetBytes(rel32).CopyTo(bytes, 1);
        return bytes;
    }

    // `call qword ptr [rax+0x10]`: FF /2 with ModRM=01 010 000 (mod=disp8, reg=/2, rm=rax), disp8=0x10.
    private static byte[] CallIndirectMemory() => [0xFF, 0x50, 0x10];

    // `ret` (near return, no operands).
    private static byte[] Ret() => [0xC3];

    // `mov rax, [rbx+disp32]`: REX.W 8B /r, ModRM=10 000 011 (mod=disp32, reg=rax, rm=rbx).
    private static byte[] MovRaxFromRbxDisp32(uint disp)
    {
        var bytes = new byte[7];
        bytes[0] = 0x48; // REX.W
        bytes[1] = 0x8B; // MOV r64, r/m64
        bytes[2] = 0x83; // ModRM: mod=10, reg=000 (rax), rm=011 (rbx)
        BitConverter.GetBytes(disp).CopyTo(bytes, 3);
        return bytes;
    }

    private static byte[] Concat(params byte[][] chunks)
    {
        var result = new List<byte>();
        foreach (var chunk in chunks)
        {
            result.AddRange(chunk);
        }

        return [.. result];
    }

    private sealed class FakeAddressResolver(AddressResolutionKind kind, string? managedName = null) : IAddressResolver
    {
        public AddressResolution Resolve(ulong virtualAddress) => new(kind, managedName);
    }

    private sealed class FakeFieldResolver(string? name) : IFieldResolver
    {
        public string? ResolveFieldName(ulong offset) => name;
    }

    [Fact]
    public void Decode_CallRel32ResolvingSingle_YieldsCompleteDirectCallEdge()
    {
        const ulong target = 0x2000;
        var code = CallRel32(StartAddress, target);
        var resolver = new FakeAddressResolver(AddressResolutionKind.Single, "ScheduleOne.Economy.Customer.Foo");

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        var edge = Assert.Single(result.Edges);
        Assert.Equal("", edge.EdgeId);
        Assert.Equal("0x1000", edge.SourceMethodPointer);
        Assert.Equal("DirectCall", edge.Kind);
        Assert.Equal(NativeNameNormalizer.Pointer(target), edge.TargetMethodPointer);
        Assert.Equal("ScheduleOne.Economy.Customer.Foo", edge.TargetText);
        Assert.True(edge.IsComplete);
        Assert.True(NativeNameNormalizer.IsSummarySafe(edge.Evidence));
    }

    [Fact]
    public void Decode_CallRel32ResolvingAmbiguous_YieldsIncompleteDirectCallWithNullTargetText()
    {
        const ulong target = 0x2000;
        var code = CallRel32(StartAddress, target);
        var resolver = new FakeAddressResolver(AddressResolutionKind.Ambiguous);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        var edge = Assert.Single(result.Edges);
        Assert.Equal("DirectCall", edge.Kind);
        Assert.Equal(NativeNameNormalizer.Pointer(target), edge.TargetMethodPointer);
        Assert.Null(edge.TargetText);
        Assert.False(edge.IsComplete);
        Assert.True(NativeNameNormalizer.IsSummarySafe(edge.Evidence));
    }

    [Fact]
    public void Decode_CallRel32ResolvingNone_YieldsRuntimeDispatchEdge()
    {
        const ulong target = 0x2000;
        var code = CallRel32(StartAddress, target);
        var resolver = new FakeAddressResolver(AddressResolutionKind.None);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        var edge = Assert.Single(result.Edges);
        Assert.Equal("RuntimeDispatch", edge.Kind);
        Assert.Null(edge.TargetMethodPointer);
        Assert.Null(edge.TargetText);
        Assert.False(edge.IsComplete);
        Assert.True(NativeNameNormalizer.IsSummarySafe(edge.Evidence));
    }

    [Fact]
    public void Decode_IndirectMemoryCall_YieldsRuntimeDispatchEdge()
    {
        var code = CallIndirectMemory();
        var resolver = new FakeAddressResolver(AddressResolutionKind.Single, "Should.Not.Be.Used");

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        var edge = Assert.Single(result.Edges);
        Assert.Equal("RuntimeDispatch", edge.Kind);
        Assert.Null(edge.TargetMethodPointer);
        Assert.Null(edge.TargetText);
        Assert.False(edge.IsComplete);
    }

    [Fact]
    public void Decode_HittingMaxEdges_TruncatesAndMarksIncomplete()
    {
        var call1 = CallRel32(StartAddress, 0x2000);
        var call2 = CallRel32(StartAddress + (ulong)call1.Length, 0x3000);
        var code = Concat(call1, call2);
        var resolver = new FakeAddressResolver(AddressResolutionKind.Single, "Some.Method");

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 1, resolver, new FakeFieldResolver(null));

        Assert.Single(result.Edges);
        Assert.False(result.IsComplete);
    }

    [Fact]
    public void Decode_Ret_TerminatesWithIsCompleteTrue()
    {
        var code = Ret();
        var resolver = new FakeAddressResolver(AddressResolutionKind.None);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        Assert.Empty(result.Edges);
        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Decode_MemoryReadWithResolvableField_AppendsThisDotFieldAccess()
    {
        var code = Concat(MovRaxFromRbxDisp32(0x168), Ret());
        var resolver = new FakeAddressResolver(AddressResolutionKind.None);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver("balance"));

        var access = Assert.Single(result.FieldAccesses);
        Assert.Equal("this.balance @ 0x168", access);
        Assert.True(NativeNameNormalizer.IsSummarySafe(access));
    }

    [Fact]
    public void Decode_MemoryReadWithUnresolvableField_AppendsOffsetOnlyFieldAccess()
    {
        var code = Concat(MovRaxFromRbxDisp32(0x168), Ret());
        var resolver = new FakeAddressResolver(AddressResolutionKind.None);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        var access = Assert.Single(result.FieldAccesses);
        Assert.Equal("field @ 0x168", access);
        Assert.True(NativeNameNormalizer.IsSummarySafe(access));
    }

    [Fact]
    public void Decode_ExceedingByteCap_TruncatesAndMarksIncomplete()
    {
        // No ret anywhere in this buffer: 5000 single-byte NOPs, well past the internal
        // byte-cap constant, so the decoder must stop before reaching the end of the buffer.
        var code = new byte[5000];
        Array.Fill(code, (byte)0x90); // NOP
        var resolver = new FakeAddressResolver(AddressResolutionKind.None);

        var result = BoundedNativeDecoder.Decode(
            code, StartAddress, "0x1000", maxEdges: 10, resolver, new FakeFieldResolver(null));

        Assert.Empty(result.Edges);
        Assert.False(result.IsComplete);
    }
}
