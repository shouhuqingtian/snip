using System.Text.Json;
using System.Text.RegularExpressions;
using Snip.Core;

namespace Snip.Translation;

/// <summary>Bundled lexical lookup, never a sentence translation engine. No HTTP/native/model dependency.</summary>
public sealed class BasicDictionaryTranslator : ITranslationService
{
    private sealed record Lexicon(Dictionary<string,string> English, Dictionary<string,List<string>> Chinese, int MaxChineseLength);
    private static readonly Lazy<Lexicon> Data = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Regex Tokens = new("[A-Za-z0-9]+(?:['’-][A-Za-z0-9]+)*|[\\u4e00-\\u9fff]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    public Task<bool> IsAvailableAsync(CancellationToken token) { token.ThrowIfCancellationRequested();return Task.FromResult(true); }
    public Task<string> TranslateAsync(string text,string direction,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(text.Length>12000)throw new InvalidOperationException("基础词典单次最多查询 12000 字符，请分段查询。");
        if(direction is not "en-zh" and not "zh-en")throw new ArgumentException("请选择查词方向。");
        if(string.IsNullOrWhiteSpace(text))return Task.FromResult("");
        var data=Data.Value;token.ThrowIfCancellationRequested();
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
                    if(!data.English.TryGetValue(phrase,out var meaning))continue;
                    Add(phrase,meaning);i+=count-1;found=true;break;
                }
                if(!found)Add(term,"未收录");
            }
            else
            {
                for(int offset=0;offset<term.Length && lines.Count<200;)
                {
                    token.ThrowIfCancellationRequested();int length=Math.Min(data.MaxChineseLength,term.Length-offset);
                    while(length>0 && !data.Chinese.ContainsKey(term.Substring(offset,length)))length--;
                    if(length>0)
                    {
                        string word=term.Substring(offset,length);Add(word,string.Join(" / ",data.Chinese[word]));offset+=length;
                    }
                    else
                    {
                        int start=offset++;
                        while(offset<term.Length && !Enumerable.Range(1,Math.Min(data.MaxChineseLength,term.Length-offset)).Any(n=>data.Chinese.ContainsKey(term.Substring(offset,n))))offset++;
                        Add(term[start..offset],"未收录");
                    }
                }
            }
        }
        string result=lines.Count==0 ? "没有可查询的词语。" : string.Join(Environment.NewLine,lines);
        if(lines.Count>=200)result+=Environment.NewLine+"已达到单次 200 个词条上限，请分段查询。";
        return Task.FromResult(result);
    }
    private static Lexicon Load()
    {
        using var stream=typeof(BasicDictionaryTranslator).Assembly.GetManifestResourceStream("Snip.Translation.Resources.basic-dictionary.json") ?? throw new InvalidOperationException("内置基础词典资源缺失。");
        var rows=JsonSerializer.Deserialize<string[][]>(stream) ?? throw new InvalidOperationException("内置基础词典无法读取。");
        var english=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var chinese=new Dictionary<string,List<string>>(StringComparer.Ordinal);int max=1;
        foreach(var row in rows)
        {
            english[row[0]]=row[1];
            foreach(var line in row[1].Split(';'))
            foreach(var part in Regex.Split(line,"[,，、；]"))
            {
                string meaning=Regex.Replace(part.Trim(),"^(?:[a-z]+\\.\\s*|\\[[^\\]]+\\]\\s*)+","").Trim();
                if(meaning.Length is <1 or >12 || meaning.Any(c=>c<'\u4e00' || c>'\u9fff'))continue;
                if(!chinese.TryGetValue(meaning,out var words)){words=[];chinese[meaning]=words;}
                if(words.Count<3 && !words.Contains(row[0]))words.Add(row[0]);
                max=Math.Max(max,meaning.Length);
            }
        }
        return new(english,chinese,max);
    }
}
