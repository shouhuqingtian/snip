using Snip.Core;
using Xunit;
namespace Snip.Core.Tests;
public sealed class SpeechTextTests
{
    [Theory]
    [InlineData("Hello world.", "en")]
    [InlineData("你好，欢迎使用截图。", "zh")]
    [InlineData("這是中文翻譯。", "zh")]
    [InlineData("Windows 应用程序支持保存截图。", "zh")]
    [InlineData("A sentence in English with 中文 notes.", "en")]
    public void VoiceLanguageFollowsActualContent(string text, string language)
        => Assert.Equal(language, SpeechText.Language(text));
    [Fact] public void LongTextIsBoundedWithoutLosingWords()
    {
        string text=string.Join(" ",Enumerable.Range(0,2000).Select(i=>$"word{i}."));
        var chunks=SpeechText.Chunks(text);
        Assert.All(chunks,part=>Assert.InRange(part.Length,1,600));
        Assert.Equal(string.Concat(text.Where(c=>!char.IsWhiteSpace(c))),string.Concat(chunks).Replace(" ",""));
        Assert.True(chunks.Count>10);
    }
    [Fact] public void SurrogatePairsAreNotSplit()
    {
        string text=new string('a',31)+"😀"+new string('b',70);
        var chunks=SpeechText.Chunks(text,32);
        Assert.Equal(text,string.Concat(chunks));
        Assert.All(chunks,part=>{Assert.False(char.IsLowSurrogate(part[0]));Assert.False(char.IsHighSurrogate(part[^1]));});
    }
    [Fact] public void EmptyAndUnreasonablyLargeInputDoNotStartSpeech()
    {
        Assert.Empty(SpeechText.Chunks(" \r\n "));
        Assert.Throws<ArgumentException>(()=>SpeechText.Chunks(new string('x',60001)));
        Assert.Throws<ArgumentOutOfRangeException>(()=>SpeechText.Chunks("text",0));
    }
}
