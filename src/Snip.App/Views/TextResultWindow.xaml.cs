using System.Windows;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class TextResultWindow : Window
{
    public TextResultWindow(TextResultViewModel vm)
    {
        InitializeComponent(); DataContext = vm;
        Loaded += async (_, _) => await vm.InitializeAsync();
        Closed += (_, _) => vm.Dispose();
    }
}
