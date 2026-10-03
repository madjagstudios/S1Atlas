using Xunit;

namespace S1Atlas.Core.Tests;

public sealed class SymbolNamesTests
{
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
