using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class AutomaticTranslationChecks
{
    public static void Run(string folder, BitmapSource image)
    {
        using var diagnostics=new DiagnosticJournal(Path.Combine(folder,"automatic-diagnostics"));
        using var handler=new Protocol();using var client=new HttpClient(handler);
        var online=new OnlineTranslator(()=>new(OnlineTranslationProviders.Defaults(OnlineTranslationProviders.DeepSeek),"PUBLIC-KEY"),client);
        var offline=new Offline();var clipboard=new Clipboard();
        var preferences=new TranslationPreferencesStore(Path.Combine(folder,"automatic-preferences.json"));
        using var vm=new TextResultViewModel(image,new TextWindowChecks.FakeOcr(),new ModelStore(Path.Combine(folder,"automatic-no-models")),offline,
            diagnostics,clipboard,online,preferences:preferences,initialPreferences:new(TranslationMode.Online)) {AutoTranslate=true};
        var window=new TextResultWindow(vm);window.Show();
        TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Translated.Length>0,TimeSpan.FromSeconds(8));
        Require(vm.Direction=="auto" && handler.Directions.SequenceEqual(new[]{"en-zh"}) && vm.Translated=="应用已就绪。" && vm.LanguageNotes.Contains("yìng yòng") && vm.LanguageNotes.Contains("名词(n.)"),"OCR did not automatically translate English with annotations.");
        window.UpdateLayout();
        var notes=(TextBox)window.FindName("TranslationLanguageNotes");notes.BringIntoView();window.UpdateLayout();
        Require(notes.IsVisible && notes.IsReadOnly && notes.Text.Contains("/ˌæplɪˈkeɪʃən/"),"Language annotations are missing from the actual result XAML.");
        vm.CopyTranslationCommand.Execute(null);TextWindowChecks.PumpUntil(()=>vm.CopyStatus.Contains("已复制"),TimeSpan.FromSeconds(3));
        Require(clipboard.Text=="应用已就绪。","Copy translation included pinyin or word notes.");
        void Translate()
        {
            var task=vm.TranslateAsync();TextWindowChecks.PumpUntil(()=>task.IsCompleted,TimeSpan.FromSeconds(5));task.GetAwaiter().GetResult();
        }
        ((TextBox)window.FindName("RecognizedText")).Text="应用已就绪。";Translate();
        Require(handler.Directions.Last()=="zh-en" && vm.Translated=="The application is ready." && !vm.LanguageNotes.Contains("yìng yòng") && vm.LanguageNotes.Contains("名词(n.)") && vm.ResultDirection.Contains("中文 → 英文"),"Editing to Chinese did not reverse direction or retained stale Chinese pinyin.");
        ((TextBox)window.FindName("RecognizedText")).Text="The application is ready.";Translate();
        Require(handler.Directions.Last()=="en-zh" && vm.LanguageNotes.Contains("yìng yòng"),"Editing back to English did not redetect direction.");
        ((ComboBox)window.FindName("TranslationDirectionBox")).SelectedValue="zh-en";Translate();
        Require(handler.Directions.Last()=="zh-en","Explicit manual direction override is ignored.");
        ((ComboBox)window.FindName("TranslationDirectionBox")).SelectedValue="auto";
        ((Button)window.FindName("SaveTranslationSettingsButton")).Command.Execute(null);
        Require(new TranslationPreferencesStore(Path.Combine(folder,"automatic-preferences.json")).Read()==new TranslationPreferences(TranslationMode.Online,"auto"),"Workbench default automatic direction was not persisted.");
        string priorTranslation=vm.Translated,priorNotes=vm.LanguageNotes;
        handler.Failure=true;Translate();handler.Failure=false;
        Require(vm.Status.Contains("失败") && vm.Translated==priorTranslation && vm.LanguageNotes==priorNotes && window.IsVisible,"Provider failure discarded prior translation or notes.");
        handler.Malformed=true;Translate();handler.Malformed=false;
        Require(vm.Status.Contains("格式") && vm.Translated==priorTranslation && vm.LanguageNotes==priorNotes,"Bad structured response replaced a usable result.");
        handler.Pending=true;var pending=vm.TranslateAsync();TextWindowChecks.PumpUntil(()=>handler.PendingEntered,TimeSpan.FromSeconds(3));
        Require(!((ComboBox)window.FindName("TranslationDirectionBox")).IsEnabled,"Direction can change while a request is pending.");
        vm.CancelCommand.Execute(null);TextWindowChecks.PumpUntil(()=>pending.IsCompleted && handler.Cancelled,TimeSpan.FromSeconds(3));pending.GetAwaiter().GetResult();handler.Pending=false;
        Require(vm.Translated==priorTranslation && vm.LanguageNotes==priorNotes && !vm.Busy,"Cancelled request lost results or left controls locked.");
        handler.Plain=true;Translate();handler.Plain=false;
        Require(vm.Translated=="纯译文" && vm.LanguageNotes.Length==0 && vm.Status.Contains("注释"),"Plain-text compatible response did not clear old notes or report missing annotations.");
        int calls=handler.Directions.Count;
        vm.Mode=TranslationMode.Offline;vm.Source="中文界面";Translate();
        Require(offline.Direction=="zh-en" && vm.Translated=="Offline fixture" && !vm.HasLanguageNotes && handler.Directions.Count==calls,"Offline automatic direction used online annotations or a server.");
        vm.Source="English interface";Translate();Require(offline.Direction=="en-zh","Offline direction did not update after source edit.");
        vm.Mode=TranslationMode.Basic;vm.Source="application";Translate();Require(vm.Translated.Contains("应用"),"English dictionary lookup did not resolve automatic direction.");
        vm.Source="应用";Translate();Require(vm.Translated.Contains("application") && handler.Directions.Count==calls,"Chinese dictionary lookup used wrong direction or contacted an online service.");
        diagnostics.FlushAsync().GetAwaiter().GetResult();
        string log=File.ReadAllText(Path.Combine(folder,"automatic-diagnostics","events.jsonl"));
        Require(!log.Contains("Hello world") && !log.Contains("应用已就绪") && !log.Contains("PUBLIC-KEY") && !log.Contains("PRIVATE-RESPONSE") && !log.Contains("yìng yòng"),"Translation or notes leaked into runtime diagnostics.");
        window.Close();
        Console.WriteLine("PASS actual OCR → auto English/Chinese translation → editable source reversal; online IPA/POS/pinyin stay separate from clipboard; failure, malformed response and cancellation preserve results; offline/dictionary remain local.");
    }
    private sealed class Protocol : HttpMessageHandler
    {
        public readonly List<string> Directions=[];
        public bool Failure,Malformed,Plain,Pending,PendingEntered,Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string direction=body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("into Simplified Chinese") ? "en-zh" : "zh-en";
            Directions.Add(direction);
            if(Pending)
            {
                PendingEntered=true;
                try{await Task.Delay(Timeout.Infinite,token);}catch(OperationCanceledException){Cancelled=true;throw;}
            }
            if(Failure)return new(HttpStatusCode.ServiceUnavailable){Content=new StringContent("PRIVATE-RESPONSE")};
            string content=Malformed ? "{PRIVATE-RESPONSE" : Plain ? "纯译文" : JsonSerializer.Serialize(new
            {
                translation=direction=="en-zh" ? "应用已就绪。" : "The application is ready.",pinyin=direction=="en-zh" ? "yìng yòng yǐ jiù xù。" : "",
                words=new[]{new {english="application",ipa="/ˌæplɪˈkeɪʃən/",partOfSpeech="名词(n.)",meaning="应用程序"}}
            });
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new {choices=new[]{new {message=new {content}}}}),Encoding.UTF8,"application/json")};
        }
    }
    private sealed class Clipboard : ITextClipboardService
    {public string Text="";public Task CopyAsync(string text,CancellationToken token,IProgress<int>? retries=null){token.ThrowIfCancellationRequested();Text=text;return Task.CompletedTask;}}
    private sealed class Offline : ITranslationService
    {public string Direction="";public Task<string> TranslateAsync(string text,string direction,CancellationToken token){token.ThrowIfCancellationRequested();Direction=direction;return Task.FromResult("Offline fixture");}}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
}
