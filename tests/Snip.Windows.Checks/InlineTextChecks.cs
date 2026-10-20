using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.Services;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class InlineTextChecks
{
    public static int Run()
    {
        _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            Ui.EnsureTheme();
            var pixels = Enumerable.Repeat((byte)255, 1000 * 700 * 4).ToArray();
            var image = BitmapSource.Create(1000, 700, 96, 96, PixelFormats.Bgra32, null, pixels, 4000); image.Freeze();
            var renderer = new AnnotationRenderer();
            foreach (double scale in new[] { 0.5, 1.0 })
            {
                var vm = new EditorViewModel(image, renderer, new Export(), new Dialogs());
                Require(vm.StrokeWidth == 6 && vm.StrokeWidths.Contains(6), "New screenshots must default to stroke width 6.");
                var window = new EditorWindow { DataContext = vm };
                try
                {
                    window.Show(); window.UpdateLayout(); vm.Zoom = scale; window.UpdateLayout(); Pause();
                    var canvas = TextWindowChecks.Descendants<EditorCanvas>(window).Single();
                    var choices = TextWindowChecks.Descendants<ComboBox>(window.Toolbar).ToArray();
                    Require(choices.Length == 2 && !choices.Any(c => c.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == nameof(EditorViewModel.MosaicSize)), "Mosaic numeric dropdown remains in the toolbar.");
                    var stroke = choices.Single(c => c.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == nameof(EditorViewModel.StrokeWidth));
                    Require(Equals(stroke.SelectedItem, 6.0), "Stroke width control does not show default 6.");
                    var font = choices.Single(c => c.GetBindingExpression(ComboBox.SelectedItemProperty)?.ParentBinding.Path.Path == nameof(EditorViewModel.FontSize));
                    font.SelectedItem = 64.0;
                    TextWindowChecks.Descendants<RadioButton>(window.Toolbar).Single(b => Equals(b.ToolTip, "文字（Ctrl+Enter 提交）")).IsChecked = true;
                    vm.BeginPointer(new(40, 40), false); window.UpdateLayout(); Pause();
                    var input = TextWindowChecks.Descendants<TextBox>(canvas).Single();
                    Require(input.FontSize == 64 && input.ActualWidth >= 64 * 3 && input.ActualHeight >= input.FontFamily.LineSpacing * 64, "Empty large-font editor is too small to show its glyphs.");
                    double initialHeight = input.ActualHeight;
                    input.Text = "大字号 Text 64\n第二行 Line two"; window.UpdateLayout(); Pause();
                    Require(input.ActualHeight > initialHeight * 1.7, "Inline editor did not grow for multiline large text.");
                    foreach (int index in new[] { 0, 5, input.Text.Length - 1 })
                    {
                        var rect = input.GetRectFromCharacterIndex(index);
                        Require(!rect.IsEmpty && rect.Left >= 0 && rect.Top >= 0 && rect.Right <= input.ActualWidth && rect.Bottom <= input.ActualHeight, "Large-font character is outside the visible input: " + rect + " / " + input.RenderSize);
                    }
                    var rendered = new RenderTargetBitmap((int)Math.Ceiling(input.ActualWidth), (int)Math.Ceiling(input.ActualHeight), 96, 96, PixelFormats.Pbgra32); rendered.Render(input);
                    byte[] bytes = new byte[rendered.PixelWidth * rendered.PixelHeight * 4]; rendered.CopyPixels(bytes, rendered.PixelWidth * 4, 0);
                    int red = 0; for (int i = 0; i < bytes.Length; i += 4) if (bytes[i + 2] > 180 && bytes[i + 1] < 120 && bytes[i] < 120) red++;
                    Require(red > 1000, "Large-font input has text properties but no visible glyph pixels.");
                    if (scale == 1) SavePreview(window);
                    input.Focus(); HotkeyChecks.Press(0x11, 0x0D); TextWindowChecks.PumpUntil(() => vm.InlineText is null, TimeSpan.FromSeconds(3));
                    var text = vm.BaseAnnotations.Single();
                    Require(text.FontSize == 64 && text.StrokeWidth == 6 && text.Text.Replace("\r\n", "\n", StringComparison.Ordinal) == "大字号 Text 64\n第二行 Line two", "Committing large text changed font, default stroke or content.");
                    var output = vm.Compose(); Require(output.PixelWidth == 1000 && output.PixelHeight == 700 && renderer.GetBounds(text).Height > 100, "Text editor display changes altered export size or line layout.");
                    vm.UndoCommand.Execute(null); Require(vm.BaseAnnotations.Count == 0, "Large text undo failed.");
                    vm.RedoCommand.Execute(null); Require(vm.BaseAnnotations.Count == 1, "Large text redo failed.");
                    Console.WriteLine($"PASS large-font 64 inline text at {scale:P0}: empty editor, all glyph rectangles and {red} red pixels visible; grows for newlines; Ctrl+Enter/undo/redo and original export retained; stroke 6 and no mosaic dropdown.");
                }
                finally { window.CancelScreenshot(); }
            }
            Directory.CreateDirectory("artifacts/validation");
            File.WriteAllText("artifacts/validation/editor-options.json", JsonSerializer.Serialize(new { DefaultStroke6 = true, MosaicDropdownRemoved = true, LargeFont64Visible = true, MultilineGrows = true, ActualScales = true, CommitUndoRedo = true, OriginalExportSize = true }));
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Pause() { var task = Task.Delay(60); TextWindowChecks.PumpUntil(() => task.IsCompleted, TimeSpan.FromSeconds(2)); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void SavePreview(EditorWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("artifacts/validation/ui-previews");
        using var file = File.Create("artifacts/validation/ui-previews/41-large-inline-text.png"); encoder.Save(file);
    }
    private sealed class Export : IImageExportService { public void SavePng(BitmapSource image, string path) { } public Task CopyAsync(BitmapSource image) => Task.CompletedTask; }
    private sealed class Dialogs : IEditorDialogs
    {
        public string? ChoosePngPath() => null;
        public TextInput? EditText(string text, string color, double fontSize) => throw new InvalidOperationException("Inline expected.");
        public CloseChoice ConfirmClose() => CloseChoice.Discard;
        public void ShowError(string message) => throw new InvalidOperationException("No error expected.");
    }
}
