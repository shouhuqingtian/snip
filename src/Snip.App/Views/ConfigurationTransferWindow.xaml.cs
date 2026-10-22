using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class ConfigurationTransferWindow : Window
{
    public ConfigurationTransferWindow(ConfigurationTransferViewModel vm)
    {
        Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        vm.SecretsCleared += ClearSecrets;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { vm.Dispose(); vm.SecretsCleared -= ClearSecrets; ClearSecrets(this, EventArgs.Empty); };
    }
    private void ClearSecrets(object? sender, EventArgs args) { ExportPasswordBox.Clear(); ExportConfirmationBox.Clear(); ImportPasswordBox.Clear(); }
    private void ExportPasswordChanged(object sender, RoutedEventArgs args) { if (DataContext is ConfigurationTransferViewModel vm) vm.Password = ExportPasswordBox.Password; }
    private void ExportConfirmationChanged(object sender, RoutedEventArgs args) { if (DataContext is ConfigurationTransferViewModel vm) vm.Confirmation = ExportConfirmationBox.Password; }
    private void ImportPasswordChanged(object sender, RoutedEventArgs args) { if (DataContext is ConfigurationTransferViewModel vm) vm.ImportPassword = ImportPasswordBox.Password; }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
