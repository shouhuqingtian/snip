using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class TextContentTests
{
    [Fact]
    public void FormColumnPaddingDoesNotConsumeTranslationLimitOrChangeParagraphs()
    {
        string source = string.Join("\r\n\r\n", Enumerable.Range(0, 12).Select(i =>
            "   申请部门：" + new string(' ', 2048) + "采购 " + i + "   "));
        Assert.True(source.Length > 12000);
        string input = TextContent.WithoutLayoutPadding(source);
        Assert.Equal(string.Join("\r\n\r\n", Enumerable.Range(0, 12).Select(i => "申请部门： 采购 " + i)), input);
        Assert.True(input.Length < 200);
    }

    [Theory]
    [InlineData("Hello world\r\n中文 😀", 13)]
    [InlineData(" \t\r\n　", 0)]
    [InlineData("\U00020000", 1)]
    public void CharacterCountExcludesWhitespaceAndDoesNotDoubleCountSurrogates(string text, int expected)
        => Assert.Equal(expected, TextContent.CountCharacters(text));

    [Theory]
    [InlineData("a\tb\n\n  c\r\n", "a b\n\nc\r\n")]
    [InlineData("", "")]
    public void PaddingRemovalRetainsLineEndingsAndEmptyParagraphs(string text, string expected)
        => Assert.Equal(expected, TextContent.WithoutLayoutPadding(text));
}
