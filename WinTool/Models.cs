using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace WinTool;

/// <summary>一个组合键：修饰键 + 主键。直接复用 WPF 的 Key / ModifierKeys。</summary>
public readonly record struct Chord(ModifierKeys Mods, Key Key)
{
    [JsonIgnore]
    public bool IsEmpty => Key == Key.None;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if ((Mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Mods & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    public static bool IsModifierKey(Key key) => key is
        Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;

    static string KeyName(Key key) => key switch
    {
        Key.D0 => "0",
        Key.D1 => "1",
        Key.D2 => "2",
        Key.D3 => "3",
        Key.D4 => "4",
        Key.D5 => "5",
        Key.D6 => "6",
        Key.D7 => "7",
        Key.D8 => "8",
        Key.D9 => "9",
        _ => key.ToString(),
    };
}

public sealed class Mapping
{
    public bool Enabled { get; set; } = true;
    public Chord Source { get; set; }
    public Chord Target { get; set; }

    [JsonIgnore]
    public string SourceText => Source.ToString();

    [JsonIgnore]
    public string TargetText => Target.ToString();
}

public sealed class AppConfig
{
    public List<Mapping> Mappings { get; set; } = new();
    public Chord Hotkey { get; set; } = new(ModifierKeys.Control | ModifierKeys.Shift, Key.A);
    public bool EngineEnabled { get; set; }
    public bool ScreenshotEnabled { get; set; }
}
