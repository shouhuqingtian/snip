namespace Snip.Core;

public enum PinHotkeyAction { Restore, Previous, Next }

public sealed record PinHotkeySettings(HotkeyGesture? Restore = null, HotkeyGesture? Previous = null, HotkeyGesture? Next = null, int Version = 1)
{
    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("贴图快捷键配置版本不受支持。");
        var assigned = new[] { Restore, Previous, Next }.Where(value => value is not null).ToArray();
        foreach (var gesture in assigned) gesture!.Validate();
        if (assigned.Distinct().Count() != assigned.Length) throw new ArgumentException("不同贴图操作不能使用相同的快捷键。");
    }
    public HotkeyGesture? For(PinHotkeyAction action) => action switch
    {
        PinHotkeyAction.Restore => Restore, PinHotkeyAction.Previous => Previous, PinHotkeyAction.Next => Next,
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}

public interface IPinHotkeySettingsStore
{
    PinHotkeySettings Read();
    void Save(PinHotkeySettings settings);
}

public interface IPinHotkeyService : IDisposable
{
    PinHotkeySettings Current { get; }
    bool Apply(PinHotkeySettings settings);
    event EventHandler<PinHotkeyAction>? Triggered;
}
