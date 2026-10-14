using System.Net;
using System.Text;
using System.Text.Json;
using Snip.Core;
using Snip.Translation;

internal static class OnlineTranslationChecks
{
    public static async Task<int> RunAsync()
    {
        int passed=0;
        async Task Check(string name,Func<Task> action){await action();passed++;Console.WriteLine("PASS "+name);}
        try
        {
            foreach(var provider in OnlineTranslationProviders.Names)
                await Check(provider+" text-only protocol and endpoint",async()=>
                {
                    var configured=OnlineTranslationProviders.Defaults(provider);
                    using var handler=new Handler(async(request,token)=>
                    {
                        Require(request.Method==HttpMethod.Post && request.RequestUri==OnlineTranslator.ResolveEndpoint(configured.Endpoint),"Wrong endpoint.");
                        Require(request.Headers.Authorization?.Scheme=="Bearer" && request.Headers.Authorization.Parameter=="PUBLIC-TEST-KEY","Authentication missing.");
                        using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));var root=body.RootElement;
                        Require(root.GetProperty("model").GetString()==configured.Model && !root.GetProperty("stream").GetBoolean(),"Wrong model or streaming mode.");
                        Require(root.GetProperty("messages").GetArrayLength()==2 && root.GetProperty("messages")[1].GetProperty("content").GetString()=="PUBLIC text\n第二行","Text was lost.");
                        Require(!root.ToString().Contains("PUBLIC-TEST-KEY") && !root.ToString().Contains("image_url"),"Request leaked key into content or sent an image.");
                        if(provider==OnlineTranslationProviders.Qwen)Require(!root.GetProperty("enable_thinking").GetBoolean(),"Qwen nonstream request needs thinking disabled.");
                        return Response(HttpStatusCode.OK,"{\"choices\":[{\"message\":{\"content\":\"译文\\n第二行\",\"reasoning_content\":\"PUBLIC reasoning ignored\"}}]}");
                    });
                    using var client=new HttpClient(handler);var service=new OnlineTranslator(()=>new(configured,"PUBLIC-TEST-KEY"),client);
                    Require(await service.TranslateAsync("PUBLIC text\n第二行","en-zh",CancellationToken.None)=="译文\n第二行","Translation content parsing failed.");
                });
            await Check("complete endpoints are not duplicated",()=>
            {
                Require(OnlineTranslator.ResolveEndpoint("https://api.example.com/v1/chat/completions").AbsolutePath=="/v1/chat/completions","Path duplicated.");
                Require(OnlineTranslator.ResolveEndpoint("https://api.example.com/v1/").AbsolutePath=="/v1/chat/completions","Base path wrong.");return Task.CompletedTask;
            });
            foreach(var code in new[]{HttpStatusCode.Unauthorized,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.InternalServerError,HttpStatusCode.Redirect})
                await Check("HTTP "+(int)code+" gives sanitized failure",async()=>
                {
                    using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(code,"PRIVATE provider body containing key and input"))));
                    var service=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-TEST-KEY"),client);
                    try{await service.TranslateAsync("PUBLIC input","en-zh",CancellationToken.None);throw new Exception("Error status accepted.");}
                    catch(InvalidOperationException error){Require(!error.ToString().Contains("PRIVATE") && !error.ToString().Contains("PUBLIC-TEST-KEY"),"Provider error leaked content.");}
                });
            foreach(var data in new[]{"not-json","{}","{\"choices\":[]}","{\"choices\":[{\"message\":{\"content\":\"\"}}]}"})
                await Check("malformed or empty response is actionable",async()=>
                {
                    using var client=new HttpClient(new Handler((_,_)=>Task.FromResult(Response(HttpStatusCode.OK,data))));
                    var service=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-TEST-KEY"),client);
                    try{await service.TranslateAsync("PUBLIC input","en-zh",CancellationToken.None);throw new Exception("Malformed response accepted.");}
                    catch(InvalidOperationException error){Require(!error.Message.Contains("PUBLIC input"),"Malformed response leaked original.");}
                });
            await Check("cancellation interrupts request",async()=>
            {
                using var current=new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
                using var client=new HttpClient(new Handler(async(_,token)=>{await Task.Delay(Timeout.Infinite,token);return Response(HttpStatusCode.OK,"{}");}));
                var service=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-TEST-KEY"),client);
                try{await service.TranslateAsync("PUBLIC input","en-zh",current.Token);throw new Exception("Cancellation ignored.");}
                catch(OperationCanceledException)when(current.IsCancellationRequested){}
            });
            await Check("network failure does not expose endpoint or key",async()=>
            {
                using var client=new HttpClient(new Handler((_,_)=>throw new HttpRequestException("PRIVATE endpoint")));
                var service=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-TEST-KEY"),client);
                try{await service.TranslateAsync("PUBLIC input","en-zh",CancellationToken.None);throw new Exception("Network failure ignored.");}
                catch(InvalidOperationException error){Require(!error.ToString().Contains("PRIVATE"),"Network details exposed.");}
            });
            Console.WriteLine(passed+" deterministic online protocol checks passed; no external request or credential used.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static HttpResponseMessage Response(HttpStatusCode code,string text)=>new(code){Content=new StringContent(text,Encoding.UTF8,"application/json")};
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token);}
}
