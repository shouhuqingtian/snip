namespace Snip.Core;

/// <summary>Only the layout choice is persisted, never the recognized or translated text.</summary>
public interface ITextReadingPreferencesStore
{
    bool? ReadSourceExpanded();
    void SaveSourceExpanded(bool expanded);
}
