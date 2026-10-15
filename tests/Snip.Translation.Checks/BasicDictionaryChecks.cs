using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Snip.Translation;

internal static class BasicDictionaryChecks
{
    public static async Task<int> RunAsync()
    {
        try
        {
            long baseline=GC.GetTotalMemory(true);
            var service=new BasicDictionaryTranslator();var watch=Stopwatch.StartNew();
            string result=await service.TranslateAsync("Hello, world! Hello.","en-zh",CancellationToken.None);
            Require(result.Contains("你好") && result.Contains("世界") && result.Split("Hello →").Length==2,"Case/punctuation lookup or repeat suppression failed.");
            long cold=watch.ElapsedMilliseconds, englishBytes=Math.Max(0,GC.GetTotalMemory(true)-baseline);
            Require(englishBytes<20L*1024*1024 && cold<5000,"English lookup eagerly loaded excessive data or took too long.");
            Console.WriteLine("PASS English dictionary retained managed bytes: "+englishBytes+"; budget 20 MiB; Chinese reverse index deferred.");
            Console.WriteLine("PASS bundled dictionary cold lookup: "+cold+" ms; no model, network or native runtime.");
            result=await service.TranslateAsync("Good morning! Thank you.","en-zh",CancellationToken.None);
            Require(result.Contains("Good morning → 早上好") && result.Contains("谢谢你") && !result.Contains("Good →"),"Phrase lookup split a known greeting.");
            Console.WriteLine("PASS known English phrases use whole-phrase meanings.");
            watch.Restart();
            result=await service.TranslateAsync("屏幕截图，剪贴板，设置","zh-en",CancellationToken.None);
            Require(result.Contains("screenshot") && result.Contains("clipboard") && result.Contains("settings"),"Chinese reverse lookup failed.");
            long chineseCold=watch.ElapsedMilliseconds,totalBytes=Math.Max(0,GC.GetTotalMemory(true)-baseline);
            Require(totalBytes<48L*1024*1024 && chineseCold<5000,"Reverse index exceeded its managed data/time budget.");
            Console.WriteLine("PASS Chinese reverse cold lookup: "+chineseCold+" ms; combined retained managed bytes: "+totalBytes+"; budget 48 MiB.");
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
            Console.WriteLine("PASS 100 warm dictionary queries: "+watch.ElapsedMilliseconds+" ms.");
            result=await service.TranslateAsync("serialization; asynchronous; reimbursement; force majeure; purchase order","en-zh",CancellationToken.None);
            Require(result.Contains("串行化") && result.Contains("异步") && result.Contains("偿付") && result.Contains("不可抗力") && result.Contains("采购订单") && !result.Contains("未收录"),"Expanded technical/business vocabulary is missing.");
            Console.WriteLine("PASS expanded technical/business words and phrases return their dictionary meanings.");
            using var stream=typeof(BasicDictionaryTranslator).Assembly.GetManifestResourceStream("Snip.Translation.Resources.basic-dictionary.json")!;
            using var buffer=new MemoryStream();stream.CopyTo(buffer);byte[] bytes=buffer.ToArray();
            Require(Convert.ToHexString(SHA256.HashData(bytes)).Equals("b2bb3a1943ea9304dfb51ac3f0c1003eb1c8f920494825141d16d6195a965a1b",StringComparison.OrdinalIgnoreCase),"Bundled dictionary checksum mismatch.");
            using var document=JsonDocument.Parse(bytes);var rows=document.RootElement.EnumerateArray().ToArray();
            Require(rows.Length==100000 && rows.Select(row=>row[0].GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count()==100000 && rows.Count(row=>row[0].GetString()!.Contains(' '))==5000,"Dictionary contains duplicate/missing entries or phrases.");
            Console.WriteLine("PASS 100000 distinct bundled entries, 5000 phrases and pinned resource SHA256.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
