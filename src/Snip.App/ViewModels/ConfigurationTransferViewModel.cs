using System.IO;
using System.Security.Cryptography;
using Snip.App.Mvvm;
using Snip.Infrastructure;

namespace Snip.App.ViewModels;

/// <summary>Passwords are transient; preview retains only destination-protected files.</summary>
public sealed class ConfigurationTransferViewModel : ObservableObject, IDisposable
{
    private readonly SettingsConfigurationTransfer transfer;
    private readonly Func<string?> chooseExport, chooseImport;
    private readonly Func<bool> canApply;
    private readonly Action applied;
    private readonly Action<Exception>? report;
    private readonly CancellationTokenSource lifetime = new();
    private SettingsConfigurationTransfer.Prepared? prepared;
    private string? expectedFingerprint;
    private string password = "", confirmation = "", importPassword = "";
    private string status = "选择导出或导入；不需要服务器，也不会联网。", preview = "尚未预览导入文件。";
    private bool busy;
    public ConfigurationTransferViewModel(SettingsConfigurationTransfer transfer, Func<string?> chooseExport, Func<string?> chooseImport,
        Func<bool> canApply, Action applied, Action<Exception>? report = null)
    {
        this.transfer = transfer; this.chooseExport = chooseExport; this.chooseImport = chooseImport;
        this.canApply = canApply; this.applied = applied; this.report = report;
        ExportCommand = new(ExportAsync, Report, () => CanEdit);
        PreviewCommand = new(PreviewAsync, Report, () => CanEdit);
        ApplyCommand = new(Apply, () => CanEdit && prepared is not null);
    }
    public event EventHandler? SecretsCleared;
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public string Password { get => password; set => Set(ref password, value); }
    public string Confirmation { get => confirmation; set => Set(ref confirmation, value); }
    public string ImportPassword { get => importPassword; set { if (Set(ref importPassword, value) && !Busy) ClearPreview(); } }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Preview { get => preview; private set => Set(ref preview, value); }
    public bool CanEdit => !Busy && !lifetime.IsCancellationRequested;
    public bool Busy { get => busy; private set { if (Set(ref busy, value)) Refresh(); } }
    private async Task ExportAsync()
    {
        if (Password.Length is < 8 or > 256) { Status = "请设置 8–256 位加密密码，建议使用至少 12 位；导入时需要同一密码。"; return; }
        if (Password != Confirmation) { Status = "两次输入的导出密码不一致。"; return; }
        string? path = chooseExport(); if (path is null) return;
        Busy = true; string secret = Password; Status = "正在加密导出…";
        try
        {
            await Task.Run(() => transfer.ExportFile(transfer.Capture(), secret, path, lifetime.Token));
            if (!lifetime.IsCancellationRequested) Status = "已导出加密配置。把文件带到另一台电脑，导入时输入同一密码即可。请妥善保管密码，遗忘后无法找回。";
        }
        finally { ClearSecrets(); Busy = false; }
    }
    private async Task PreviewAsync()
    {
        if (ImportPassword.Length is < 8 or > 256) { Status = "请输入导出时设置的加密密码（8–256 位）。"; return; }
        string? path = chooseImport(); if (path is null) return;
        Busy = true; ClearPreview(); string secret = ImportPassword; Status = "正在解密并检查配置…";
        try
        {
            var result = await Task.Run(() =>
            {
                var before = transfer.Capture();
                var imported = transfer.ReadArchive(path, secret);
                return (before.Fingerprint, imported);
            });
            if (lifetime.IsCancellationRequested) return;
            expectedFingerprint = result.Fingerprint; prepared = result.imported;
            Preview = $"翻译 / 在线 OCR：{prepared.TranslationProfiles} 组服务\nAI 发音：{prepared.SpeechProfiles} 组服务\n包含密钥、模型、音色、默认方式、原文展开状态和快捷键。";
            Status = "预览通过，尚未修改本机。导入会替换上述范围内的全部配置；请确认后点击‘导入并应用’。";
        }
        finally { ClearSecrets(); Busy = false; }
    }
    private void Apply()
    {
        if (prepared is null || expectedFingerprint is null || !CanEdit) return;
        if (!canApply()) { Status = "请先关闭文字工作台和其他设置窗口，以免覆盖尚未保存的修改或打断发音，然后再导入。"; return; }
        Busy = true;
        try
        {
            transfer.Apply(prepared, expectedFingerprint, () => !lifetime.IsCancellationRequested);
            ClearPreview(); applied();
            Status = "导入成功，密钥已按本机 Windows 用户重新加密保存。请从托盘退出并重新启动软件，使快捷键和所有默认设置生效。";
        }
        catch (Exception error) { ClearPreview(); Report(error); }
        finally { Busy = false; }
    }
    private void Report(Exception error)
    {
        if (lifetime.IsCancellationRequested) return;
        report?.Invoke(error);
        Status = error switch
        {
            CryptographicException => "密码不正确，或配置文件已损坏。本机配置未修改。",
            InvalidDataException => "不是受支持的配置文件，文件过大、损坏或版本不受支持。本机配置未修改。",
            InvalidOperationException when error.Message.Contains("新修改", StringComparison.Ordinal) => "本机配置有新修改，请重新预览导入文件。",
            _ => "操作未完成。请检查文件权限、关闭其他设置窗口后重试；若导入中断，请退出软件后重新启动，程序会先恢复原有配置。"
        };
    }
    private void ClearPreview() { prepared = null; expectedFingerprint = null; Preview = "尚未预览导入文件。"; ApplyCommand.Refresh(); }
    private void ClearSecrets() { Password = ""; Confirmation = ""; ImportPassword = ""; SecretsCleared?.Invoke(this, EventArgs.Empty); }
    private void Refresh() { Notify(nameof(CanEdit)); ExportCommand.Refresh(); PreviewCommand.Refresh(); ApplyCommand.Refresh(); }
    public void Dispose() { lifetime.Cancel(); ClearPreview(); ClearSecrets(); Refresh(); }
}
