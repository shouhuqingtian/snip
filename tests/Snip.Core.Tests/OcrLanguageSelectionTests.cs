using Snip.Core;
using Xunit;

namespace Snip.Core.Tests;

public sealed class OcrLanguageSelectionTests
{
    [Fact]
    public void AutomaticCanRetryEnglishInsteadOfStoppingAtChinese()
        => Assert.Equal(new[]{"zh-Hans-CN","en-US"},OcrLanguageSelection.Candidates(["fr-FR","en-US","zh-Hans-CN"],OcrLanguageSelection.Automatic));
    [Fact]
    public void ExplicitLanguageNeverChangesSilently()
        => Assert.Equal(new[]{"zh-Hans-CN"},OcrLanguageSelection.Candidates(["zh-Hans-CN","en-US"],"zh-Hans-CN"));
    [Fact]
    public void MissingEnglishDoesNotInventAnInstalledRecognizer()
        => Assert.Equal(new[]{"zh-Hans-CN"},OcrLanguageSelection.Candidates(["zh-Hans-CN"],null));
    [Fact]
    public void ManualOtherLanguageRemainsSupported()
        => Assert.Equal(new[]{"de-DE"},OcrLanguageSelection.Candidates(["zh-Hans-CN","en-US","de-DE"],"de-DE"));
    [Fact]
    public void MultipleVariantsHaveABoundedRetryCount()
        => Assert.Equal(2,OcrLanguageSelection.Candidates(["zh-Hans-CN","zh-Hant-TW","en-US","en-GB"],null).Count);
}
