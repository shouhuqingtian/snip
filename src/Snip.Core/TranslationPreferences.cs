namespace Snip.Core;

public enum TranslationMode { Offline, Online, Basic }

public sealed record TranslationPreferences(TranslationMode Mode, string Direction = "auto")
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Direction is not "auto" and not "en-zh" and not "zh-en")
            throw new ArgumentException("翻译方式或翻译方向无效。");
    }
}

public interface ITranslationPreferencesStore
{
    TranslationPreferences? Read();
    void Save(TranslationPreferences preferences);
}

public interface IAutoStartService
{
    bool IsEnabled { get; }
    void SetEnabled(bool enabled);
}
