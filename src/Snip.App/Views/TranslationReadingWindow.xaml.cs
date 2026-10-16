using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class TranslationReadingWindow : Window
{
    public TranslationReadingWindow(TextResultViewModel vm)
    {
        Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => vm.StopSpeechFor(original: false);
    }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
