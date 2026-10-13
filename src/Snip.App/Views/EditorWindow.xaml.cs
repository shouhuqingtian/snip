using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class EditorWindow : Window
{
    public EditorToolbar Toolbar => ToolbarControl;
    public EditorWindow()
    {
        Ui.EnsureTheme(); InitializeComponent();
        Loaded += (_, _) => Fit();
        Closing += (_, e) => { if (DataContext is EditorViewModel vm) e.Cancel = !vm.CanClose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && DataContext is EditorViewModel vm) { vm.CancelCurrent(); e.Handled = true; } };
    }
    private void Fit() { if (DataContext is EditorViewModel vm) vm.Fit(Viewport.ActualWidth, Viewport.ActualHeight); }
    private void FitClick(object sender, RoutedEventArgs e) => Fit();
}
