using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace WinTool;

/// <summary>
/// 全局按键映射：低级键盘钩子 + SendInput。
/// 回调只做集合/字典查找和注入，不做任何 I/O 或 Dispatcher 同步调用。
/// </summary>
public sealed class KeyboardEngine
{
    // 必须由静态字段持有委托，否则被 GC 后钩子静默失效。
    static readonly Native.LowLevelKeyboardProc s_proc = HookCallback;
    static KeyboardEngine? s_current;

    readonly Dictionary<(ModifierKeys Mods, Key Key), Chord> _map = new();
    readonly HashSet<uint> _down = new();
    readonly HashSet<uint> _swallowed = new();
    IntPtr _hook;

    public KeyboardEngine()
    {
        s_current = this;
    }

    /// <summary>为 true 时钩子只记录物理按键状态，不匹配、不吞键（用于设置界面的按键录入）。</summary>
    public bool Suspended { get; set; }

    /// <summary>录入期间报告按下的原始 VK（音量键等 WPF 收不到，只能从这里拿）。在 UI 线程触发。</summary>
    public event Action<uint>? KeyCaptured;

    /// <summary>当前物理按住的修饰键。</summary>
    public ModifierKeys CurrentModifiers => ModsFromDown(_down);

    public bool IsHooked => _hook != IntPtr.Zero;

    public void Apply(IEnumerable<Mapping> mappings, bool enabled)
    {
        _map.Clear();
        if (enabled)
        {
            foreach (var m in mappings)
            {
                if (m.Enabled && m.Source.Key != Key.None && m.Target.Key != Key.None)
                    _map[(m.Source.Mods, m.Source.Key)] = m.Target;
            }
        }

        if (_map.Count > 0) Install();
        else Uninstall();
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, s_proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
            ConfigStore.Log($"键盘钩子安装失败，错误码 {Marshal.GetLastWin32Error()}");
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _down.Clear();
        _swallowed.Clear();
    }

    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && s_current is { } engine)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            // 忽略自己注入的事件，避免循环匹配。
            if ((info.flags & Native.LLKHF_INJECTED) == 0)
            {
                if (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN)
                {
                    if (engine.OnKeyDown(info.vkCode)) return (IntPtr)1;
                }
                else if (wParam == (IntPtr)Native.WM_KEYUP || wParam == (IntPtr)Native.WM_SYSKEYUP)
                {
                    if (engine.OnKeyUp(info.vkCode)) return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    bool OnKeyDown(uint vk)
    {
        bool repeat = !_down.Add(vk);
        if (Suspended)
        {
            if (!repeat) KeyCaptured?.Invoke(vk);
            return false;
        }
        if (repeat) return _swallowed.Contains(vk); // 按住触发键：连发的 keydown 一并吞掉
        if (IsModifierVk(vk) || _map.Count == 0) return false;

        var mods = ModsFromDown();
        var key = KeyInterop.KeyFromVirtualKey((int)vk);
        if (!_map.TryGetValue((mods, key), out var target)) return false;

        Inject(target, mods);
        _swallowed.Add(vk);
        return true;
    }

    bool OnKeyUp(uint vk)
    {
        _down.Remove(vk);
        if (Suspended) return false;
        return _swallowed.Remove(vk);
    }

    void Inject(Chord target, ModifierKeys held)
    {
        var inputs = new List<Native.INPUT>(10);

        // 1) 释放目标不需要的源修饰键（按物理上按下的具体 L/R 键释放）
        var released = new List<uint>();
        foreach (var vk in _down)
        {
            var flag = ModifierFlagOf(vk);
            if (flag != ModifierKeys.None && (held & flag) != 0 && (target.Mods & flag) == 0)
                released.Add(vk);
        }
        // 释放 Alt 前先打一个哑键，避免窗口过程把"Alt 单独按下又抬起"当成菜单激活
        if (released.Contains(0xA4u) || released.Contains(0xA5u))
        {
            AddKey(inputs, 0xFF, up: false);
            AddKey(inputs, 0xFF, up: true);
        }

        foreach (var vk in released) AddKey(inputs, vk, up: true);

        // 2) 按下目标需要但当前未按住的修饰键
        var pressed = new List<uint>();
        foreach (var vk in ModifierVks(target.Mods & ~held))
        {
            AddKey(inputs, vk, up: false);
            pressed.Add(vk);
        }

        // 3) 目标键
        int targetVk = KeyInterop.VirtualKeyFromKey(target.Key);
        if (targetVk != 0)
        {
            AddKey(inputs, (uint)targetVk, up: false);
            AddKey(inputs, (uint)targetVk, up: true);
        }

        // 4) 释放目标修饰键
        foreach (var vk in pressed) AddKey(inputs, vk, up: true);

        // 5) 恢复源修饰键的物理状态
        foreach (var vk in released) AddKey(inputs, vk, up: false);

        if (inputs.Count > 0)
            Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
    }

    static void AddKey(List<Native.INPUT> inputs, uint vk, bool up)
    {
        uint flags = 0;
        if (up) flags |= Native.KEYEVENTF_KEYUP;
        if (ExtendedVks.Contains(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        inputs.Add(new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new Native.INPUTUNION
            {
                ki = new Native.KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        });
    }

    static ModifierKeys ModsFromDown(HashSet<uint> down)
    {
        var mods = ModifierKeys.None;
        if (down.Contains(0x11) || down.Contains(0xA2) || down.Contains(0xA3)) mods |= ModifierKeys.Control;
        if (down.Contains(0x12) || down.Contains(0xA4) || down.Contains(0xA5)) mods |= ModifierKeys.Alt;
        if (down.Contains(0x10) || down.Contains(0xA0) || down.Contains(0xA1)) mods |= ModifierKeys.Shift;
        if (down.Contains(0x5B) || down.Contains(0x5C)) mods |= ModifierKeys.Windows;
        return mods;
    }

    ModifierKeys ModsFromDown() => ModsFromDown(_down);

    static bool IsModifierVk(uint vk) => ModifierFlagOf(vk) != ModifierKeys.None;

    static ModifierKeys ModifierFlagOf(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
        0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
        0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };

    static IEnumerable<uint> ModifierVks(ModifierKeys mods)
    {
        if ((mods & ModifierKeys.Control) != 0) yield return 0xA2; // 左 Ctrl
        if ((mods & ModifierKeys.Alt) != 0) yield return 0xA4;     // 左 Alt
        if ((mods & ModifierKeys.Shift) != 0) yield return 0xA0;   // 左 Shift
        if ((mods & ModifierKeys.Windows) != 0) yield return 0x5B; // 左 Win
    }

    // E0 前缀（扩展键）白名单。不要用 MapVirtualKey 推 scancode，它不携带 E0 信息。
    static readonly HashSet<uint> ExtendedVks = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home ← ↑ → ↓
        0x2D, 0x2E, 0x2C, 0x90, 0x6F,                   // Insert Delete PrintScreen NumLock Divide
        0xA3, 0xA5, 0x5B, 0x5C,                         // 右 Ctrl / 右 Alt / LWin / RWin
        0xAD, 0xAE, 0xAF,                               // VolumeMute / Down / Up
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xAB, 0xAC,       // Browser 键
        0xB0, 0xB1, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, // 媒体键
    };
}
