using System.Windows;

namespace Snip.App.Views;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
    }
}
