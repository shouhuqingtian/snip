using System.Windows;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class TextResultWindow : Window
{
    public TextResultWindow(TextResultViewModel vm)
    {
        InitializeComponent(); DataContext = vm;
        Loaded += (_, _) => vm.OcrCommand.Execute(null);
        Closed += (_, _) => vm.Dispose();
    }
}
