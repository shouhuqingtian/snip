using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class TranslationDirectionTests
{
    [Theory]
    [InlineData("这是截图中的中文。", "zh-en")]
    [InlineData("The application is ready.", "en-zh")]
    [InlineData("请保存 API Key 并重新启动 Chrome。", "zh-en")]
    [InlineData("This is a long English sentence with a 中文 note.", "en-zh")]
    [InlineData("版本 2.0.23，2026/10/15", "zh-en")]
    [InlineData("\U00020000", "zh-en")]
    [InlineData("123.45 %", "en-zh")]
    [InlineData("", "en-zh")]
    public void ResolvesEachSourceIndependently(string source, string direction)
        => Assert.Equal(direction, TranslationDirection.Resolve(source));

    [Fact]
    public void ExplicitOverridesRemainAvailableForMixedText()
    {
        Assert.Equal("en-zh", TranslationDirection.Resolve("中文", "en-zh"));
        Assert.Equal("zh-en", TranslationDirection.Resolve("English", "zh-en"));
        Assert.Throws<ArgumentException>(() => TranslationDirection.Resolve("text", "invalid"));
    }

    [Fact]
    public void FreshPreferencesAreAutomaticInEveryMode()
    {
        foreach (var mode in Enum.GetValues<TranslationMode>())
        {
            var preferences = new TranslationPreferences(mode);
            preferences.Validate();
            Assert.Equal("auto", preferences.Direction);
        }
    }
}
