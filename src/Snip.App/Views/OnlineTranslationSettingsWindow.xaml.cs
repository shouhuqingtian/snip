using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Snip.App.ViewModels;

namespace Snip.App.Views;

public partial class OnlineTranslationSettingsWindow : Window
{
    private readonly OnlineTranslationSettingsViewModel vm;
    public OnlineTranslationSettingsWindow(OnlineTranslationSettingsViewModel vm)
    {
        this.vm=vm;Ui.ApplyWindow(this);InitializeComponent();DataContext=vm;
        vm.PropertyChanged+=Changed;vm.Saved+=Saved;
        PreviewKeyDown+=(_,args)=>{if(args.Key==Key.Escape){args.Handled=true;Close();}};
        Closed+=(_,_)=>{vm.PropertyChanged-=Changed;vm.Saved-=Saved;vm.Dispose();ApiKeyBox.Clear();};
    }
    private void Changed(object? sender,PropertyChangedEventArgs args){if(args.PropertyName==nameof(vm.Provider))ApiKeyBox.Clear();}
    private void Saved(object? sender,EventArgs args)=>ApiKeyBox.Clear();
    private void KeyChanged(object sender,RoutedEventArgs args)=>vm.SetApiKey(ApiKeyBox.Password);
    private void CloseClick(object sender,RoutedEventArgs args)=>Close();
}
