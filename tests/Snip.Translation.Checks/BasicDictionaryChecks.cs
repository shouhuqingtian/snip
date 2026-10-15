using System.Diagnostics;
using Snip.Translation;

internal static class BasicDictionaryChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            var service=new BasicDictionaryTranslator();var watch=Stopwatch.StartNew();
            string result=await service.TranslateAsync("Hello, world! Hello.","en-zh",CancellationToken.None);
            Require(result.Contains("你好") && result.Contains("世界") && result.Split("Hello →").Length==2,"Case/punctuation lookup or repeat suppression failed.");
            Console.WriteLine("PASS bundled dictionary cold lookup: "+watch.ElapsedMilliseconds+" ms; no model, network or native runtime.");
            result=await service.TranslateAsync("Good morning! Thank you.","en-zh",CancellationToken.None);
            Require(result.Contains("Good morning → 早上好") && result.Contains("谢谢你") && !result.Contains("Good →"),"Phrase lookup split a known greeting.");
            Console.WriteLine("PASS known English phrases use whole-phrase meanings.");
            result=await service.TranslateAsync("屏幕截图，剪贴板，设置","zh-en",CancellationToken.None);
            Require(result.Contains("screenshot") && result.Contains("clipboard") && result.Contains("settings"),"Chinese reverse lookup failed.");
            Console.WriteLine("PASS Chinese terms map to English dictionary entries.");
            result=await service.TranslateAsync("我，水，爱","zh-en",CancellationToken.None);
            Require(result.Contains("我 → i",StringComparison.OrdinalIgnoreCase) && result.Contains("water") && result.Contains("love"),"Common one-character Chinese words were omitted.");
            Console.WriteLine("PASS one-character Chinese pronoun/noun/verb lookup.");
            result=await service.TranslateAsync("qzxvunknownword","en-zh",CancellationToken.None);
            Require(result=="qzxvunknownword → 未收录","Unknown term was invented or silently discarded.");
            Console.WriteLine("PASS unknown terms are retained and explicitly marked.");
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try{await service.TranslateAsync("Hello","en-zh",cancelled.Token);throw new Exception("Cancellation ignored.");}catch(OperationCanceledException)when(cancelled.IsCancellationRequested){}
            try{await service.TranslateAsync(new string('a',12001),"en-zh",CancellationToken.None);throw new Exception("Input limit ignored.");}catch(InvalidOperationException){}
            result=await service.TranslateAsync(string.Join(" ",Enumerable.Range(1,300).Select(i=>"unknownword"+i)),"en-zh",CancellationToken.None);
            Require(result.Split(Environment.NewLine).Length==201 && result.Contains("200 个词条"),"Lookup output limit failed.");
            Console.WriteLine("PASS dictionary cancellation and bounded input/output.");
            watch.Restart();
            for(int i=0;i<100;i++)await service.TranslateAsync("screenshot clipboard computer configuration translation","en-zh",CancellationToken.None);
            Require(watch.Elapsed<TimeSpan.FromSeconds(5),"Warm dictionary lookup is unexpectedly slow.");
            Console.WriteLine("PASS 100 warm dictionary queries: "+watch.ElapsedMilliseconds+" ms.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
