using System.Collections.ObjectModel;
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
    private string customProfileName="", visionModel="";
    private bool defaultOnline,hasSavedKey,busy,disposed;
    public OnlineTranslationSettingsViewModel(IOnlineTranslationSettingsStore store,Func<OnlineTranslationCredentials,ITranslationService>? factory=null)
    {
        this.store=store;this.factory=factory ?? (credentials=>new OnlineTranslator(()=>credentials));
        SaveCommand=new(Save,()=>!busy && !disposed);
        TestCommand=new(TestAsync,error=>Status=error.Message,()=>!busy && !disposed);
        ClearKeyCommand=new(ClearKey,()=>!busy && !disposed && hasSavedKey);
        AddProfileCommand=new(AddProfile,()=>!busy && !disposed);
        DeleteProfileCommand=new(DeleteProfile,()=>!busy && !disposed && IsCustomProfile);
        try { RefreshProfiles();var active=store.Read();provider=active?.Provider ?? provider;LoadProfile(); }
        catch(InvalidOperationException error){Status=error.Message;LoadDefaults();}
    }
    public event EventHandler? Saved;
    public ObservableCollection<string> Providers { get; } = new(OnlineTranslationProviders.Names);
    public string[] SuggestedModels => OnlineTranslationProviders.Models(provider);
    public string CustomProfileName { get=>customProfileName;set=>Set(ref customProfileName,value); }
    public bool IsCustomProfile => !OnlineTranslationProviders.Names.Contains(provider);
    public string Provider
    {
        get=>provider;
        set
        {
            if (busy || disposed || value is null || !Providers.Contains(value) || value==provider) return;
            provider=value;apiKey="";
            Notify(nameof(Provider));Notify(nameof(SuggestedModels));Notify(nameof(SuggestedVisionModels));
            LoadProfile();Notify(nameof(IsCustomProfile));DeleteProfileCommand.Refresh();
        }
    }
    public string VisionModel { get=>visionModel;set=>Set(ref visionModel,value); }
    public string[] SuggestedVisionModels => OnlineTranslationProviders.VisionModels(provider);
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
    public RelayCommand AddProfileCommand{get;}
    public RelayCommand DeleteProfileCommand{get;}
    private void RefreshProfiles()
    {
        foreach(var profile in store.ReadProfiles())
            if(!Providers.Contains(profile.Provider))Providers.Add(profile.Provider);
    }
    private void AddProfile()
    {
        string name=customProfileName.Trim();
        if(name.Length is 0 or > 48 || name.Any(char.IsControl)){Status="请填写 1–48 个字符的配置名称，不能包含控制字符。";return;}
        string identity=OnlineTranslationProviders.CustomPrefix+name;
        if(Providers.Contains(identity)){Status="这个配置名称已存在，请选择该配置或换一个名称。";return;}
        if(Providers.Count>=32){Status="最多保留 32 组配置，请先删除不再使用的自定义配置。";return;}
        Providers.Add(identity);Provider=identity;CustomProfileName="";
        Status="请填写兼容 OpenAI Chat Completions 的地址、模型和密钥，然后保存；不会自动调用。";
    }
    private void DeleteProfile()
    {
        try
        {
            string removed=provider;store.DeleteProfile(removed);apiKey="";
            Provider=store.Read()?.Provider ?? OnlineTranslationProviders.DeepSeek;
            // The active saved entry may be the deleted unsaved draft only in malformed external configuration.
            if(provider==removed)Provider=OnlineTranslationProviders.DeepSeek;
            Providers.Remove(removed);Status="已删除当前自定义配置，其余服务配置保留。";Saved?.Invoke(this,EventArgs.Empty);
        }
        catch(Exception error){Status=error.Message;}
    }
    public void SetApiKey(string value)=>apiKey=value;
    private void LoadDefaults()
    {
        var defaults=OnlineTranslationProviders.Defaults(provider);Endpoint=defaults.Endpoint;Model=defaults.Model;VisionModel="";HasSavedKey=false;
    }
    private void LoadProfile()
    {
        var saved=store.Read(provider);
        if(saved is null){LoadDefaults();DefaultOnline=store.Read()?.DefaultOnline ?? false;return;}
        Endpoint=saved.Endpoint;Model=saved.Model;VisionModel=saved.VisionModel;DefaultOnline=saved.DefaultOnline;HasSavedKey=saved.HasApiKey;
    }
    private OnlineTranslationSettings Draft()=>new(provider,endpoint,model,defaultOnline,VisionModel:visionModel);
    private void Save()
    {
        try
        {
            _=OnlineTranslator.ResolveEndpoint(endpoint);store.Save(Draft(),string.IsNullOrWhiteSpace(apiKey)?null:apiKey);
            apiKey="";HasSavedKey=store.Read(provider)?.HasApiKey ?? false;Status="已保存并选用此服务。返回文字工作台点“在线翻译”即可重试；不会自动切换或发送给其他服务。";
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
        busy=true;Notify(nameof(CanEdit));SaveCommand.Refresh();ClearKeyCommand.Refresh();AddProfileCommand.Refresh();DeleteProfileCommand.Refresh();
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
        finally{cancellation=null;busy=false;if(!disposed){Notify(nameof(CanEdit));SaveCommand.Refresh();ClearKeyCommand.Refresh();AddProfileCommand.Refresh();DeleteProfileCommand.Refresh();}}
    }
    public void Dispose(){disposed=true;apiKey="";cancellation?.Cancel();}
}
