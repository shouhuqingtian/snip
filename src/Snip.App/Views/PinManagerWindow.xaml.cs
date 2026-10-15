using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;
namespace Snip.App.Views;
public partial class PinManagerWindow : Window
{
    public PinManagerWindow(PinManagerViewModel vm)
    {
        Ui.ApplyWindow(this);InitializeComponent();DataContext=vm;
        PreviewKeyDown+=(_,args)=>{if(args.Key==Key.Escape){args.Handled=true;Close();}};
    }
}
