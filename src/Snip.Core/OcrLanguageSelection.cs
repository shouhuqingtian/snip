namespace Snip.Core;

public static class OcrLanguageSelection
{
    public const string Automatic = "自动（中英）";
    public static IReadOnlyList<string> Candidates(IReadOnlyList<string> installed, string? selected)
    {
        if (selected is not null && selected != Automatic)
            return installed.Contains(selected) ? [selected] : [];
        var chinese=installed.FirstOrDefault(value=>value.StartsWith("zh",StringComparison.OrdinalIgnoreCase));
        var english=installed.FirstOrDefault(value=>value.StartsWith("en",StringComparison.OrdinalIgnoreCase));
        var candidates=new List<string>(2);
        if(chinese is not null)candidates.Add(chinese);
        if(english is not null)candidates.Add(english);
        if(candidates.Count==0 && installed.Count>0)candidates.Add(installed[0]);
        return candidates;
    }
}
