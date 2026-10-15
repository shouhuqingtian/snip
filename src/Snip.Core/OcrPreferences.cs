namespace Snip.Core;

public enum OcrMode { System, EnhancedOffline, Online }

public interface IOcrModeSelection : IOcrService, IOcrEnhancement
{
    OcrMode Mode { get; set; }
}

public interface IOcrPreferencesStore
{
    OcrMode? Read();
    void Save(OcrMode mode);
}
