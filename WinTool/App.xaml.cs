using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace WinTool;

public partial class App : Application
{
    const int HotkeyId = 0x5754; // 'WT'
    const string MutexName = @"Local\WinTool.SingleInstance";
    const string ShowEventName = @"Local\WinTool.ShowSettings";

    Mutex? _mutex;
    EventWaitHandle? _showEvent;
    HwndSource? _hotkeyWindow;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem? _engineItem;
    WinForms.ToolStripMenuItem? _screenshotItem;
    WinForms.ToolStripMenuItem? _screenshotAction;
    MainWindow? _main;
    KeyboardEngine? _engine;
    bool _exiting;
    bool _cleaned;
    bool _hotkeyMuted;

    public AppConfig Config { get; private set; } = new();
    public bool IsExiting => _exiting;
    public KeyboardEngine? Engine => _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing)) existing.Set();
            Shutdown();
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowSettings)),
            null, Timeout.Infinite, executeOnlyOnce: false);

        Config = ConfigStore.Load();
        _engine = new KeyboardEngine();
        _engine.Apply(Config.Mappings, Config.EngineEnabled);

        CreateTray();
        CreateHotkeyWindow();
        if (Config.ScreenshotEnabled) RegisterHotkey(Config.Hotkey);

        DispatcherUnhandledException += OnUnhandledException;
        SessionEnding += (_, _) => Cleanup();
    }

    // ---- 托盘 ----

    void CreateTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "WinTool",
            Visible = true,
        };

        var menu = new WinForms.ContextMenuStrip();

        var settings = new WinForms.ToolStripMenuItem("打开设置");
        settings.Click += (_, _) => ShowSettings();

        _engineItem = new WinForms.ToolStripMenuItem("启用按键映射")
        {
            CheckOnClick = true,
            Checked = Config.EngineEnabled,
        };
        _engineItem.CheckedChanged += (_, _) => SetEngineEnabled(_engineItem.Checked);

        _screenshotItem = new WinForms.ToolStripMenuItem("启用截图")
        {
            CheckOnClick = true,
            Checked = Config.ScreenshotEnabled,
        };
        _screenshotItem.CheckedChanged += (_, _) => SetScreenshotEnabled(_screenshotItem.Checked);

        _screenshotAction = new WinForms.ToolStripMenuItem("截图")
        {
            Enabled = Config.ScreenshotEnabled,
        };
        _screenshotAction.Click += (_, _) => BeginScreenshot();

        var exit = new WinForms.ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApp();

        menu.Items.Add(settings);
        menu.Items.Add(_engineItem);
        menu.Items.Add(_screenshotItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_screenshotAction);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exit);

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    static Drawing.Icon LoadAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (path is not null)
                return Drawing.Icon.ExtractAssociatedIcon(path) ?? Drawing.SystemIcons.Application;
        }
        catch { }
        return Drawing.SystemIcons.Application;
    }

    void ShowSettings()
    {
        if (_main is null) _main = new MainWindow(this);
        _main.SyncToggles();
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
    }

    // ---- 热键 ----

    void CreateHotkeyWindow()
    {
        var parameters = new HwndSourceParameters("WinTool.HotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        _hotkeyWindow = new HwndSource(parameters);
        _hotkeyWindow.AddHook(HotkeyWndProc);
    }

    IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(new Action(BeginScreenshot));
        }
        return IntPtr.Zero;
    }

    bool RegisterHotkey(Chord chord)
    {
        UnregisterHotkey();
        if (_hotkeyWindow is null || chord.IsEmpty) return false;

        uint mods = Native.MOD_NOREPEAT;
        if ((chord.Mods & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
        if ((chord.Mods & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
        if ((chord.Mods & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
        if ((chord.Mods & ModifierKeys.Windows) != 0) mods |= Native.MOD_WIN;

        int vk = KeyInterop.VirtualKeyFromKey(chord.Key);
        if (vk == 0) return false;

        if (!Native.RegisterHotKey(_hotkeyWindow.Handle, HotkeyId, mods, (uint)vk))
        {
            _tray?.ShowBalloonTip(3000, "WinTool",
                $"截图热键 {chord} 注册失败（可能被其他程序占用），可从托盘菜单截图。",
                WinForms.ToolTipIcon.Warning);
            return false;
        }
        return true;
    }

    void UnregisterHotkey()
    {
        if (_hotkeyWindow is not null)
            Native.UnregisterHotKey(_hotkeyWindow.Handle, HotkeyId);
    }

    // ---- 截图 ----

    async void BeginScreenshot()
    {
        if (SelectionSession.Active) return; // 已有截图会话（框选中或标注中）
        try
        {
            // 等托盘菜单/阴影消失，避免被截进画面
            await Task.Delay(150);
            var shot = ScreenCapture.CaptureVirtualScreen();
            SelectionSession.Begin(shot, rect =>
            {
                if (rect is null) return;
                try
                {
                    var sel = rect.Value;
                    var window = new AnnotationWindow(shot, sel, this);
                    // 覆盖层还在（压暗），标注窗口挂到所在显示器的覆盖窗下，
                    // 免得点遮罩时覆盖层跑到标注窗口上面。
                    var owner = SelectionSession.OwnerAt(sel.X + sel.Width / 2, sel.Y + sel.Height / 2);
                    if (owner is not null) window.Owner = owner;
                    window.Closed += (_, _) => SelectionSession.Finish();
                    window.Show();
                    window.Activate();
                }
                catch (Exception ex)
                {
                    ConfigStore.Log("打开标注窗口失败", ex);
                    SelectionSession.Finish();
                }
            });
        }
        catch (Exception ex)
        {
            ConfigStore.Log("截图失败", ex);
            _tray?.ShowBalloonTip(3000, "WinTool", "截图失败：" + ex.Message, WinForms.ToolTipIcon.Error);
        }
    }

    // ---- 配置变更入口 ----

    public void SetSuspended(bool suspended)
    {
        if (_engine is not null)
        {
            _engine.Suspended = suspended;
            // 没启用映射时钩子通常不装，但录入时段要强制装上：
            // 音量键等媒体键 WPF 收不到，只能从低级钩子捕获。
            if (suspended) _engine.Install();
            else _engine.Apply(Config.Mappings, Config.EngineEnabled);
        }

        // 录入按键期间必须注销全局热键，否则系统热键会吞掉按键，
        // 用户无法把当前热键重新录进去。
        if (suspended == _hotkeyMuted) return;
        _hotkeyMuted = suspended;
        if (suspended) UnregisterHotkey();
        else if (Config.ScreenshotEnabled) RegisterHotkey(Config.Hotkey);
    }

    public void SetMappings(System.Collections.Generic.List<Mapping> mappings)
    {
        Config.Mappings = mappings;
        ApplyEngine();
    }

    void ApplyEngine()
    {
        _engine?.Apply(Config.Mappings, Config.EngineEnabled);
        ConfigStore.Save(Config);
        if (_engineItem is not null && _engineItem.Checked != Config.EngineEnabled)
            _engineItem.Checked = Config.EngineEnabled;
        _main?.SyncToggles();
    }

    public void SetEngineEnabled(bool enabled)
    {
        Config.EngineEnabled = enabled;
        ApplyEngine();
    }

    public void SetScreenshotEnabled(bool enabled)
    {
        Config.ScreenshotEnabled = enabled;
        if (enabled) RegisterHotkey(Config.Hotkey);
        else UnregisterHotkey();
        ConfigStore.Save(Config);

        if (_screenshotItem is not null && _screenshotItem.Checked != enabled)
            _screenshotItem.Checked = enabled;
        if (_screenshotAction is not null) _screenshotAction.Enabled = enabled;
        _main?.SyncToggles();
    }

    public bool SetHotkey(Chord chord)
    {
        var previous = Config.Hotkey;
        Config.Hotkey = chord;

        // 截图功能未启用或设置界面录入期间，只保存不注册
        if (!Config.ScreenshotEnabled || _hotkeyMuted)
        {
            ConfigStore.Save(Config);
            return true;
        }

        if (!RegisterHotkey(chord))
        {
            Config.Hotkey = previous;
            RegisterHotkey(previous);
            ConfigStore.Save(Config);
            return false;
        }
        ConfigStore.Save(Config);
        return true;
    }

    // ---- 退出 / 异常 ----

    void ExitApp()
    {
        _exiting = true;
        _main?.Close();
        Cleanup();
        Shutdown();
    }

    void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;

        _engine?.Uninstall();
        UnregisterHotkey();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _hotkeyWindow?.Dispose();
        _hotkeyWindow = null;

        _showEvent?.Dispose();
        _showEvent = null;

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ConfigStore.Log("未处理异常", e.Exception);
        _tray?.ShowBalloonTip(3000, "WinTool",
            @"发生错误，详情见程序目录下 data\error.log", WinForms.ToolTipIcon.Error);
        e.Handled = true;
    }
}
