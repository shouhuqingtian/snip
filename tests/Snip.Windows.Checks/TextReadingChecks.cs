using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snip.App.ViewModels;
using Snip.App.Views;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.Windows.Checks;

internal static class TextReadingChecks
{
    internal static string LongBody => Translation.Body;
    public static void Run(string folder, BitmapSource image)
    {
        using var journal=new DiagnosticJournal(Path.Combine(folder,"reading-diagnostics"));
        var speech=new Speech();var clipboard=new Clipboard();var translation=new Translation();
        using var vm=new TextResultViewModel(image,new TextWindowChecks.FakeOcr(),new ModelStore(Path.Combine(folder,"reading-no-models")),translation,journal,clipboard,translation,
            initialPreferences:new(TranslationMode.Online),speech:speech);
        var window=new TextResultWindow(vm);window.Show();
        TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Source.Length>0,TimeSpan.FromSeconds(5));
        vm.Source="中文长文示例";
        Await(vm.TranslateAsync());window.UpdateLayout();
        var body=(TextBox)window.FindName("TranslatedText");
        Require(!vm.SourceExpanded && !((TextBox)window.FindName("RecognizedText")).IsVisible,"First translation did not give space to the result.");
        Require(body.IsVisible && body.Text==Translation.Body && body.ActualHeight>180,"Long translation is clipped into a small fixed box.");
        for(DependencyObject? ancestor=VisualTreeHelper.GetParent(body);ancestor is not null;ancestor=VisualTreeHelper.GetParent(ancestor))
            Require(ancestor is not ScrollViewer,"Whole-page scrolling still wraps the translation text box.");
        Click(window,"ToggleSourceButton");window.UpdateLayout();
        Require(vm.SourceExpanded && ((TextBox)window.FindName("RecognizedText")).IsVisible,"Original cannot be expanded for editing.");
        ((TabItem)window.FindName("LanguageNotesTab")).IsSelected=true;window.UpdateLayout();
        Require(((TextBox)window.FindName("TranslationLanguageNotes")).IsVisible && !body.IsVisible,"Pronunciation is not separated from the full translation.");
        Click(window,"OpenReadingButton");window.UpdateLayout();
        var reading=window.OwnedWindows.OfType<TranslationReadingWindow>().Single();reading.UpdateLayout();
        Require(ReferenceEquals(reading.DataContext,vm) && ((TextBox)reading.FindName("ReadingTranslation")).Text==Translation.Body,"Reading view re-requested or truncated the result.");
        Click(window,"OpenReadingButton");Require(window.OwnedWindows.Count==1,"Reading view can duplicate itself.");
        ((ComboBox)reading.FindName("ReadingFontSizeBox")).SelectedValue="24";reading.UpdateLayout();
        Require(((TextBox)reading.FindName("ReadingTranslation")).FontSize==24,"Reading font size did not apply.");
        vm.CopyTranslationCommand.Execute(null);TextWindowChecks.PumpUntil(()=>clipboard.Text.Length>0,TimeSpan.FromSeconds(3));
        Require(clipboard.Text==Translation.Body,"Copying includes pronunciation notes.");
        Require(speech.Calls==0 && vm.ShowSpeechButton,"Speech started without a click or English online result has no speaker.");
        Click(window,"SpeakTranslationButton");TextWindowChecks.PumpUntil(()=>speech.Calls==1,TimeSpan.FromSeconds(3));
        Require(vm.Speaking && speech.Text==Translation.Body,"Speaker did not use only the full English translation.");
        Click(reading,"ReadingSpeakButton");TextWindowChecks.PumpUntil(()=>!vm.Speaking,TimeSpan.FromSeconds(3));
        Require(speech.Token.IsCancellationRequested && vm.SpeechStatus.Contains("停止"),"Second click did not stop speech.");
        speech.Pending.TrySetResult();
        Await(vm.ToggleSpeechAsync());Require(vm.SpeechStatus.Contains("完成"),"Completed speech status was not shown.");
        speech.Fail=true;Await(vm.ToggleSpeechAsync());
        Require(vm.SpeechStatus.Contains("Windows 英语语音") && window.IsVisible && vm.Translated==Translation.Body,"Missing voice discarded the result or lacked guidance.");speech.Fail=false;
        speech.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        var playing=vm.ToggleSpeechAsync();TextWindowChecks.PumpUntil(()=>vm.Speaking,TimeSpan.FromSeconds(3));
        reading.Close();Await(playing);Require(window.IsVisible && speech.Token.IsCancellationRequested,"Closing reading view did not stop playback or closed workbench.");
        Await(vm.TranslateAsync());Require(vm.ResultTab==0,"New translation did not return to full-result tab.");
        speech.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);playing=vm.ToggleSpeechAsync();window.Close();Await(playing);
        Require(speech.Token.IsCancellationRequested && !vm.SpeakTranslationCommand.CanExecute(null),"Closing result retained an active speaker.");
        journal.FlushAsync().GetAwaiter().GetResult();
        string log=File.ReadAllText(Path.Combine(folder,"reading-diagnostics","events.jsonl"));
        Require(log.Contains("text-speech") && !log.Contains("PUBLIC-READING") && !log.Contains("中文长文示例") && !log.Contains("VOICE-PRIVATE"),"Speech text or native error leaked into diagnostics.");
        Console.WriteLine("PASS actual long-result layout, original expansion, separate notes tab, shared reading window/font/copy; English speaker click/stop/missing voice/reading close/result close and log privacy.");
    }
    private static void Click(Window window,string name)
    {
        var button=(Button)window.FindName(name);
        if(button.Command is { } command){Require(command.CanExecute(button.CommandParameter),"Disabled command: "+name);command.Execute(button.CommandParameter);}
        else button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void Await(Task task){TextWindowChecks.PumpUntil(()=>task.IsCompleted,TimeSpan.FromSeconds(6));task.GetAwaiter().GetResult();}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class Clipboard : ITextClipboardService
    {public string Text="";public Task CopyAsync(string text,CancellationToken token,IProgress<int>? retries=null){Text=text;return Task.CompletedTask;}}
    private sealed class Speech : ITextSpeechService
    {
        public int Calls;public string Text="";public CancellationToken Token;public bool Fail;
        public TaskCompletionSource Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SpeakEnglishAsync(string text,CancellationToken token)
        {Calls++;Text=text;Token=token;if(Fail)throw new SpeechUnavailableException();return Pending.Task.WaitAsync(token);}
    }
    private sealed class Translation : IAnnotatedTranslationService
    {
        public static readonly string Body=string.Join("\n\n",Enumerable.Range(1,24).Select(i=>$"PUBLIC-READING paragraph {i}. Capture an idea and keep the details that matter. This longer result should remain readable without scrolling the entire workbench."));
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token)=>Task.FromResult(Body);
        public Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text,string direction,IProgress<TranslationProgress>? progress,CancellationToken token)
            =>Task.FromResult(new AnnotatedTranslation(Body,"",[new("capture","/ˈkæptʃə/","动词(v.)","捕捉")]));
    }
}
