namespace Snip.Core;

public sealed record TranslationVocabulary(string English, string Ipa, string PartOfSpeech, string Meaning);

/// <summary>Keep the copyable translation separate from optional model-generated language notes.</summary>
public sealed record AnnotatedTranslation(string Translation, string Pinyin, IReadOnlyList<TranslationVocabulary> Words, string Notice = "");

public interface IAnnotatedTranslationService : ITranslationService
{
    Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text, string direction, IProgress<TranslationProgress>? progress, CancellationToken cancellationToken);
}
