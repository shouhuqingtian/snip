using System.Windows.Threading;
using System.Diagnostics;
using Snip.Core;
using Snip.Infrastructure;
using Snip.Translation;

namespace Snip.App.Services;

public enum VoiceInputState { Idle, Starting, Recording, Recognizing, Inserting }
public sealed class VoiceInputController : IDisposable
{
    private readonly ITranscriptionSettingsStore store;
    private readonly IMicrophoneRecorder recorder;
    private readonly IVoiceTargetProbe probe;
    private readonly Dispatcher dispatcher;
    private readonly DiagnosticJournal? diagnostics;
    private readonly Func<TranscriptionCredentials, IAudioTranscriber> transcriber;
    private readonly Func<IHotkeyService> hotkeyFactory;
    private readonly Func<HotkeyGesture, Action, Action, IVoiceInputTarget> targetFactory;
    private readonly Func<string, Func<bool>, CancellationToken, Task<int>> type;
    private IHotkeyService? hotkey;
    private VoiceInputPreferences preferences = VoiceInputPreferences.Default;
    private CancellationTokenSource? active, stop;
    private IVoiceInputTarget? currentTarget;
    private bool disposed;
    private long generation;
    public VoiceInputController(ITranscriptionSettingsStore store, Dispatcher dispatcher, DiagnosticJournal? diagnostics = null,
        IMicrophoneRecorder? recorder = null, IVoiceTargetProbe? probe = null, Func<TranscriptionCredentials, IAudioTranscriber>? transcriber = null,
        Func<IHotkeyService>? hotkeyFactory = null, Func<HotkeyGesture, Action, Action, IVoiceInputTarget>? targetFactory = null,
        Func<string, Func<bool>, CancellationToken, Task<int>>? type = null)
    {
        this.store = store; this.dispatcher = dispatcher; this.diagnostics = diagnostics;
        this.recorder = recorder ?? new IsolatedMicrophoneRecorder(FeatureHub.WorkerHost);
        this.probe = probe ?? new VoiceTargetProbe(FeatureHub.WorkerHost);
        this.transcriber = transcriber ?? (credentials => new OnlineTranscriptionService(credentials));
        this.hotkeyFactory = hotkeyFactory ?? (() => new HotkeyService());
        this.targetFactory = targetFactory ?? ((gesture, released, cancelled) => new VoiceInputTarget(gesture, released, cancelled));
        this.type = type ?? VoiceTextInput.TypeAsync;
    }
    public event EventHandler? Changed;
    public event Action<HotkeyGesture>? ShortcutWhileConfiguring;
    public Func<bool> CanStart { get; set; } = () => true;
    public VoiceInputState State { get; private set; }
    public bool IsActive => State != VoiceInputState.Idle;
    public bool Enabled => hotkey is not null;
    public string Shortcut => preferences.Hotkey.DisplayText;
    public VoiceInputMode Mode => preferences.Mode;
    public string RecordingEndHint => Mode == VoiceInputMode.Toggle ? "再按快捷键结束" : "松开结束";
    public string UsageHint => Mode == VoiceInputMode.Toggle ? "按一次 " + Shortcut + " 开始录音，再按一次结束并识别。" : "按住 " + Shortcut + " 说话，松开后识别并输入。";
    public string Status { get; private set; } = "按所选方式录音，结束后识别并输入。";
    public string LastFailure { get; private set; } = "";
    public string LastText { get; private set; } = "";
    public bool ReviewNeeded { get; private set; }
    public bool SuppressNotification { get; private set; }
    public DateTimeOffset? RecordingStartedUtc { get; private set; }
    public void Initialize()
    {
        dispatcher.VerifyAccess(); preferences = store.ReadPreferences(); preferences.Validate();
        if (!preferences.Enabled) return;
        _ = store.ReadCredentials(); var candidate = hotkeyFactory();
        if (!candidate.Register(preferences.Hotkey)) { candidate.Dispose(); throw new VoiceInputException("语音输入快捷键已被占用，请在语音输入设置中修改。"); }
        hotkey = candidate; hotkey.Triggered += Triggered; Publish("语音输入已启用，" + UsageHint, VoiceInputState.Idle);
    }
    public void Save(TranscriptionSettings settings, string? key, VoiceInputPreferences next, bool removeKey = false)
    {
        dispatcher.VerifyAccess(); if (IsActive) throw new VoiceInputException("请先结束录音，并等待当前识别结束。");
        TranscriptionProviders.Validate(settings); next.Validate();
        IHotkeyService? candidate = null;
        try
        {
            if (next.Enabled && (hotkey is null || hotkey.Current != next.Hotkey))
            {
                candidate = hotkeyFactory();
                if (!candidate.Register(next.Hotkey)) throw new VoiceInputException("快捷键已被其他程序或截图/贴图功能占用，请换一个组合；原设置保留。");
            }
            store.Save(settings, key, next, removeKey);
            if (!next.Enabled || candidate is not null) { if (hotkey is not null) { hotkey.Triggered -= Triggered; hotkey.Dispose(); } hotkey = candidate; candidate = null; if (hotkey is not null) hotkey.Triggered += Triggered; }
            preferences = next; LastFailure = ""; Publish(next.Enabled ? "已保存，" + UsageHint : "已保存，语音输入已停用。", VoiceInputState.Idle);
        }
        finally { candidate?.Dispose(); }
    }
    private async void Triggered(object? sender, EventArgs args)
    {
        if (!CanStart()) { ShortcutWhileConfiguring?.Invoke(preferences.Hotkey); return; }
        if (Mode == VoiceInputMode.Toggle && State is VoiceInputState.Starting or VoiceInputState.Recording) { stop?.Cancel(); return; }
        await BeginAsync();
    }
    public async Task BeginAsync()
    {
        dispatcher.VerifyAccess(); if (disposed || IsActive || !Enabled || !CanStart()) return;
        long current = ++generation; SuppressNotification = false; ReviewNeeded = false; RecordingStartedUtc = null; LastFailure = "";
        using var request = new CancellationTokenSource(); using var released = new CancellationTokenSource(); active = request; stop = released;
        IVoiceInputTarget? target = null;
        using var targetSession = (probe as IReusableVoiceTargetProbe)?.OpenSession();
        var activeProbe = (IVoiceTargetProbe?)targetSession ?? probe;
        var elapsed = Stopwatch.StartNew();
        long recordedAt = 0, submittedAt = 0, recognizedAt = 0;
        int audioBytes = 0;
        string completion = "语音输入已停止。";
        bool holdMode = Mode == VoiceInputMode.Hold;
        Publish(holdMode ? "准备麦克风，请继续按住 " + Shortcut + "…" : "准备麦克风，再按一次快捷键结束录音…", VoiceInputState.Starting);
        try
        {
            var credentials = store.ReadCredentials();
            target = targetFactory(preferences.Hotkey,
                () => dispatcher.BeginInvoke(() => { if (holdMode && !disposed && current == generation && ReferenceEquals(stop, released)) released.Cancel(); }),
                () => dispatcher.BeginInvoke(() => { if (!disposed && current == generation) Cancel(); }));
            currentTarget = target;
            if (holdMode && !target.HotkeyHeld) throw new VoiceInputException("请按住语音快捷键说话，松开后才会识别。");
            var initialTask = activeProbe.ReadAsync(target.Foreground, request.Token);
            using var holdWatch = CancellationTokenSource.CreateLinkedTokenSource(request.Token);
            var releaseWatch = Task.Run(async () =>
            {
                try
                {
                    while (holdMode && !holdWatch.IsCancellationRequested)
                    {
                        if (!target.HotkeyHeld) { released.Cancel(); return; }
                        await Task.Delay(20, holdWatch.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
            RecordedVoice recorded;
            try
            {
                recorded = await recorder.RecordAsync(released.Token, request.Token, () => dispatcher.BeginInvoke(() =>
                {
                    if (!disposed && generation == current && State == VoiceInputState.Starting)
                    { RecordingStartedUtc = DateTimeOffset.UtcNow; Publish("正在录音 · " + RecordingEndHint + " · Esc 取消（最长 2 分钟）", VoiceInputState.Recording); }
                }));
            }
            finally { holdWatch.Cancel(); await releaseWatch; }
            using var audio = recorded;
            recordedAt = elapsed.ElapsedMilliseconds;
            audioBytes = audio.Wav.Length;
            request.Token.ThrowIfCancellationRequested();
            Publish("正在识别语音… Esc 可取消", VoiceInputState.Recognizing);
            var original = await initialTask;
            if (original?.Password == true) throw new VoiceInputException("当前是密码输入框，请选择普通文字输入框后再使用语音输入。");
            submittedAt = elapsed.ElapsedMilliseconds;
            string text = VoiceText.ForTyping(await transcriber(credentials).TranscribeAsync(audio, request.Token));
            recognizedAt = elapsed.ElapsedMilliseconds;
            request.Token.ThrowIfCancellationRequested(); if (disposed || current != generation) return;
            LastText = text;
            var now = target.IsCurrent ? await activeProbe.ReadAsync(target.Foreground, request.Token) : null;
            bool safe = original is { Editable: true, Password: false } && now is { Editable: true, Password: false } && original.ProcessId == now.ProcessId && original.RuntimeId.SequenceEqual(now.RuntimeId) && target.IsCurrent;
            diagnostics?.Record("voice-input", "target-checked", new { OriginalAvailable = original is not null, OriginalEditable = original?.Editable, CurrentAvailable = now is not null, CurrentEditable = now?.Editable, SameInput = safe, WindowUnchanged = target.IsCurrent });
            int entered = 0;
            if (safe)
            {
                Publish("正在输入文字…", VoiceInputState.Inserting);
                entered = await type(text, () => !disposed && current == generation && target.IsCurrent, request.Token);
            }
            ReviewNeeded = entered != text.Length;
            string message = entered == text.Length ? "识别完成，文字已输入。" : entered > 0 ? "输入已停止，可能只输入了部分文字；完整结果已保留。" : "识别完成，输入位置变化或无法确认可输入；结果已保留，可查看并复制。";
            completion = message;
            diagnostics?.Record("voice-input", "completed", new { Characters = text.Length, InsertedCharacters = entered, AutomaticallyInserted = entered == text.Length });
        }
        catch (OperationCanceledException) { completion = "语音输入已取消。"; }
        catch (Exception error)
        {
            diagnostics?.Record("voice-input", "failed", new { Stage = State.ToString(), Failure = (error as VoiceInputException)?.Failure.ToString(), NativeCode = (error as VoiceInputException)?.NativeCode }, error: error);
            completion = error is VoiceInputException or ArgumentException ? error.Message : "语音输入未完成，请检查麦克风和识别服务设置。";
            LastFailure = completion;
        }
        finally
        {
            diagnostics?.Record("voice-input", "timing", new
            {
                TotalMs = elapsed.ElapsedMilliseconds,
                RecordingAndStartupMs = recordedAt > 0 ? recordedAt : (long?)null,
                InitialTargetWaitMs = submittedAt > 0 ? submittedAt - recordedAt : (long?)null,
                RecognitionMs = submittedAt > 0 ? (recognizedAt > 0 ? recognizedAt : elapsed.ElapsedMilliseconds) - submittedAt : (long?)null,
                FinalTargetAndInputMs = recognizedAt > 0 ? elapsed.ElapsedMilliseconds - recognizedAt : (long?)null,
                AudioBytes = audioBytes
            });
            request.Cancel(); target?.Dispose(); if (ReferenceEquals(currentTarget, target)) currentTarget = null;
            if (ReferenceEquals(active, request)) { active = null; stop = null; }
            if (!disposed && current == generation) Publish(completion, VoiceInputState.Idle);
        }
    }
    public void Cancel(bool hide = false) { dispatcher.VerifyAccess(); SuppressNotification |= hide; active?.Cancel(); currentTarget?.Dispose(); }
    private void Publish(string message, VoiceInputState state) { Status = message; State = state; if (!disposed) Changed?.Invoke(this, EventArgs.Empty); }
    public void Dispose() { if (disposed) return; disposed = true; ++generation; active?.Cancel(); currentTarget?.Dispose(); if (hotkey is not null) { hotkey.Triggered -= Triggered; hotkey.Dispose(); hotkey = null; } }
}
