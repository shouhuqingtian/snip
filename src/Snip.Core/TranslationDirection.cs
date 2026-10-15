namespace Snip.Core;

public static class TranslationDirection
{
    /// <summary>Compare Chinese characters with English words, so product names inside Chinese do not dominate.</summary>
    public static string Resolve(string text, string preference = "auto")
    {
        if (preference is "en-zh" or "zh-en") return preference;
        if (preference != "auto") throw new ArgumentException("翻译方向无效。", nameof(preference));
        int chinese = 0, englishWords = 0;
        bool inEnglish = false;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c is >= 0x3400 and <= 0x4dbf or >= 0x4e00 and <= 0x9fff or >= 0x20000 and <= 0x323af) chinese++;
            bool english = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            if (english && !inEnglish) englishWords++;
            inEnglish = english;
        }
        return chinese > 0 && chinese >= englishWords ? "zh-en" : "en-zh";
    }
}
