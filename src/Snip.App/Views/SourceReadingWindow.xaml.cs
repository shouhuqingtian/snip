using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class SourceReadingWindow : Window
{
    public SourceReadingWindow(TextResultViewModel vm)
    {
        Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        Closed += (_, _) => vm.StopSpeechFor(original: true);
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
