using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class AccountSyncWindow : Window
{
    public AccountSyncWindow(AccountSyncViewModel vm)
    {
        Ui.ApplyWindow(this); InitializeComponent(); DataContext = vm;
        vm.SecretsCleared += (_, _) => { SyncPasswordBox.Clear(); SyncConfirmationBox.Clear(); };
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { args.Handled = true; Close(); } };
        Closed += (_, _) => { SyncPasswordBox.Clear(); SyncConfirmationBox.Clear(); vm.Dispose(); };
    }
    private void PasswordChanged(object sender, RoutedEventArgs args) { if (DataContext is AccountSyncViewModel vm) vm.Password = SyncPasswordBox.Password; }
    private void ConfirmationChanged(object sender, RoutedEventArgs args) { if (DataContext is AccountSyncViewModel vm) vm.Confirmation = SyncConfirmationBox.Password; }
    private void ServerLostFocus(object sender, RoutedEventArgs args) { if (DataContext is AccountSyncViewModel vm) vm.NormalizeServerIfValid(); }
    private void CloseClick(object sender, RoutedEventArgs args) => Close();
}
