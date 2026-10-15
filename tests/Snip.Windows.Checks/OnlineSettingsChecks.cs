using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Snip.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Translation;
using Snip.Infrastructure;

namespace Snip.Windows.Checks;

internal static class OnlineSettingsChecks
{
    public static int Run()
    {
        string folder=Path.Combine(Path.GetTempPath(),"Snip.Online.Settings-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,"settings.json"), secret="PUBLIC-TEST-KEY-"+Guid.NewGuid().ToString("N");
        _ = Application.Current ?? new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            var store=new OnlineTranslationSettingsStore(path);
            Require(store.Read() is null,"Fresh settings unexpectedly configured.");
            var settings=new OnlineTranslationSettings("public-provider","https://api.example.com/v1","public-model",true);
            store.Save(settings,secret);
            string raw=File.ReadAllText(path);
            Require(!raw.Contains(secret),"Key was stored in plain text.");
            var encrypted=Convert.FromBase64String(JsonNode.Parse(raw)!["Profiles"]![0]!["ProtectedKey"]!.GetValue<string>());
            var plain=ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser);
            try { Require(Encoding.UTF8.GetString(plain)==secret,"Native Windows DPAPI did not recover the original key."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
            Require(new OnlineTranslationSettingsStore(path).ReadCredentials().ApiKey==secret,"Restart did not retain encrypted settings.");
            Require(store.Read() is { HasApiKey:true,DefaultOnline:true },"Metadata did not describe configured state.");
            Require(!store.ReadCredentials().ToString().Contains(secret),"Credential ToString leaks a key.");
            Console.WriteLine("PASS actual current-user DPAPI storage encrypts key, restores after restart and redacts credentials.");

            store.Save(settings with { Model="other-public-model" },null);
            Require(store.ReadCredentials().ApiKey==secret,"Changing model lost saved key.");
            bool rejected=false;
            try { store.Save(settings with { Endpoint="https://other.example.com/v1" },null); }catch(InvalidOperationException){rejected=true;}
            Require(rejected && store.ReadCredentials().ApiKey==secret,"Changing server silently sent or reused a previous credential.");
            store.Save(settings with { Endpoint="https://other.example.com/v1",DefaultOnline=false },null);
            Require(store.Read() is { HasApiKey:false },"Changed server retained old credential.");
            Console.WriteLine("PASS changing model keeps key; changing provider endpoint requires a new key and preserves previous settings on failure.");

            store.Save(settings,secret);store.Save(settings with { DefaultOnline=false },null,removeKey:true);
            Require(store.Read() is { HasApiKey:false },"Explicit key removal failed.");
            store.Delete();Require(store.Read() is null,"Settings delete failed.");
            Console.WriteLine("PASS explicit key removal and configuration deletion work.");
            store.Save(new("DeepSeek","https://api.deepseek.com/v1","deepseek-chat",true),secret);
            var legacyProfile = JsonNode.Parse(File.ReadAllText(path))!;
            legacyProfile["Profiles"]![0]!.AsObject().Remove("VisionModel");
            File.WriteAllText(path, legacyProfile.ToJsonString());
            Require(store.Read()!.VisionModel == "" && store.ReadCredentials().ApiKey == secret, "Legacy profile without vision field lost credentials.");
            string oldBlob=legacyProfile["Profiles"]![0]!["ProtectedKey"]!.GetValue<string>();
            store.Save(new("自定义：迁移检查","https://custom.example.com/v1","custom-model",true),"PUBLIC-MIGRATION-KEY");
            var migrated=JsonNode.Parse(File.ReadAllText(path))!;
            Require(migrated["Version"]!.GetValue<int>()==1 && migrated["Profiles"]![0]!["ProtectedKey"]!.GetValue<string>()==oldBlob && store.ReadCredentials("DeepSeek").ApiKey==secret,"Version-one credentials were rewritten or lost.");
            Require(store.ReadProfiles().Count==2,"Profile metadata enumeration failed.");
            store.Delete();
            Console.WriteLine("PASS existing version-one profile encryption remains unchanged when adding custom profiles.");
            CheckWindows(path);
            OcrModeChecks.Run(folder);
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{foreach(var window in Application.Current!.Windows.Cast<Window>().ToArray())window.Close();SynchronizationContext.SetSynchronizationContext(context);Directory.Delete(folder,true);}
    }
    private static void CheckWindows(string path)
    {
        var store=new OnlineTranslationSettingsStore(path);
        var settings=new OnlineTranslationSettingsViewModel(store);
        var window=new OnlineTranslationSettingsWindow(settings);window.Show();window.UpdateLayout();
        Require(settings.Provider==OnlineTranslationProviders.DeepSeek && settings.Model=="deepseek-chat","DeepSeek defaults missing.");
        var modelControl=(ComboBox)window.FindName("ModelBox");
        modelControl.ApplyTemplate();
        var modelEditor=modelControl.Template.FindName("PART_EditableTextBox",modelControl) as TextBox;
        Require(modelEditor is { IsVisible:true,Text:"deepseek-chat" },"Model name is not visible in an editable input.");
        modelControl.SelectedItem="deepseek-reasoner";
        Require(modelEditor!.Text=="deepseek-reasoner" && settings.Model=="deepseek-reasoner","Model suggestion did not update visible input and settings.");
        modelEditor.Text="deepseek-chat";
        Require(settings.Model=="deepseek-chat","Typing into visible model input did not update settings.");
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-KEY-DEEPSEEK";settings.SaveCommand.Execute(null);
        Require(store.ReadCredentials(OnlineTranslationProviders.DeepSeek).ApiKey=="PUBLIC-KEY-DEEPSEEK","Actual password control did not save key.");
        Require(((PasswordBox)window.FindName("ApiKeyBox")).Password.Length==0,"Save did not clear visible password buffer.");
        settings.Provider=OnlineTranslationProviders.Qwen;
        Require(settings.Model=="qwen-plus" && !settings.HasSavedKey,"Switching provider reused previous key.");
        var visionControl = (ComboBox)window.FindName("VisionModelBox");
        visionControl.Text = "qwen-vl-plus";
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-KEY-QWEN";settings.DefaultOnline=true;settings.SaveCommand.Execute(null);
        Require(store.Read(OnlineTranslationProviders.Qwen) is { Model: "qwen-plus", VisionModel: "qwen-vl-plus" } && store.ReadCredentials(OnlineTranslationProviders.Qwen).Settings.VisionModel == "qwen-vl-plus", "Shared profile did not retain separate vision model.");
        settings.Provider=OnlineTranslationProviders.DeepSeek;
        Require(settings.HasSavedKey && ((PasswordBox)window.FindName("ApiKeyBox")).Password.Length==0,"Saved provider key should remain encrypted and not be repopulated.");
        Require(store.ReadCredentials(OnlineTranslationProviders.Qwen).ApiKey=="PUBLIC-KEY-QWEN" && store.ReadCredentials(OnlineTranslationProviders.DeepSeek).ApiKey=="PUBLIC-KEY-DEEPSEEK","Provider profiles did not persist independently.");
        Require(settings.Providers.Count>=6,"Provider presets are missing.");
        settings.CustomProfileName="备用账号";settings.AddProfileCommand.Execute(null);
        string custom=settings.Provider;
        Require(custom=="自定义：备用账号" && settings.Endpoint=="" && settings.Model=="" && !settings.HasSavedKey,"Custom profile reused provider configuration.");
        settings.Endpoint="https://custom.example.com/v1";
        modelEditor.Text="private-compatible-model";
        window.UpdateLayout();
        Require(settings.Model=="private-compatible-model","Editable model control did not update binding.");
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-CUSTOM-KEY";settings.SaveCommand.Execute(null);
        Require(store.Read() is { Provider:"自定义：备用账号", Model:"private-compatible-model" },"Custom profile was not activated.");
        Require(store.ReadCredentials(custom).ApiKey=="PUBLIC-CUSTOM-KEY","Custom key not stored independently.");
        settings.Provider=OnlineTranslationProviders.Qwen;
        Require(settings.Model=="qwen-plus" && modelEditor.Text=="qwen-plus" && settings.HasSavedKey,"Previous provider not restored after custom profile.");
        settings.SaveCommand.Execute(null);
        Require(store.Read()!.Provider==OnlineTranslationProviders.Qwen,"Saving selected backup did not switch active provider.");
        settings.CustomProfileName="备用账号";int count=settings.Providers.Count;settings.AddProfileCommand.Execute(null);
        Require(settings.Providers.Count==count && settings.Status.Contains("已存在"),"Duplicate custom name overwrote credentials.");
        settings.Provider=custom;
        settings.CustomProfileName="另一个网关";settings.AddProfileCommand.Execute(null);
        settings.Endpoint="https://second.example.com/v1";settings.Model="another-model";
        Require(!settings.HasSavedKey && ((PasswordBox)window.FindName("ApiKeyBox")).Password.Length==0,"New profile exposed another key.");
        ((PasswordBox)window.FindName("ApiKeyBox")).Password="PUBLIC-SECOND-KEY";settings.SaveCommand.Execute(null);
        string second=settings.Provider;
        var restarted=new OnlineTranslationSettingsViewModel(new OnlineTranslationSettingsStore(path));
        Require(restarted.Provider==second && restarted.Providers.Contains(custom) && restarted.Model=="another-model","Restart lost custom profiles or active selection.");
        restarted.Dispose();
        settings.DeleteProfileCommand.Execute(null);
        Require(store.Read(second) is null && store.ReadCredentials(custom).ApiKey=="PUBLIC-CUSTOM-KEY" && store.ReadCredentials(OnlineTranslationProviders.DeepSeek).ApiKey=="PUBLIC-KEY-DEEPSEEK","Deleting a profile changed other credentials.");
        window.Width=540;window.Height=420;window.UpdateLayout();
        Require(window.IsVisible && ((ComboBox)window.FindName("ModelBox")).IsEditable,"Minimum-size settings or editable model is unavailable.");
        Console.WriteLine("PASS actual custom profile creation, editable model, independent keys, switching, restart, duplicate rejection and deletion.");
        window.Close();Console.WriteLine("PASS actual settings XAML saves independent provider keys, clears password display and restores profiles.");

        var pending=new PendingOnline();
        using(var testing=new OnlineTranslationSettingsViewModel(store,_=>pending))
        {
            var busyWindow=new OnlineTranslationSettingsWindow(testing);busyWindow.Show();
            string selected=testing.Provider;testing.TestCommand.Execute(null);
            Require(!testing.CanEdit && !testing.AddProfileCommand.CanExecute(null) && !testing.SaveCommand.CanExecute(null),"Connection test allowed configuration mutations.");
            testing.Provider=selected==OnlineTranslationProviders.Qwen ? OnlineTranslationProviders.DeepSeek : OnlineTranslationProviders.Qwen;
            Require(testing.Provider==selected,"Provider changed during an in-flight connection test.");
            busyWindow.Close();
            TextWindowChecks.PumpUntil(()=>pending.Cancelled,TimeSpan.FromSeconds(3));
        }
        Console.WriteLine("PASS in-flight connection test locks profile selection and closing cancels the request.");

        var image=BitmapSource.Create(100,80,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,100*80*4).ToArray(),400);image.Freeze();
        var online=new FakeOnline();var offline=new OfflineTrap();
        using var vm=new TextResultViewModel(image,new TextWindowChecks.FakeOcr(),new ModelStore("unused-models"),offline,onlineTranslator:online,defaultOnline:true){ AutoTranslate=true };
        var result=new TextResultWindow(vm);result.Show();
        TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Translated.Length>0,TimeSpan.FromSeconds(8));
        Require(vm.Source=="Hello world" && vm.Translated=="在线译文" && online.Calls==1 && offline.Calls==0,"Online selection still required offline model.");
        Require(((TextBox)result.FindName("TranslatedText")).Text=="在线译文" && vm.TranslationNotice.Contains("发送"),"Online result or transmission notice is missing.");
        online.Fail=true;vm.TranslateCommand.Execute(null);TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Status.Contains("失败"),TimeSpan.FromSeconds(4));
        Require(vm.Source=="Hello world" && vm.Translated=="在线译文" && result.IsVisible,"Online failure discarded prior text or window.");
        result.Close();Console.WriteLine("PASS online OCR-to-result bypasses model download, displays notice and retains original/result on service failure.");
        AutomaticTranslationChecks.Run(Path.GetDirectoryName(path)!,image);
    }
    private sealed class PendingOnline : ITranslationService
    {
        public bool Cancelled;
        public async Task<string> TranslateAsync(string text,string direction,CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite,token);return ""; }
            catch(OperationCanceledException){Cancelled=true;throw;}
        }
    }
    private sealed class OfflineTrap : ITranslationService
    {
        public int Calls;
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token){Calls++;throw new InvalidOperationException("Offline route unexpectedly used.");}
    }
    private sealed class FakeOnline : ITranslationService
    {
        public int Calls;public bool Fail;
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token){Calls++;token.ThrowIfCancellationRequested();if(Fail)throw new InvalidOperationException("PUBLIC service limit fixture");return Task.FromResult("在线译文");}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
