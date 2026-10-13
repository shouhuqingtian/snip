using System.Windows;
using Microsoft.Win32;
using Snip.App.Views;
using Snip.Core;

namespace Snip.App.Services;

public enum CloseChoice { Save, Discard, Cancel }
public sealed record TextInput(string Text, string Color, double FontSize);
public interface IEditorDialogs
{
    string? ChoosePngPath();
    TextInput? EditText(string text, string color, double fontSize);
    CloseChoice ConfirmClose();
    void ShowError(string message);
}

public sealed class EditorDialogs(Func<Window?> owner) : IEditorDialogs
{
    public string? ChoosePngPath()
    {
        var dialog = new SaveFileDialog { Title = "保存截图", Filter = "PNG 图片 (*.png)|*.png", DefaultExt = ".png", AddExtension = true,
            FileName = $"自用截图_{DateTime.Now:yyyyMMdd_HHmmss}.png", OverwritePrompt = true };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }
    public TextInput? EditText(string text, string color, double fontSize)
    {
        var window = new TextDialog(text, color, fontSize) { Owner = owner() };
        return window.ShowDialog() == true ? new(window.TextValue, window.ColorValue, window.FontSizeValue) : null;
    }
    public CloseChoice ConfirmClose() => MessageBox.Show(owner()!, "当前修改尚未导出，是否保存后关闭？", "自用截图", MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
    { MessageBoxResult.Yes => CloseChoice.Save, MessageBoxResult.No => CloseChoice.Discard, _ => CloseChoice.Cancel };
    public void ShowError(string message) => MessageBox.Show(owner()!, message, "自用截图", MessageBoxButton.OK, MessageBoxImage.Warning);
}
