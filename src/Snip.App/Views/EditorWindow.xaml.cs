using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class EditorWindow : Window
{
    private bool discardRequested;
    public EditorToolbar Toolbar => ToolbarControl;
    public EditorWindow()
    {
        Ui.ApplyWindow(this); InitializeComponent();
        Loaded += (_, _) => UpdateViewport();
        DataContextChanged += (_, _) => UpdateViewport();
        Viewport.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) UpdateViewport();
        };
        Closing += (_, e) => { if (DataContext is EditorViewModel vm) e.Cancel = vm.IsExporting || (!discardRequested && !vm.CanClose()); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && DataContext is EditorViewModel vm) { vm.CancelCurrent(); e.Handled = true; } };
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
