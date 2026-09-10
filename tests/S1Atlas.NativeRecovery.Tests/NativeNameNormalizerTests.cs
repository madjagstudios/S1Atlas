using S1Atlas.NativeRecovery;
using Xunit;

namespace S1Atlas.NativeRecovery.Tests;

public class NativeNameNormalizerTests
{
    // Pointer tests
    [Fact]
    public void Pointer_FormatsVirtualAddressAsLowercaseHexWithPrefix()
    {
        var result = NativeNameNormalizer.Pointer(0x1806A7C20);
        Assert.Equal("0x1806a7c20", result);
    }

    [Fact]
    public void Pointer_ProducesLowercaseHex()
    {
        var result = NativeNameNormalizer.Pointer(0xDEADBEEF);
        Assert.Equal("0xdeadbeef", result);
    }

    [Fact]
    public void Pointer_HandlesZero()
    {
        var result = NativeNameNormalizer.Pointer(0x0);
        Assert.Equal("0x0", result);
    }

    [Fact]
    public void Pointer_HandlesMaxUlong()
    {
        var result = NativeNameNormalizer.Pointer(ulong.MaxValue);
        Assert.Equal("0xffffffffffffffff", result);
    }

    [Fact]
    public void Pointer_HasNoDigitSeparators()
    {
        var result = NativeNameNormalizer.Pointer(0x123456789ABCDEF0);
        Assert.DoesNotContain("_", result);
        Assert.DoesNotContain(",", result);
    }

    [Fact]
    public void Pointer_OutputPassesIsSummarySafe()
    {
        var result = NativeNameNormalizer.Pointer(0x1806A7C20);
        Assert.True(NativeNameNormalizer.IsSummarySafe(result));
    }

    // ManagedName tests
    [Fact]
    public void ManagedName_JoinsTypeAndMethodWithDot()
    {
        var result = NativeNameNormalizer.ManagedName("ScheduleOne.Economy", "MyMethod");
        Assert.Equal("ScheduleOne.Economy.MyMethod", result);
    }

    [Fact]
    public void ManagedName_ReplacesIL2CPPNestedSeparatorSlash()
    {
        var result = NativeNameNormalizer.ManagedName("ScheduleOne.Economy.Customer/Nested", "M");
        Assert.Equal("ScheduleOne.Economy.Customer.Nested.M", result);
    }

    [Fact]
    public void ManagedName_ReplacesIL2CPPNestedSeparatorPlus()
    {
        var result = NativeNameNormalizer.ManagedName("Namespace.Type+NestedType", "Method");
        Assert.Equal("Namespace.Type.NestedType.Method", result);
    }

    [Fact]
    public void ManagedName_ReplacesMultipleNestedSeparators()
    {
        var result = NativeNameNormalizer.ManagedName("Outer/Middle+Inner", "Method");
        Assert.Equal("Outer.Middle.Inner.Method", result);
    }

