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
            await Check("availability is local and tolerates absent or invalid online configuration",async()=>
            {
                using var client=new HttpClient(new Handler((_,_)=>throw new InvalidOperationException("Availability contacted a provider.")));
                var configured=OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek);
                foreach(var credentials in new Func<OnlineTranslationCredentials>[]
                {
                    ()=>new(configured,""),
                    ()=>new(configured with { Model="" },"PUBLIC-TEST-KEY"),
                    ()=>new(configured with { Endpoint="http://remote.example.com/v1" },"PUBLIC-TEST-KEY"),
                    ()=>throw new InvalidOperationException("Settings not configured."),
                    ()=>throw new System.Security.Cryptography.CryptographicException("Public fixture: credentials unavailable."),
                    ()=>throw new UnauthorizedAccessException("Public fixture: settings inaccessible.")
                })
                    Require(!await new OnlineTranslator(credentials,client).IsAvailableAsync(CancellationToken.None),"Unavailable configuration accepted.");
                Require(await new OnlineTranslator(()=>new(configured,"PUBLIC-TEST-KEY"),client).IsAvailableAsync(CancellationToken.None),"Configured online service rejected.");
            });
            await Check("availability preserves cancellation",async()=>
            {
                using var cancelled=new CancellationTokenSource();cancelled.Cancel();
                var service=new OnlineTranslator(()=>throw new InvalidOperationException("Credentials must not be read after cancellation."));
                try{await service.IsAvailableAsync(cancelled.Token);throw new Exception("Availability ignored cancellation.");}
                catch(OperationCanceledException)when(cancelled.IsCancellationRequested){}
            });
            await Check("fresh offline availability does not create or download model files",async()=>
            {
                string folder=Path.Combine(Path.GetTempPath(),"Snip.NoModels-"+Guid.NewGuid());
                var models=new ModelStore(folder);
                Require(!await new OfflineTranslator(models).IsAvailableAsync(CancellationToken.None),"Fresh installation has a translation model.");
                Require(!Directory.Exists(folder),"Availability created model files.");
            });
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
            await Check("custom model and endpoint are sent without provider-specific options",async()=>
            {
                var configured=new OnlineTranslationSettings("自定义：备用账号","https://custom.example.com/gateway/chat/completions","my-translation-model");
                using var client=new HttpClient(new Handler(async(request,token)=>
                {
                    Require(request.RequestUri!.AbsolutePath=="/gateway/chat/completions","Custom endpoint changed.");
                    using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                    Require(body.RootElement.GetProperty("model").GetString()=="my-translation-model" && !body.RootElement.TryGetProperty("enable_thinking",out _),"Custom model or capability options changed.");
                    return Response(HttpStatusCode.OK,"{\"choices\":[{\"message\":{\"content\":\"自定义译文\"}}]}");
                }));
                Require(await new OnlineTranslator(()=>new(configured,"PUBLIC-CUSTOM-KEY"),client).TranslateAsync("Public fixture","en-zh",CancellationToken.None)=="自定义译文","Custom translation failed.");
            });
            await Check("service failure does not automatically send text to another provider",async()=>
            {
                int calls=0;
                using var client=new HttpClient(new Handler((_,_)=>{calls++;return Task.FromResult(Response(HttpStatusCode.ServiceUnavailable,"PRIVATE"));}));
                var service=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-KEY"),client);
                try { await service.TranslateAsync("Public fixture","en-zh",CancellationToken.None);throw new Exception("Service failure accepted."); }
                catch(InvalidOperationException error){Require(calls==1 && error.Message.Contains("切换服务"),"Unexpected failover or missing recovery guidance.");}
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
