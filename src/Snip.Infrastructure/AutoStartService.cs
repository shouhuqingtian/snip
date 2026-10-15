using System;
using System.IO;
using Microsoft.Win32;
using Snip.Core;

namespace Snip.Infrastructure;

/// <summary>Only manages this application's current-user Run value; never requires administrator rights.</summary>
public sealed class AutoStartService : IAutoStartService
{
    public const string ValueName = "SelfSnip";
    private readonly string executable;
    private readonly string registryPath;
    public AutoStartService(string? executable = null, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
    {
        this.executable = Path.GetFullPath(executable ?? Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。"));
        this.registryPath = registryPath;
    }
    public bool IsEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(registryPath); return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value); }
    }
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!string.Equals(Path.GetFileName(executable), "Snip.exe", StringComparison.OrdinalIgnoreCase) || executable.Contains('"') || !File.Exists(executable))
                throw new InvalidOperationException("请从固定文件夹中的 Snip.exe 启用开机自启，不要移动或删除该文件夹。");
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, true);
            key.SetValue(ValueName, "\"" + executable + "\" --startup", RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, true);
            key?.DeleteValue(ValueName, false);
        }
    }
}
