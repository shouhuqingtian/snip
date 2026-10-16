using System.Collections.ObjectModel;
using Snip.App.Mvvm;
using Snip.Core;
using Snip.Translation;

namespace Snip.App.ViewModels;

public sealed class OnlineSpeechSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ISpeechSettingsStore store;
    private readonly IOnlineTranslationSettingsStore translation;
    private readonly Func<OnlineSpeechCredentials, IOnlineSpeechSynthesizer> factory;
    private readonly ISpeechAudioPlayer player;
    private string provider = SpeechProviders.Names[0], endpoint = "", model = "", voice = "", apiKey = "", customName = "", translationProfile = "独立密钥";
    private string status = "仅试听或点击喇叭时联网发送文字；密钥在当前 Windows 用户下加密保存。";
    private SpeechProtocol protocol; private SpeechMode defaultMode;
    private double speed = 1;
    private bool savedKey, busy, disposed;
    private CancellationTokenSource? cancellation;
    public OnlineSpeechSettingsViewModel(ISpeechSettingsStore store, IOnlineTranslationSettingsStore translation,
        Func<OnlineSpeechCredentials, IOnlineSpeechSynthesizer> factory, ISpeechAudioPlayer player)
    {
        this.store = store; this.translation = translation; this.factory = factory; this.player = player;
        SaveCommand = new(Save, () => !busy && !disposed);
        PreviewCommand = new(PreviewAsync, _ => Status = "语音试听未完成，请检查配置和音频设备。", () => !busy && !disposed);
        StopCommand = new(() => { if (cancellation is { } current) _ = current.CancelAsync(); }, () => busy);
        AddCommand = new(Add, () => !busy && !disposed);
        DeleteCommand = new(Delete, () => !busy && !disposed && !SpeechProviders.Names.Contains(provider));
        ClearKeyCommand = new(ClearKey, () => !busy && !disposed && savedKey);
        try
        {
            foreach (var profile in store.ReadProfiles()) if (!Providers.Contains(profile.Provider)) Providers.Add(profile.Provider);
            foreach (var profile in translation.ReadProfiles()) TranslationProfiles.Add(profile.Provider);
            defaultMode = store.ReadMode(); provider = store.Read()?.Provider ?? provider; Load();
        }
        catch (Exception) { Defaults(); Status = "语音配置读取失败，可重新填写保存；原文和译文仍可使用。"; }
    }
    public event EventHandler? Saved;
    public ObservableCollection<string> Providers { get; } = new(SpeechProviders.Names);
    public ObservableCollection<string> TranslationProfiles { get; } = new(["独立密钥"]);
    public string[] SuggestedModels => SpeechProviders.Models(provider);
    public string[] SuggestedVoices => SpeechProviders.Voices(provider);
    public string Provider
    {
        get => provider;
        set { if (busy || disposed || value == provider || !Providers.Contains(value)) return; provider = value; apiKey = ""; try { Load(); } catch { Defaults(); Status = "配置读取失败，可重新填写保存。"; } Notify(); Notify(nameof(SuggestedModels)); Notify(nameof(SuggestedVoices)); DeleteCommand.Refresh(); }
    }
    public string Endpoint { get => endpoint; set => Set(ref endpoint, value); }
    public string Model { get => model; set => Set(ref model, value); }
    public string Voice { get => voice; set => Set(ref voice, value); }
    public SpeechProtocol Protocol { get => protocol; set => Set(ref protocol, value); }
    public SpeechMode DefaultMode { get => defaultMode; set => Set(ref defaultMode, value); }
    public double Speed { get => speed; set => Set(ref speed, value); }
    public string TranslationProfile { get => translationProfile; set { if (Set(ref translationProfile, value)) Notify(nameof(UsesOwnKey)); } }
    public bool UsesOwnKey => translationProfile == "独立密钥";
    public string CustomName { get => customName; set => Set(ref customName, value); }
    public string KeyHint => savedKey ? "已保存加密密钥；同一 API 地址留空保留，换地址需重新填写。" : "请填写语音服务的 API Key，或选择同域名/地域的翻译配置复用密钥。";
    public string Status { get => status; private set => Set(ref status, value); }
    public bool CanEdit => !busy;
    public RelayCommand SaveCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ClearKeyCommand { get; }
    public void SetApiKey(string value) => apiKey = value;
    private void Defaults()
    {
        var preset = SpeechProviders.Defaults(provider);
        Endpoint = preset.Endpoint; Model = preset.Model; Voice = preset.Voice; Protocol = preset.Protocol; Speed = 1; savedKey = false; TranslationProfile = "独立密钥"; Notify(nameof(KeyHint)); ClearKeyCommand.Refresh();
    }
    private void Load()
    {
        var saved = store.Read(provider);
        if (saved is null) { Defaults(); return; }
        Endpoint = saved.Endpoint; Model = saved.Model; Voice = saved.Voice; Protocol = saved.Protocol; Speed = saved.Speed;
        if (saved.TranslationProfile.Length > 0 && !TranslationProfiles.Contains(saved.TranslationProfile)) TranslationProfiles.Add(saved.TranslationProfile);
        TranslationProfile = saved.TranslationProfile.Length == 0 ? "独立密钥" : saved.TranslationProfile;
        savedKey = saved.HasApiKey; Notify(nameof(KeyHint)); ClearKeyCommand.Refresh();
    }
    private OnlineSpeechSettings Draft() => new(provider, Protocol, Endpoint, Model, Voice, Speed, UsesOwnKey ? "" : TranslationProfile);
    private void Save()
    {
        try { store.Save(Draft(), apiKey, DefaultMode); apiKey = ""; Load(); Status = "已保存语音服务、音色和默认发音方式；返回文字工作台点喇叭即可。"; Saved?.Invoke(this, EventArgs.Empty); }
        catch (Exception error) { Status = error is ArgumentException or InvalidOperationException ? error.Message : "语音设置保存失败，请检查设置目录权限。"; }
    }
    private void Add()
    {
        string name = CustomName.Trim();
        if (name.Length is 0 or > 48 || name.Any(char.IsControl) || Providers.Contains(name)) { Status = "请填写未使用的 1–48 字符配置名称。"; return; }
        Providers.Add(name); Provider = name; CustomName = ""; Status = "请填写协议、完整语音 API 地址、模型和音色；自定义接口需要兼容所选协议。";
    }
    private void Delete()
    {
        try { store.DeleteProfile(provider); string previous = provider; Providers.Remove(previous); provider = ""; Provider = Providers[0]; DefaultMode = store.ReadMode(); Status = "已删除配置；删除正在使用的配置时会切回系统语音。"; Saved?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { Status = "删除语音配置失败，原配置保留。"; }
    }
    private void ClearKey()
    {
        try { store.Save(Draft(), null, SpeechMode.System, removeKey: true); DefaultMode = SpeechMode.System; apiKey = ""; Load(); Status = "独立语音密钥已清除，默认切回系统语音；复用的翻译密钥不受影响。"; Saved?.Invoke(this, EventArgs.Empty); }
        catch (Exception) { Status = "清除语音密钥失败，请检查配置。"; }
    }
    private async Task PreviewAsync()
    {
        busy = true; Refresh();
        using var current = new CancellationTokenSource(TimeSpan.FromSeconds(75)); cancellation = current;
        try
        {
            var draft = Draft(); SpeechProviders.Validate(draft); string key = apiKey.Trim();
            if (!UsesOwnKey)
            {
                var shared = translation.ReadCredentials(TranslationProfile);
                if (!SpeechProviders.SameService(SpeechProviders.Endpoint(Endpoint), SpeechProviders.Endpoint(shared.Settings.Endpoint))) throw new SpeechServiceException("语音与翻译 API 域名或地域不一致，不能复用密钥。");
                key = shared.ApiKey;
            }
            else if (key.Length == 0)
            {
                var saved = store.Read(provider);
                if (saved?.Endpoint != Endpoint || saved.TranslationProfile.Length > 0) throw new SpeechServiceException("请为当前语音 API 地址填写独立密钥。");
                // Do not activate another profile or save the draft just to preview it.
                key = store.ReadCredentials(provider).ApiKey;
            }
            Status = "正在用固定中英短句试听，不发送截图或识别文字；本次试听不会自动保存设置。";
            var audio = await factory(new(draft, key)).SynthesizeAsync("你好，欢迎使用自用截图。 Hello, welcome to Self Snip.", "zh", current.Token);
            await player.PlayAsync(audio, Speed, current.Token);
            if (!disposed) Status = "试听完成；满意后点击保存。";
        }
        catch (OperationCanceledException) { if (!disposed) Status = "试听已停止或超时。"; }
        catch (Exception error) { if (!disposed) Status = error is SpeechServiceException ? error.Message : "试听失败，请检查协议、模型、音色、密钥或音频输出设备。"; }
        finally { cancellation = null; busy = false; Refresh(); }
    }
    private void Refresh() { Notify(nameof(CanEdit)); SaveCommand.Refresh(); PreviewCommand.Refresh(); StopCommand.Refresh(); AddCommand.Refresh(); DeleteCommand.Refresh(); ClearKeyCommand.Refresh(); }
    public void Dispose() { disposed = true; apiKey = ""; if (cancellation is { } current) _ = current.CancelAsync(); Refresh(); }
}
