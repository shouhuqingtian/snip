using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;
public partial class OnlineSpeechSettingsWindow : Window
{
    private readonly OnlineSpeechSettingsViewModel vm;
    public OnlineSpeechSettingsWindow(OnlineSpeechSettingsViewModel vm)
    {
        this.vm = vm; Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        vm.PropertyChanged += Changed; vm.Saved += Saved;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { vm.PropertyChanged -= Changed; vm.Saved -= Saved; vm.Dispose(); SpeechApiKeyBox.Clear(); };
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(vm.Provider)) SpeechApiKeyBox.Clear(); }
    private void Saved(object? sender, EventArgs args) => SpeechApiKeyBox.Clear();
    private void KeyChanged(object sender, RoutedEventArgs args) => vm.SetApiKey(SpeechApiKeyBox.Password);
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
