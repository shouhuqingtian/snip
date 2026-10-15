using System.Text;

namespace Snip.Core;

public static class TextContent
{
    public static int CountCharacters(string text)
    {
        int count = 0;
        foreach (var rune in text.EnumerateRunes()) if (!Rune.IsWhiteSpace(rune)) count++;
        return count;
    }

    /// <summary>Remove visual column padding from the translation copy, retaining line/paragraph breaks.</summary>
    public static string WithoutLayoutPadding(string text)
    {
        var result = new StringBuilder(Math.Min(text.Length, 4096));
        bool lineStart = true, pendingSpace = false;
        foreach (char c in text)
        {
            if (c is ' ' or '\t') { pendingSpace = !lineStart; continue; }
            if (c is '\r' or '\n') { result.Append(c); lineStart = true; pendingSpace = false; continue; }
            if (pendingSpace) result.Append(' ');
            result.Append(c); lineStart = false; pendingSpace = false;
        }
        return result.ToString();
    }
}
