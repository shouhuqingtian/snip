using System.Windows;
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
        Closed += (_, _) => vm.Dispose();
    }
}
