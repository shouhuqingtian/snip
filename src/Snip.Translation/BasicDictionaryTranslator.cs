using System.Text.Json;
using System.Text.RegularExpressions;
using Snip.Core;

namespace Snip.Translation;

/// <summary>Bundled lexical lookup, never a sentence translation engine. No HTTP/native/model dependency.</summary>
public sealed class BasicDictionaryTranslator : ITranslationService
{
    public const int EntryCount = 100000;
    private readonly record struct Translations(string First, string? Second = null, string? Third = null)
    {
        public Translations Add(string word) => word==First || word==Second || word==Third ? this
            : Second is null ? this with { Second=word } : Third is null ? this with { Third=word } : this;
        public override string ToString() => Second is null ? First : Third is null ? First+" / "+Second : First+" / "+Second+" / "+Third;
    }
    private sealed record ChineseIndex(Dictionary<string,Translations> Words, int MaxLength);
    private static readonly Lazy<Dictionary<string,string>> English = new(LoadEnglish, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ChineseIndex> Chinese = new(LoadChinese, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Regex Tokens = new("[A-Za-z0-9]+(?:['’-][A-Za-z0-9]+)*|[\\u4e00-\\u9fff]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public Task<bool> IsAvailableAsync(CancellationToken token) { token.ThrowIfCancellationRequested();return Task.FromResult(true); }
    public Task<string> TranslateAsync(string text,string direction,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(text.Length>12000)throw new InvalidOperationException("基础词典单次最多查询 12000 字符，请分段查询。");
        if(direction is not "en-zh" and not "zh-en")throw new ArgumentException("请选择查词方向。");
        if(string.IsNullOrWhiteSpace(text))return Task.FromResult("");
        var english=direction=="en-zh" ? English.Value : null;
        var chinese=direction=="zh-en" ? Chinese.Value : null;
        token.ThrowIfCancellationRequested();
        var lines=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string term,string meaning)
        {
            if(seen.Add(term))lines.Add(term+" → "+meaning);
        }
        var matches=Tokens.Matches(text).Cast<Match>().ToArray();
        for(int i=0;i<matches.Length && lines.Count<200;i++)
        {
            token.ThrowIfCancellationRequested();var match=matches[i];string term=match.Value;
            if(direction=="en-zh")
            {
                bool found=false;
                for(int count=Math.Min(5,matches.Length-i);count>0;count--)
                {
                    var last=matches[i+count-1];string phrase=text.Substring(match.Index,last.Index+last.Length-match.Index);
                    if(!english!.TryGetValue(phrase,out var meaning))continue;
                    Add(phrase,meaning);i+=count-1;found=true;break;
                }
                if(!found)Add(term,"未收录");
            }
            else
            {
                for(int offset=0;offset<term.Length && lines.Count<200;)
                {
                    token.ThrowIfCancellationRequested();int length=Math.Min(chinese!.MaxLength,term.Length-offset);
                    while(length>0 && !chinese!.Words.ContainsKey(term.Substring(offset,length)))length--;
                    if(length>0)
                    {
                        string word=term.Substring(offset,length);Add(word,chinese!.Words[word].ToString());offset+=length;
                    }
                    else
                    {
                        int start=offset++;
                        while(offset<term.Length && !Enumerable.Range(1,Math.Min(chinese!.MaxLength,term.Length-offset)).Any(n=>chinese!.Words.ContainsKey(term.Substring(offset,n))))offset++;
                        Add(term[start..offset],"未收录");
                    }
                }
            }
        }
        string result=lines.Count==0 ? "没有可查询的词语。" : string.Join(Environment.NewLine,lines);
        if(lines.Count>=200)result+=Environment.NewLine+"已达到单次 200 个词条上限，请分段查询。";
        return Task.FromResult(result);
    }
    private static Dictionary<string,string> LoadEnglish()
    {
        using var stream=typeof(BasicDictionaryTranslator).Assembly.GetManifestResourceStream("Snip.Translation.Resources.basic-dictionary.json") ?? throw new InvalidOperationException("内置基础词典资源缺失。");
        var rows=JsonSerializer.Deserialize<string[][]>(stream) ?? throw new InvalidOperationException("内置基础词典无法读取。");
        var english=new Dictionary<string,string>(rows.Length,StringComparer.OrdinalIgnoreCase);
        foreach(var row in rows)english.Add(row[0],row[1]);
        if(english.Count!=EntryCount)throw new InvalidOperationException("内置基础词典词条数不正确。");
        return english;
    }
    private static ChineseIndex LoadChinese()
    {
        var chinese=new Dictionary<string,Translations>(StringComparer.Ordinal);int max=1;
        foreach(var (word,definition) in English.Value)
        foreach(var part in definition.Split([';',',','，','、','；'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var meaning=StripLabels(part.AsSpan());
            if(meaning.Length is <1 or >12)continue;
            bool valid=true;
            foreach(char c in meaning)if(c<'\u4e00' || c>'\u9fff'){valid=false;break;}
            if(!valid)continue;
            string key=meaning.ToString();
            chinese[key]=chinese.TryGetValue(key,out var previous) ? previous.Add(word) : new(word);
            max=Math.Max(max,meaning.Length);
        }
        return new(chinese,max);
    }
    private static ReadOnlySpan<char> StripLabels(ReadOnlySpan<char> value)
    {
        value=value.Trim();
        while(!value.IsEmpty)
        {
            if(value[0]=='[')
            {
                int end=value.IndexOf(']');if(end<0)break;
                value=value[(end+1)..].TrimStart();continue;
            }
            int letters=0;
            while(letters<value.Length && value[letters] is >= 'a' and <= 'z')letters++;
            if(letters==0 || letters==value.Length || value[letters]!='.')break;
            value=value[(letters+1)..].TrimStart();
        }
        return value;
    }
}
