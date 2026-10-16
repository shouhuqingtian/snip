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
        var expected=new InvalidOperationException("Expected polling assertion");
        try { TextWindowChecks.PumpUntil(()=>throw expected,TimeSpan.FromSeconds(1));throw new InvalidOperationException("Polling exception was swallowed."); }
        catch(InvalidOperationException error) when(ReferenceEquals(error,expected)) { }
        var window=new TextResultWindow(vm);window.Show();
        TextWindowChecks.PumpUntil(()=>!vm.Busy && vm.Source.Length>0,TimeSpan.FromSeconds(5));
        vm.Source="中文长文示例";
        Await(vm.TranslateAsync());window.UpdateLayout();
        var body=(TextBox)window.FindName("TranslatedText");
        Require(!vm.SourceExpanded && !((TextBox)window.FindName("RecognizedText")).IsVisible,"First translation did not give space to the result.");
        Require(body.IsVisible && body.Text==Translation.Body && body.ActualHeight>180,"Long translation is clipped into a small fixed box.");
        var original = string.Join("\n\n", Enumerable.Repeat("PUBLIC-SOURCE 原文阅读应保留完整内容，可放大、复制和修正。", 60));
        vm.Source = original;
        int requests = translation.Calls;
        Click(window,"OpenSourceReadingButton");
        var sourceReading = window.OwnedWindows.OfType<SourceReadingWindow>().Single();sourceReading.UpdateLayout();
        var sourceBody = (TextBox)sourceReading.FindName("ReadingSource");
        Require(ReferenceEquals(sourceReading.DataContext,vm) && sourceBody.Text==original && sourceBody.ActualHeight>180 && !vm.SourceExpanded,"Source reading truncated text or changed collapsed workbench layout.");
        Click(window,"OpenSourceReadingButton");Require(window.OwnedWindows.OfType<SourceReadingWindow>().Count()==1,"Source reader can duplicate itself.");
        ((ComboBox)sourceReading.FindName("SourceReadingFontSizeBox")).SelectedValue="28";sourceReading.UpdateLayout();
        Require(sourceBody.FontSize==28,"Original reading font size did not apply.");
        sourceBody.Text = original + "\n修正末行";
        Require(vm.Source==sourceBody.Text,"Source reader edits did not reach the workbench.");
        Click(sourceReading,"ReadingCopySourceButton");
        TextWindowChecks.PumpUntil(()=>vm.CopyStatus.Contains("已复制") && vm.CopySourceCommand.CanExecute(null),TimeSpan.FromSeconds(3));
        Require(clipboard.Text==vm.Source && translation.Calls==requests,"Source reading/copy made a translation request or lost text.");
        sourceReading.Width=sourceReading.MinWidth;sourceReading.Height=sourceReading.MinHeight;sourceReading.UpdateLayout();
        Require(sourceBody.ActualHeight>100,"Compact source reader has no usable text area.");
        var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(sourceReading),0,System.Windows.Input.Key.Escape) { RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        sourceReading.RaiseEvent(escape);
        Require(!sourceReading.IsVisible && window.IsVisible && vm.CopySourceCommand.CanExecute(null),"Escape closed/disposed the workbench instead of its reader.");
        vm.Source="中文长文示例";
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
        Click(window,"OpenSourceReadingButton");var ownedSource=window.OwnedWindows.OfType<SourceReadingWindow>().Single();
        speech.Pending=new(TaskCreationOptions.RunContinuationsAsynchronously);playing=vm.ToggleSpeechAsync();window.Close();Await(playing);
        Require(!ownedSource.IsVisible,"Closing the workbench left its source reader open.");
        Require(speech.Token.IsCancellationRequested && !vm.SpeakTranslationCommand.CanExecute(null),"Closing result retained an active speaker.");
        journal.FlushAsync().GetAwaiter().GetResult();
        string log=File.ReadAllText(Path.Combine(folder,"reading-diagnostics","events.jsonl"));
        Require(log.Contains("text-speech") && !log.Contains("PUBLIC-READING") && !log.Contains("PUBLIC-SOURCE") && !log.Contains("中文长文示例") && !log.Contains("VOICE-PRIVATE"),"Speech/source text or native error leaked into diagnostics.");
        BilingualReadingChecks.Run(folder, image);
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
        public Task SpeakAsync(string text,string language,CancellationToken token)
        {Calls++;Text=text;Token=token;if(Fail)throw new SpeechUnavailableException(language);return Pending.Task.WaitAsync(token);}
    }
    private sealed class Translation : IAnnotatedTranslationService
    {
        public int Calls;
        public static readonly string Body=string.Join("\n\n",Enumerable.Range(1,24).Select(i=>$"PUBLIC-READING paragraph {i}. Capture an idea and keep the details that matter. This longer result should remain readable without scrolling the entire workbench."));
        public Task<string> TranslateAsync(string text,string direction,CancellationToken token)=>Task.FromResult(Body);
        public Task<AnnotatedTranslation> TranslateWithDetailsAsync(string text,string direction,IProgress<TranslationProgress>? progress,CancellationToken token)
        { Calls++; return Task.FromResult(new AnnotatedTranslation(Body,"",[new("capture","/ˈkæptʃə/","动词(v.)","捕捉")])); }
    }
}