    [Fact]
    public void ManagedName_CollapsesDoubledDots()
    {
        // When replacement happens, we might get ".." which should be collapsed
        var result = NativeNameNormalizer.ManagedName("Type./Nested", "M");
        // After replacing "/" -> ".", we get "Type...Nested.M", should become "Type.Nested.M"
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void ManagedName_ContainsNoSlashes()
    {
        var result = NativeNameNormalizer.ManagedName("Outer/Middle+Inner/Deep", "Method");
        Assert.DoesNotContain("/", result);
    }

    [Fact]
    public void ManagedName_OutputPassesIsSummarySafe()
    {
        var result = NativeNameNormalizer.ManagedName("ScheduleOne.Economy.Customer/Nested", "M");
        Assert.True(NativeNameNormalizer.IsSummarySafe(result));
    }

    // FieldAccess tests
    [Fact]
    public void FieldAccess_WithNonNullFieldName_FormatsAsThisFieldNameAtHex()
    {
        var result = NativeNameNormalizer.FieldAccess("myField", 0x168);
        Assert.Equal("this.myField @ 0x168", result);
    }

    [Fact]
    public void FieldAccess_WithNullFieldName_FormatsAsFieldAtHex()
    {
        var result = NativeNameNormalizer.FieldAccess(null, 0x168);
        Assert.Equal("field @ 0x168", result);
    }

    [Fact]
    public void FieldAccess_WithBlankFieldName_FormatsAsFieldAtHex()
    {
        var result = NativeNameNormalizer.FieldAccess("   ", 0x168);
        Assert.Equal("field @ 0x168", result);
    }

    [Fact]
    public void FieldAccess_FormatsOffsetAsLowercaseHexWithoutPrefix()
    {
        var result = NativeNameNormalizer.FieldAccess("data", 0xDEADBEEF);
        Assert.Equal("this.data @ 0xdeadbeef", result);
    }

    [Fact]
    public void FieldAccess_HandlesZeroOffset()
    {
        var result = NativeNameNormalizer.FieldAccess("field", 0x0);
        Assert.Equal("this.field @ 0x0", result);
    }

    [Fact]
    public void FieldAccess_HandlesLargeOffset()
    {
        var result = NativeNameNormalizer.FieldAccess("x", 0x123456789ABCDEF0);
        Assert.Equal("this.x @ 0x123456789abcdef0", result);
    }

    [Fact]
    public void FieldAccess_OutputPassesIsSummarySafe()
    {
        var result = NativeNameNormalizer.FieldAccess("myField", 0x168);
        Assert.True(NativeNameNormalizer.IsSummarySafe(result));
    }

    [Fact]
    public void FieldAccess_WithoutFieldNameOutputPassesIsSummarySafe()
    {
        var result = NativeNameNormalizer.FieldAccess(null, 0x168);
        Assert.True(NativeNameNormalizer.IsSummarySafe(result));
    }

    // IsSummarySafe tests
    [Fact]
    public void IsSummarySafe_RejectsNull()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe(null!));
    }

    [Fact]
    public void IsSummarySafe_RejectsEmpty()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe(""));
    }

    [Fact]
    public void IsSummarySafe_RejectsWhitespaceOnly()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("   "));
        Assert.False(NativeNameNormalizer.IsSummarySafe("\t\n"));
    }

    [Fact]
    public void IsSummarySafe_RejectsLongerThan512Chars()
    {
        var longString = new string('a', 513);
        Assert.False(NativeNameNormalizer.IsSummarySafe(longString));
    }

    [Fact]
    public void IsSummarySafe_AcceptsExactly512Chars()
    {
        var string512 = new string('a', 512);
        Assert.True(NativeNameNormalizer.IsSummarySafe(string512));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingNullChar()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("hello\0world"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingProtocolSeparator()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("http://example.com"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingBackslash()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("path\\to\\file"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingForwardSlash()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("path/to/file"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingBinExtension()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("game.bin"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingBinExtensionMixedCase()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("game.BIN"));
        Assert.False(NativeNameNormalizer.IsSummarySafe("game.Bin"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingDisassembly()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("disassembly of function"));
    }

    [Fact]
    public void IsSummarySafe_RejectsContainingDisassemblyMixedCase()
    {
        Assert.False(NativeNameNormalizer.IsSummarySafe("DISASSEMBLY"));
        Assert.False(NativeNameNormalizer.IsSummarySafe("Disassembly"));
    }

    [Fact]
    public void IsSummarySafe_AcceptsValidText()
    {
        Assert.True(NativeNameNormalizer.IsSummarySafe("ScheduleOne.Economy.Customer.M"));
    }

    [Fact]
    public void IsSummarySafe_TrimsBeforeValidating()
    {
        Assert.True(NativeNameNormalizer.IsSummarySafe("  valid text  "));
    }

    [Fact]
    public void IsSummarySafe_RejectsAfterTrimLongerThan512Chars()
    {
        var longString = new string('a', 510) + "aaa";
        Assert.False(NativeNameNormalizer.IsSummarySafe(longString));
    }

    [Fact]
    public void IsSummarySafe_AcceptsHexaddressformat()
    {
        Assert.True(NativeNameNormalizer.IsSummarySafe("0x1806a7c20"));
    }

    [Fact]
    public void IsSummarySafe_AcceptsFieldAccessFormat()
    {
        Assert.True(NativeNameNormalizer.IsSummarySafe("this.myField @ 0x168"));
    }

    [Fact]
    public void IsSummarySafe_AcceptsDots()
    {
        Assert.True(NativeNameNormalizer.IsSummarySafe("ScheduleOne.Economy.Customer.Nested.M"));
    }
}
