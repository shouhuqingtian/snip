using System.Windows;
using System.Windows.Input;
using Snip.Infrastructure;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class TextResultWindow : Window
{
    public TextResultWindow(TextResultViewModel vm)
    {
        Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        Loaded += async (_, _) =>
        {
            vm.WindowOpened();
            try { await vm.InitializeAsync(); }
            catch (Exception error) { vm.WindowFailed(error); }
        };
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            if (TextOptionsPopup.IsOpen) { TextOptionsPopup.IsOpen = false; args.Handled = true; return; }
            args.Handled = true; Close();
        };
        Closed += (_, _) => vm.Dispose();
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
    private void RecoverClick(object sender, RoutedEventArgs args) => WindowWorkAreaService.Recover(this);
    private void OpenReadingClick(object sender, RoutedEventArgs args)
    {
        var existing = OwnedWindows.OfType<TranslationReadingWindow>().FirstOrDefault();
        if (existing is not null) { existing.Show(); existing.Activate(); return; }
        new TranslationReadingWindow((TextResultViewModel)DataContext) { Owner = this }.Show();
    }
    private void RuntimeDownloadNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs args)
    {
        args.Handled = true;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(args.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { ((TextResultViewModel)DataContext).WindowFailed(error); }
    }
}
