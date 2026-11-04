using Snip.App.Mvvm;
using Snip.App.Services;
using Snip.Core;
using System.Collections.ObjectModel;

namespace Snip.App.ViewModels;

public sealed class VoiceInputSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ITranscriptionSettingsStore store;
    private readonly VoiceInputController controller;
    private string provider = "", endpoint = "", model = "", language = "", keyProfile = "不复用", apiKey = "", status = "保存后，把光标放到输入框，按住快捷键说话，松开后识别。";
    private bool enabled;
    private HotkeyGesture gesture;
    private VoiceInputMode mode;
    private TranscriptionProtocol protocol;
    private string newProfileName = "";
    public VoiceInputSettingsViewModel(ITranscriptionSettingsStore store, IOnlineTranslationSettingsStore translation, VoiceInputController controller)
    {
        this.store = store; this.controller = controller;
        var initial = store.Read(); var preferences = store.ReadPreferences(); gesture = preferences.Hotkey; enabled = initial is null || preferences.Enabled; mode = preferences.Mode;
        Providers = new(TranscriptionProviders.Names.Concat(store.ReadProfiles().Select(p => p.Provider)).Distinct());
        KeyProfiles = new[] { "不复用" }.Concat(translation.ReadProfiles().Select(p => p.Provider)).ToArray();
        Load(initial?.Provider ?? TranscriptionProviders.Names[0]);
        if (controller.LastFailure.Length > 0) Status = "上次语音输入未完成：" + controller.LastFailure;
        SaveCommand = new(Save, () => CanEdit); CancelRecordingCommand = new(() => controller.Cancel(), () => controller.IsActive);
        AddProfileCommand = new(AddProfile, () => CanEdit);
        controller.Changed += Changed;
    }
    public event EventHandler? SecretsCleared;
    public ObservableCollection<string> Providers { get; }
    public string[] Protocols { get; } = ["OpenAI 兼容语音识别", "阿里云百炼 · Qwen ASR", "智谱 · GLM ASR"];
    public int ProtocolIndex
    {
        get => (int)protocol;
        set
        {
            if (value is not (>= 0 and <= 2) || !CanEdit || (int)protocol == value) return;
            protocol = (TranscriptionProtocol)value;
            var defaults = TranscriptionProviders.Defaults(protocol == TranscriptionProtocol.Qwen ? "阿里云百炼" : protocol == TranscriptionProtocol.Zhipu ? "智谱AI" : "自定义（兼容接口）");
            if (string.IsNullOrWhiteSpace(Endpoint)) Endpoint = defaults.Endpoint;
            if (string.IsNullOrWhiteSpace(Model)) Model = defaults.Model;
            Notify(nameof(ProtocolIndex)); Notify(nameof(EndpointHint)); Notify(nameof(Models));
        }
    }
    public string NewProfileName { get => newProfileName; set => Set(ref newProfileName, value); }
    public RelayCommand AddProfileCommand { get; }
    public string[] KeyProfiles { get; }
    public string[] Languages { get; } = ["自动（中英混合）", "中文", "英文"];
    public string[] InputModes { get; } = ["按住说话，松开结束", "按一次开始，再按一次结束"];
    public int ModeIndex
    {
        get => (int)mode;
        set
        {
            if (value is < 0 or > 1 || !CanEdit || (int)mode == value) return;
            mode = (VoiceInputMode)value; Notify(nameof(ModeIndex)); Notify(nameof(ModeHint)); Status = "保存后，" + ModeHint;
        }
    }
    public string ModeHint => mode == VoiceInputMode.Toggle ? "按一次 " + gesture.DisplayText + " 开始录音，再按一次结束并识别。" : "按住 " + gesture.DisplayText + " 说话，松开后识别并输入。";
    public string[] Models => TranscriptionProviders.Models(Provider) is { Length: > 0 } models ? models : TranscriptionProviders.Models(protocol == TranscriptionProtocol.Qwen ? "阿里云百炼" : protocol == TranscriptionProtocol.Zhipu ? "智谱AI" : "自定义（兼容接口）");
    public string Provider { get => provider; set { if (value != provider && CanEdit) Load(value); } }
    public string Endpoint { get => endpoint; set { if (Set(ref endpoint, value)) Notify(nameof(EndpointHint)); } }
    public string EndpointHint => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && (uri.Host.Equals("minimaxi.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".minimaxi.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("minimax.io", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".minimax.io", StringComparison.OrdinalIgnoreCase))
        ? "这是 MiniMax 地址，本程序尚未适配其专用语音识别协议。请勿根据发音接口猜测识别地址或模型；可以改选已有的识别预设，填写对应平台密钥。"
        : protocol == TranscriptionProtocol.Qwen ? "使用百炼原生多模态语音识别接口与百炼密钥；可编辑地域地址和模型，密钥地域须一致。"
        : protocol == TranscriptionProtocol.Zhipu ? "使用智谱音频识别接口与智谱密钥；语言由模型自动判断。"
        : "使用 OpenAI 兼容的语音转文字接口。服务、地址、模型和密钥须对应同一平台；选择其他原生接口时须同时切换协议。";
    public string Model { get => model; set => Set(ref model, value); }
    public string Language { get => language; set => Set(ref language, value); }
    public string ApiKey { get => apiKey; set => Set(ref apiKey, value); }
    public string KeyProfile { get => keyProfile; set { if (Set(ref keyProfile, value)) { ApiKey = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); Notify(nameof(IndependentKey)); } } }
    public bool IndependentKey => KeyProfile == "不复用" && CanEdit;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public bool CanEdit => !controller.IsActive;
    public string GestureText => gesture.DisplayText;
    public string Status { get => status; private set => Set(ref status, value); }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelRecordingCommand { get; }
    private void AddProfile()
    {
        string name = NewProfileName.Trim();
        if (name.Length is < 1 or > 64 || name.Any(char.IsControl)) { Status = "请填写 1～64 字的配置名称。"; return; }
        if (Providers.Contains(name)) { Status = "该名称已存在，请直接选择或换一个名称。"; return; }
        if (store.ReadProfiles().Count >= 32) { Status = "最多保存 32 组识别配置。"; return; }
        Providers.Add(name); Load(name); NewProfileName = "";
        Status = "已新增配置草稿：请选择协议，填写地址、模型和对应平台密钥，再保存并使用。";
    }
    private void Load(string name)
    {
        var settings = store.Read(name) ?? TranscriptionProviders.Defaults(name);
        provider = name; endpoint = settings.Endpoint; model = settings.Model;
        protocol = settings.Protocol;
        language = settings.Language == "zh" ? Languages[1] : settings.Language == "en" ? Languages[2] : Languages[0];
        keyProfile = settings.TranslationProfile.Length > 0 ? settings.TranslationProfile : "不复用"; apiKey = "";
        Notify(nameof(Provider)); Notify(nameof(Endpoint)); Notify(nameof(EndpointHint)); Notify(nameof(Model)); Notify(nameof(Language)); Notify(nameof(KeyProfile)); Notify(nameof(ApiKey)); Notify(nameof(IndependentKey)); Notify(nameof(Models)); Notify(nameof(ProtocolIndex));
        SecretsCleared?.Invoke(this, EventArgs.Empty);
        Status = settings.HasApiKey ? "已有加密保存的密钥；留空继续使用。修改接口地址需要重新填写密钥。" : "填写语音识别模型和密钥，或选择同平台的翻译密钥。";
    }
    public bool SetGesture(HotkeyGesture value)
    {
        try { value.Validate(); gesture = value; Notify(nameof(GestureText)); Notify(nameof(ModeHint)); Status = "保存后，" + ModeHint; return true; }
        catch (ArgumentException error) { Status = error.Message; return false; }
    }
    private void Save()
    {
        try
        {
            var settings = new TranscriptionSettings(Provider, Endpoint, Model, Language == Languages[1] ? "zh" : Language == Languages[2] ? "en" : "", KeyProfile == "不复用" ? "" : KeyProfile, Protocol: protocol);
            controller.Save(settings, ApiKey, new(Enabled, gesture, mode)); ApiKey = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); Status = controller.Status;
        }
        catch (Exception e) { Status = e is ArgumentException or VoiceInputException ? e.Message : "语音输入设置保存失败，原快捷键和设置保留。"; }
    }
    private void Changed(object? sender, EventArgs args)
    {
        Notify(nameof(CanEdit)); Notify(nameof(IndependentKey)); SaveCommand.Refresh(); CancelRecordingCommand.Refresh(); AddProfileCommand.Refresh();
        if (controller.IsActive || controller.LastFailure.Length > 0) Status = controller.Status;
    }
    public void Dispose() { controller.Changed -= Changed; ApiKey = ""; }
}
