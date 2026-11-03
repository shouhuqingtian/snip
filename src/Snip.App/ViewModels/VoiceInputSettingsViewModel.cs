using Snip.App.Mvvm;
using Snip.App.Services;
using Snip.Core;

namespace Snip.App.ViewModels;

public sealed class VoiceInputSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ITranscriptionSettingsStore store;
    private readonly VoiceInputController controller;
    private string provider = "", endpoint = "", model = "", language = "", keyProfile = "不复用", apiKey = "", status = "保存后，把光标放到输入框，按住快捷键说话，松开后识别。";
    private bool enabled;
    private HotkeyGesture gesture;
    public VoiceInputSettingsViewModel(ITranscriptionSettingsStore store, IOnlineTranslationSettingsStore translation, VoiceInputController controller)
    {
        this.store = store; this.controller = controller;
        var initial = store.Read(); var preferences = store.ReadPreferences(); gesture = preferences.Hotkey; enabled = initial is null || preferences.Enabled;
        Providers = TranscriptionProviders.Names.Concat(store.ReadProfiles().Select(p => p.Provider)).Distinct().ToArray();
        KeyProfiles = new[] { "不复用" }.Concat(translation.ReadProfiles().Select(p => p.Provider)).ToArray();
        Load(initial?.Provider ?? TranscriptionProviders.Names[0]);
        SaveCommand = new(Save, () => CanEdit); CancelRecordingCommand = new(() => controller.Cancel(), () => controller.IsActive);
        controller.Changed += Changed;
    }
    public event EventHandler? SecretsCleared;
    public string[] Providers { get; }
    public string[] KeyProfiles { get; }
    public string[] Languages { get; } = ["自动（中英混合）", "中文", "英文"];
    public string[] Models => TranscriptionProviders.Models(Provider);
    public string Provider { get => provider; set { if (value != provider && CanEdit) Load(value); } }
    public string Endpoint { get => endpoint; set => Set(ref endpoint, value); }
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
    private void Load(string name)
    {
        var settings = store.Read(name) ?? TranscriptionProviders.Defaults(name);
        provider = name; endpoint = settings.Endpoint; model = settings.Model;
        language = settings.Language == "zh" ? Languages[1] : settings.Language == "en" ? Languages[2] : Languages[0];
        keyProfile = settings.TranslationProfile.Length > 0 ? settings.TranslationProfile : "不复用"; apiKey = "";
        Notify(nameof(Provider)); Notify(nameof(Endpoint)); Notify(nameof(Model)); Notify(nameof(Language)); Notify(nameof(KeyProfile)); Notify(nameof(ApiKey)); Notify(nameof(IndependentKey)); Notify(nameof(Models));
        SecretsCleared?.Invoke(this, EventArgs.Empty);
        Status = settings.HasApiKey ? "已有加密保存的密钥；留空继续使用。修改接口地址需要重新填写密钥。" : "填写语音识别模型和密钥，或选择同平台的翻译密钥。";
    }
    public void SetGesture(HotkeyGesture value)
    {
        try { value.Validate(); gesture = value; Notify(nameof(GestureText)); Status = "保存后按住 " + value.DisplayText + " 说话。"; }
        catch (ArgumentException error) { Status = error.Message; }
    }
    private void Save()
    {
        try
        {
            var settings = new TranscriptionSettings(Provider, Endpoint, Model, Language == Languages[1] ? "zh" : Language == Languages[2] ? "en" : "", KeyProfile == "不复用" ? "" : KeyProfile);
            controller.Save(settings, ApiKey, new(Enabled, gesture)); ApiKey = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); Status = controller.Status;
        }
        catch (Exception e) { Status = e is ArgumentException or VoiceInputException ? e.Message : "语音输入设置保存失败，原快捷键和设置保留。"; }
    }
    private void Changed(object? sender, EventArgs args)
    {
        Notify(nameof(CanEdit)); Notify(nameof(IndependentKey)); SaveCommand.Refresh(); CancelRecordingCommand.Refresh();
        if (controller.IsActive) Status = controller.Status;
    }
    public void Dispose() { controller.Changed -= Changed; ApiKey = ""; }
}
