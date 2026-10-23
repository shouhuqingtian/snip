using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.Core;
using Snip.Infrastructure;

namespace Snip.App.Views;

public partial class EditorWindow : Window
{
    private bool discardRequested;
    private ImageTextSelectionViewModel? textSelection;
    private EditorViewModel? selectionEditor;
    private readonly DispatcherTimer selectionScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    public EditorToolbar Toolbar => ToolbarControl;
    public EditorWindow(IOcrService? textOcr = null, ITextClipboardService? textClipboard = null, DiagnosticJournal? diagnostics = null)
    {
        Ui.ApplyWindow(this); InitializeComponent();
        Loaded += (_, _) => UpdateViewport();
        selectionScroll.Tick += (_, _) => ScrollTextSelection();
        EditorSurface.TextDragChanged += (_, _) => { if (EditorSurface.IsSelectingText) selectionScroll.Start(); else selectionScroll.Stop(); };
        ToolbarControl.PreviewMouseLeftButtonDown += (_, e) =>
        {
            for (DependencyObject? item = e.OriginalSource as DependencyObject; item is not null && item != ToolbarControl; item = VisualTreeHelper.GetParent(item))
                if (item is RadioButton) { textSelection?.Deactivate(); break; }
        };
        DataContextChanged += (_, _) =>
        {
            ReleaseTextSelection();
            if (DataContext is EditorViewModel editor)
            {
                selectionEditor = editor;
                textSelection = new(editor, textOcr ?? new OfflineOcrService(), textClipboard ?? new TextClipboardService(), diagnostics);
                editor.TextSelection = textSelection; textSelection.VisualChanged += TextSelectionChanged;
            }
            UpdateViewport();
        };
        Closed += (_, _) => { selectionScroll.Stop(); ReleaseTextSelection(); };
        Viewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) UpdateViewport();
        };
        Closing += (_, e) => { if (DataContext is EditorViewModel vm) e.Cancel = vm.IsExporting || (!discardRequested && !vm.CanClose()); };
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                if (textSelection?.IsActive == true) { textSelection.CopyCommand.Execute(null); e.Handled = true; return; }
                if (DataContext is EditorViewModel { IsDrawing: false, IsExporting: false }) Toolbar.RequestComplete();
                e.Handled=true;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (textSelection?.IsActive == true)
            {
                if (e.Key == Key.Escape) { textSelection.Deactivate(); e.Handled = true; return; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { textSelection.CopyCommand.Execute(null); e.Handled = true; return; }
                if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A) { textSelection.SelectAllCommand.Execute(null); e.Handled = true; return; }
            }
            if (e.Key == Key.Escape && DataContext is EditorViewModel vm) { vm.CancelCurrent(); e.Handled = true; }
        };
    }
    private void ScrollTextSelection()
    {
        if (!EditorSurface.IsSelectingText || textSelection?.IsActive != true) { selectionScroll.Stop(); return; }
        var point = Mouse.GetPosition(Viewport);
        static double Delta(double position, double size) => position < 28 ? -Math.Clamp(28 - position, 6, 80) : position > size - 28 ? Math.Clamp(position - size + 28, 6, 80) : 0;
        double x = Delta(point.X, Viewport.ActualWidth), y = Delta(point.Y, Viewport.ActualHeight);
        if (x == 0 && y == 0) return;
        Viewport.ScrollToHorizontalOffset(Viewport.HorizontalOffset + x); Viewport.ScrollToVerticalOffset(Viewport.VerticalOffset + y);
        Viewport.UpdateLayout(); EditorSurface.UpdateTextSelection();
    }
    private void TextSelectionChanged(object? sender, EventArgs args)
    { if (textSelection?.IsActive != true) { EditorSurface.EndTextSelection(); EditorSurface.ContextMenu = null; } EditorSurface.InvalidateVisual(); }
    private void ReleaseTextSelection()
    {
        if (textSelection is null) return;
        textSelection.VisualChanged -= TextSelectionChanged; textSelection.Dispose();
        if (selectionEditor?.TextSelection == textSelection) selectionEditor.TextSelection = null;
        selectionEditor = null; textSelection = null;
    }
    public void CancelScreenshot()
    {
        if (DataContext is EditorViewModel { IsExporting: true }) return;
        discardRequested = true;
        Close();
    }
    private void UpdateViewport()
    { if (DataContext is EditorViewModel vm) vm.UpdateViewport(Viewport.ViewportWidth, Viewport.ViewportHeight); }
}
