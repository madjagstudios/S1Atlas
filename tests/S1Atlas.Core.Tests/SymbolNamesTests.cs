using S1Atlas.Core;
using Xunit;

namespace S1Atlas.Core.Tests;

public sealed class SymbolNamesTests
{
    [Theory]
    // Types: after the last '.' or '+'.
    [InlineData("Demo.ById", "ById")]
    [InlineData("ScheduleOne.PlayerScripts.PlayerMovement", "PlayerMovement")]
    [InlineData("A.B.C+Inner", "Inner")]
    [InlineData("A.B.Box`1", "Box`1")]
    [InlineData("A.B.Box`1+<Each>d__0", "<Each>d__0")]
    [InlineData("A.B.C+<>c__DisplayClass0_0", "<>c__DisplayClass0_0")]
    [InlineData("mod/A.B.Widget", "Widget")]
    // Members: after the last "::", cut at the first '(' or ':'.
    [InlineData("A.B.C::Foo(System.Int32):System.Int32", "Foo")]
    [InlineData("N.GenericBox`1::Convert`1(!!0):!!0", "Convert`1")]
    [InlineData("Demo.Factory::.ctor()", ".ctor")]
    [InlineData("S1Atlas.ParityFixture.DelegateHolder::.cctor():System.Void", ".cctor")]
    [InlineData("A.B.C::get_Prop():System.Int32", "get_Prop")]
    [InlineData("N.EventBase::add_Changed(System.EventHandler):System.Void", "add_Changed")]
    [InlineData("Demo.Capture.Holder+<>c__DisplayClass0_0::x", "x")]
    [InlineData("A.B.C+<>c::<Foo>b__0_0(System.Int32):System.Int32", "<Foo>b__0_0")]
    [InlineData("mod/Shared.Reference::Run():System.Void", "Run")]
    // Typed members: "Type Name" after "::" yields the name.
    [InlineData("A.B.C::System.Int32 offset", "offset")]
    [InlineData("N.GenericBox`1::!0 Stored", "Stored")]
    [InlineData("A.B.C::System.String Name", "Name")]
    [InlineData("A.B.C::System.String Item(System.Int32)", "Item")]
    [InlineData("A.B.C::System.EventHandler Changed", "Changed")]
    [InlineData("mod/A.B.Widget::System.Int32 Count", "Count")]
    public void SimpleName_returns_the_type_or_member_identifier(string qualifiedName, string expected)
    {
        Assert.Equal(expected, SymbolNames.SimpleName(qualifiedName));
    }

    [Theory]
    [InlineData("Game.Widget::Run():System.Void", "Game.Widget", "Run():System.Void")]
    [InlineData("Game.Widget::Compute", "Game.Widget", "Compute")]
    [InlineData("Game.Widget+Nested::Inner():System.Void", "Game.Widget+Nested", "Inner():System.Void")]
    public void Splits_member_names_on_the_type_separator(string qualifiedName, string type, string member)
    {
        Assert.True(SymbolNames.TrySplitMember(qualifiedName, out var actualType, out var actualMember));
        Assert.Equal(type, actualType);
        Assert.Equal(member, actualMember);
    }

    [Theory]
    [InlineData("NoSeparator")]
    [InlineData("::Run()")]
    [InlineData("")]
    public void Rejects_names_without_a_declaring_type(string qualifiedName)
    {
        Assert.False(SymbolNames.TrySplitMember(qualifiedName, out _, out _));
    }
}
