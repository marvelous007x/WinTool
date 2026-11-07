using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace WinTool;

public partial class MainWindow : Window
{
    readonly App _app;
    readonly ObservableCollection<Mapping> _items;
    Mapping? _editing;
    Chord? _source;
    Chord? _target;
    TextBox? _captureBox;
    bool _ready;

    public MainWindow(App app)
    {
        InitializeComponent();
        _app = app;
        _items = new ObservableCollection<Mapping>(app.Config.Mappings);
        MappingList.ItemsSource = _items;
        HotkeyBox.Text = app.Config.Hotkey.ToString();
        EngineEnabledBox.IsChecked = app.Config.EngineEnabled;
        ScreenshotEnabledBox.IsChecked = app.Config.ScreenshotEnabled;
        if (app.Engine is { } engine) engine.KeyCaptured += OnHookKeyCaptured;
        Closing += OnClosing;
        Deactivated += (_, _) => _app.SetSuspended(false);
        _ready = true;
    }

    /// <summary>托盘切换或外部修改后刷新勾选状态。</summary>
    public void SyncToggles()
    {
        bool ready = _ready;
        _ready = false;
        EngineEnabledBox.IsChecked = _app.Config.EngineEnabled;
        ScreenshotEnabledBox.IsChecked = _app.Config.ScreenshotEnabled;
        _ready = ready;
    }

