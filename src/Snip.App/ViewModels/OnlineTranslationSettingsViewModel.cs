using Snip.App.Mvvm;
using Snip.Core;
using Snip.Translation;

namespace Snip.App.ViewModels;

public sealed class OnlineTranslationSettingsViewModel : ObservableObject, IDisposable
{
    private readonly IOnlineTranslationSettingsStore store;
    private readonly Func<OnlineTranslationCredentials,ITranslationService> factory;
    private CancellationTokenSource? cancellation;
    private string provider=OnlineTranslationProviders.DeepSeek,endpoint="",model="",apiKey="",status="密钥仅加密保存在当前 Windows 用户下。";
    private bool defaultOnline,hasSavedKey,busy,disposed;
    public OnlineTranslationSettingsViewModel(IOnlineTranslationSettingsStore store,Func<OnlineTranslationCredentials,ITranslationService>? factory=null)
    {
        this.store=store;this.factory=factory ?? (credentials=>new OnlineTranslator(()=>credentials));
        SaveCommand=new(Save,()=>!busy && !disposed);
        TestCommand=new(TestAsync,error=>Status=error.Message,()=>!busy && !disposed);
        ClearKeyCommand=new(ClearKey,()=>!busy && !disposed && hasSavedKey);
        try { var active=store.Read();provider=active?.Provider ?? provider;LoadProfile(); }
        catch(InvalidOperationException error){Status=error.Message;LoadDefaults();}
    }
    public event EventHandler? Saved;
    public string[] Providers=>OnlineTranslationProviders.Names;
    public string Provider { get=>provider;set{if(value is not null && Set(ref provider,value)){apiKey="";LoadProfile();}} }
    public string Endpoint{get=>endpoint;set=>Set(ref endpoint,value);}
    public string Model{get=>model;set=>Set(ref model,value);}
    public bool DefaultOnline{get=>defaultOnline;set=>Set(ref defaultOnline,value);}
    public bool HasSavedKey{get=>hasSavedKey;private set{Set(ref hasSavedKey,value);Notify(nameof(KeyHint));ClearKeyCommand.Refresh();}}
    public string KeyHint=>HasSavedKey ? "已保存加密密钥；留空保留，填写新值则替换。" : "请填写 API Key，不要带 Bearer 前缀。";
    public string Status{get=>status;private set=>Set(ref status,value);}
    public bool CanEdit=>!busy;
    public RelayCommand SaveCommand{get;}
    public AsyncCommand TestCommand{get;}
    public RelayCommand ClearKeyCommand{get;}
    public void SetApiKey(string value)=>apiKey=value;
    private void LoadDefaults()
    {
        var defaults=OnlineTranslationProviders.Defaults(provider);Endpoint=defaults.Endpoint;Model=defaults.Model;HasSavedKey=false;
    }
    private void LoadProfile()
    {
        var saved=store.Read(provider);
        if(saved is null){LoadDefaults();DefaultOnline=store.Read()?.DefaultOnline ?? false;return;}
        Endpoint=saved.Endpoint;Model=saved.Model;DefaultOnline=saved.DefaultOnline;HasSavedKey=saved.HasApiKey;
    }
    private OnlineTranslationSettings Draft()=>new(provider,endpoint,model,defaultOnline);
    private void Save()
    {
        try
        {
            _=OnlineTranslator.ResolveEndpoint(endpoint);store.Save(Draft(),string.IsNullOrWhiteSpace(apiKey)?null:apiKey);
            apiKey="";HasSavedKey=store.Read(provider)?.HasApiKey ?? false;Status="已保存。在线模式只发送识别文字，按服务商规则计费。";
            Saved?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error){Status=error.Message;}
    }
    private void ClearKey()
    {
        try{store.Save(Draft() with { DefaultOnline=false },null,removeKey:true);apiKey="";DefaultOnline=false;HasSavedKey=false;Status="当前服务的密钥已清除，默认模式切回离线。";Saved?.Invoke(this,EventArgs.Empty);}
        catch(Exception error){Status=error.Message;}
    }
    private async Task TestAsync()
    {
        busy=true;Notify(nameof(CanEdit));SaveCommand.Refresh();ClearKeyCommand.Refresh();
        using var current=new CancellationTokenSource(TimeSpan.FromSeconds(65));cancellation=current;
        try
        {
            var settings=Draft();_ = OnlineTranslator.ResolveEndpoint(settings.Endpoint);
            string key=apiKey.Trim();
            if(key.Length==0)
            {
                var stored=store.Read(provider);
                if(stored?.Endpoint!=settings.Endpoint)throw new InvalidOperationException("请为这个 API 地址填写密钥。");
                key=store.ReadCredentials(provider).ApiKey;
            }
            Status="正在使用固定短句测试连接…";
            var service=factory(new(settings,key));
            var translated=await service.TranslateAsync("Hello world.","en-zh",current.Token);
            if(!disposed)Status=string.IsNullOrWhiteSpace(translated)?"服务返回空内容，请检查模型。":"连接成功，模型已返回译文。测试不会自动保存设置。";
        }
        catch(OperationCanceledException){if(!disposed)Status="测试已取消或超时。";}
        catch(Exception error){if(!disposed)Status=error.Message;}
        finally{cancellation=null;busy=false;if(!disposed){Notify(nameof(CanEdit));SaveCommand.Refresh();ClearKeyCommand.Refresh();}}
    }
    public void Dispose(){disposed=true;apiKey="";cancellation?.Cancel();}
}