    void EngineToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _app.SetEngineEnabled(EngineEnabledBox.IsChecked == true);
    }

    void ScreenshotToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _app.SetScreenshotEnabled(ScreenshotEnabledBox.IsChecked == true);
    }

    void Options_Click(object sender, RoutedEventArgs e)
    {
        new OptionsWindow { Owner = this }.ShowDialog();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        _app.SetSuspended(false);
        if (!_app.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    // ---- 映射列表 ----

    void Add_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        _source = null;
        _target = null;
        SourceBox.Text = "";
        TargetBox.Text = "";
        EditorBox.Visibility = Visibility.Visible;
        SourceBox.Focus();
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (MappingList.SelectedItem is not Mapping mapping) return;
        _editing = mapping;
        _source = mapping.Source;
        _target = mapping.Target;
        SourceBox.Text = mapping.Source.ToString();
        TargetBox.Text = mapping.Target.ToString();
        EditorBox.Visibility = Visibility.Visible;
        SourceBox.Focus();
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MappingList.SelectedItem is not Mapping mapping) return;
        _items.Remove(mapping);
        SaveAndApply();
    }

    void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        if (sender is CheckBox box && box.DataContext is Mapping mapping)
            mapping.Enabled = box.IsChecked == true;
        SaveAndApply();
    }

    void SaveEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_source is null || _target is null)
        {
            MessageBox.Show(this, "请先点击源按键和目标按键输入框，并按下对应按键。", "WinTool");
            return;
        }

        var error = Validate(_source.Value, _target.Value);
        if (error is not null)
        {
            MessageBox.Show(this, error, "WinTool");
            return;
        }

        var mapping = new Mapping
        {
            Enabled = _editing?.Enabled ?? true,
            Source = _source.Value,
            Target = _target.Value,
        };

        if (_editing is not null)
        {
            int index = _items.IndexOf(_editing);
            if (index >= 0) _items[index] = mapping;
            else _items.Add(mapping);
        }
        else
        {
            _items.Add(mapping);
        }

        _editing = null;
        _source = null;
        _target = null;
        EditorBox.Visibility = Visibility.Collapsed;
        SaveAndApply();
    }

    void CancelEdit_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        _source = null;
        _target = null;
        EditorBox.Visibility = Visibility.Collapsed;
    }

    string? Validate(Chord source, Chord target)
    {
        if (Chord.IsModifierKey(source.Key)) return "源按键必须是普通键，不能只有修饰键。";
        if (Chord.IsModifierKey(target.Key)) return "目标按键必须是普通键。";
        if ((source.Mods & ModifierKeys.Windows) != 0) return "暂不支持 Win 键作为源修饰键。";
        if (_items.Any(m => !ReferenceEquals(m, _editing) && m.Source == source))
            return "该源按键已有映射，请先删除或修改原条目。";
        if (source == _app.Config.Hotkey)
            return "该组合键已用作截图热键，不能同时作为映射源。";
        return null;
    }

    void SaveAndApply()
    {
        if (!_ready) return;
        _app.SetMappings(_items.ToList());
    }

    // ---- 按键捕获 ----

    void Capture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Chord.IsModifierKey(key)) return; // 等用户按下一个非修饰键

        SetCaptured((TextBox)sender, new Chord(Keyboard.Modifiers, key));
    }

    /// <summary>低级钩子补录 WPF 收不到的媒体键（音量键等）。</summary>
    void OnHookKeyCaptured(uint vk)
    {
        var box = _captureBox;
        if (box is null || ReferenceEquals(box, HotkeyBox)) return;

        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        if (Chord.IsModifierKey(key)) return;
        SetCaptured(box, new Chord(_app.Engine?.CurrentModifiers ?? Keyboard.Modifiers, key));
    }

    void SetCaptured(TextBox box, Chord chord)
    {
        box.Text = chord.ToString();
        if (ReferenceEquals(box, SourceBox)) _source = chord;
        else if (ReferenceEquals(box, TargetBox)) _target = chord;
    }

    // 标准 104 键键盘上没有实体按键的键（媒体、浏览器、应用启动、F13+、旧式键），
    // 没法靠"按下去"录入，只能从菜单里选。
    static readonly (string Group, (string Label, Key Key)[] Keys)[] KeylessKeys =
    {
        ("音量", new (string, Key)[]
        {
            ("音量增大 (VolumeUp)", Key.VolumeUp),
            ("音量减小 (VolumeDown)", Key.VolumeDown),
            ("静音 (VolumeMute)", Key.VolumeMute),
        }),
        ("媒体控制", new (string, Key)[]
        {
            ("播放/暂停 (MediaPlayPause)", Key.MediaPlayPause),
            ("上一曲 (MediaPreviousTrack)", Key.MediaPreviousTrack),
            ("下一曲 (MediaNextTrack)", Key.MediaNextTrack),
            ("停止 (MediaStop)", Key.MediaStop),
        }),
        ("浏览器", new (string, Key)[]
        {
            ("后退 (BrowserBack)", Key.BrowserBack),
            ("前进 (BrowserForward)", Key.BrowserForward),
            ("刷新 (BrowserRefresh)", Key.BrowserRefresh),
            ("停止加载 (BrowserStop)", Key.BrowserStop),
            ("搜索 (BrowserSearch)", Key.BrowserSearch),
            ("收藏夹 (BrowserFavorites)", Key.BrowserFavorites),
            ("主页 (BrowserHome)", Key.BrowserHome),
        }),
        ("应用启动", new (string, Key)[]
        {
            ("邮件 (LaunchMail)", Key.LaunchMail),
            ("应用 1 (LaunchApplication1)", Key.LaunchApplication1),
            ("应用 2 (LaunchApplication2)", Key.LaunchApplication2),
        }),
        ("系统", new (string, Key)[]
        {
            ("睡眠 (Sleep)", Key.Sleep),
        }),
        ("功能键 F13–F24", new (string, Key)[]
        {
            ("F13", Key.F13), ("F14", Key.F14), ("F15", Key.F15), ("F16", Key.F16),
            ("F17", Key.F17), ("F18", Key.F18), ("F19", Key.F19), ("F20", Key.F20),
            ("F21", Key.F21), ("F22", Key.F22), ("F23", Key.F23), ("F24", Key.F24),
        }),
        ("旧式按键", new (string, Key)[]
        {
            ("选择 (Select)", Key.Select),
            ("打印 (Print)", Key.Print),
            ("执行 (Execute)", Key.Execute),
            ("帮助 (Help)", Key.Help),
            ("分隔符 (Separator)", Key.Separator),
            ("注意 (Attn)", Key.Attn),
            ("光标选择 (CrSel)", Key.CrSel),
            ("扩展选择 (ExSel)", Key.ExSel),
            ("擦除到结尾 (EraseEof)", Key.EraseEof),
            ("播放 (Play)", Key.Play),
            ("缩放 (Zoom)", Key.Zoom),
            ("Pa1", Key.Pa1),
            ("清除 (OemClear)", Key.OemClear),
        }),
    };

    void MediaKeys_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var box = (string)button.Tag == "source" ? SourceBox : TargetBox;

        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var (group, keys) in KeylessKeys)
        {
            var sub = new MenuItem { Header = group };
            foreach (var (label, key) in keys)
            {
                var item = new MenuItem { Header = label };
                item.Click += (_, _) => SetCaptured(box, new Chord(ModifierKeys.None, key));
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.IsOpen = true;
    }

    void Capture_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _captureBox = (TextBox)sender;
        _app.SetSuspended(true);
    }

    void Capture_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _captureBox = null;
        _app.SetSuspended(false);
    }

    // ---- 截图设置 ----

    void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Chord.IsModifierKey(key)) return;

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            HotkeyBox.Text = "需包含 Ctrl/Alt/Shift";
            return;
        }

        var chord = new Chord(mods, key);
        if (_items.Any(m => m.Source == chord))
        {
            MessageBox.Show(this, "该组合键已被某条映射占用，不能用作截图热键。", "WinTool");
            return;
        }

        if (!_app.SetHotkey(chord))
        {
            HotkeyBox.Text = _app.Config.Hotkey.ToString();
            return;
        }
        HotkeyBox.Text = chord.ToString();
    }
}
