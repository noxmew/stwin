using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace STWin.Windows;

internal sealed record KeyPressDelivery(string Key, int HoldMs, uint ForegroundProcessId, string ForegroundWindowTitle);

internal static class DesktopInput
{
    private const uint MapVirtualKeyToScanCodeEx = 4;
    public const int DefaultHoldMs = 100;
    public const int DefaultIntervalMs = 0;
    public const int MaximumDurationMs = 10000;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput { public ushort VirtualKey, ScanCode; public uint Flags, Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputData Data; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximum);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

    public static void MoveMouse(int x, int y) => SendEvents([MouseMove(x, y)], "鼠标");

    public static async Task ClickMouseAsync(int x, int y, string button, int count, int holdMs, int intervalMs, CancellationToken token)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count), "点击次数只能为 1 或 2。");
        ValidateDuration(holdMs, nameof(holdMs));
        ValidateDuration(intervalMs, nameof(intervalMs));
        var flags = button.ToLowerInvariant() switch
        {
            "left" => (Down: 0x0002u, Up: 0x0004u),
            "right" => (Down: 0x0008u, Up: 0x0010u),
            "middle" => (Down: 0x0020u, Up: 0x0040u),
            _ => throw new ArgumentException("鼠标按键只能是 left、right 或 middle。", nameof(button))
        };
        token.ThrowIfCancellationRequested();
        SendEvents([MouseMove(x, y)], "鼠标");
        for (var i = 0; i < count; i++)
        {
            uint pressed = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                SendEvents([new() { Data = new() { Mouse = new() { Flags = flags.Down } } }], "鼠标", out pressed);
                await Task.Delay(holdMs, token).ConfigureAwait(false);
            }
            finally
            {
                if (pressed > 0) SendEvents([new() { Data = new() { Mouse = new() { Flags = flags.Up } } }], "鼠标");
            }
            if (i + 1 < count) await Task.Delay(intervalMs, token).ConfigureAwait(false);
        }
    }

    public static void Scroll(int x, int y, int delta)
    {
        if (delta is < -1200 or > 1200) throw new ArgumentOutOfRangeException(nameof(delta), "滚轮值需介于 -1200 至 1200。");
        SendEvents([MouseMove(x, y), new() { Data = new() { Mouse = new() { Data = unchecked((uint)delta), Flags = 0x0800 } } }], "鼠标");
    }

    private static NativeInput MouseMove(int x, int y)
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), "鼠标坐标不在分身桌面内。");
        return new()
        {
            Data = new()
            {
                Mouse = new()
                {
                    X = checked((int)(((long)(x - bounds.Left) * 65536 + 32768) / bounds.Width)),
                    Y = checked((int)(((long)(y - bounds.Top) * 65536 + 32768) / bounds.Height)),
                    Flags = 0x0001 | 0x4000 | 0x8000 // MOVE | VIRTUALDESK | ABSOLUTE
                }
            }
        };
    }

    private static void SendEvents(NativeInput[] inputs, string device)
        => SendEvents(inputs, device, out _);

    private static void SendEvents(NativeInput[] inputs, string device, out uint sent)
    {
        sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (sent != inputs.Length)
        {
            var code = Marshal.GetLastWin32Error();
            throw new Win32Exception(code, $"Windows 未能完整发送分身{device}事件（Windows {code}）。");
        }
    }

    public static async Task<KeyPressDelivery> PressKeyAsync(Keys key, IReadOnlyList<Keys> modifiers, int holdMs, CancellationToken token)
    {
        ValidateDuration(holdMs, nameof(holdMs));
        token.ThrowIfCancellationRequested();
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) throw new InvalidOperationException("分身桌面没有活动窗口，请先点击要操作的窗口。");
        var thread = GetWindowThreadProcessId(window, out var processId);
        if (thread == 0) throw new InvalidOperationException("无法确定分身桌面当前活动窗口。");
        var layout = GetKeyboardLayout(thread);
        if (layout == IntPtr.Zero) throw new InvalidOperationException("无法读取分身活动窗口的键盘布局。");
        var title = new StringBuilder(1024);
        GetWindowTextW(window, title, title.Capacity);
        var down = new NativeInput[modifiers.Count + 1];
        var up = new NativeInput[down.Length];
        for (var i = 0; i < modifiers.Count; i++)
        {
            down[i] = KeyEvent(modifiers[i], false, layout);
            up[up.Length - 1 - i] = KeyEvent(modifiers[i], true, layout);
        }
        down[^1] = KeyEvent(key, false, layout);
        up[0] = KeyEvent(key, true, layout);
        uint pressed = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            SendEvents(down, "键盘", out pressed);
            // Games that poll key state once per frame can miss a down/up in the same batch.
            await Task.Delay(holdMs, token).ConfigureAwait(false);
        }
        finally
        {
            // Release every injected key even on cancellation or a partial key-down failure.
            if (pressed > 0) SendEvents(up[(up.Length - checked((int)pressed))..], "键盘");
        }
        return new(key.ToString(), holdMs, processId, title.ToString());
    }

    public static int ValidateDuration(int milliseconds, string name)
    {
        if (milliseconds is < 0 or > MaximumDurationMs)
            throw new ArgumentOutOfRangeException(name, $"参数 {name} 需介于 0 至 {MaximumDurationMs} 毫秒。");
        return milliseconds;
    }

    public static Keys ParseKey(string value)
    {
        var normalized = value.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => "ControlKey",
            "SHIFT" => "ShiftKey",
            "ALT" => "Menu",
            "WIN" or "WINDOWS" or "META" => "LWin",
            "ESC" => "Escape",
            "RETURN" => "Enter",
            "SPACE" => "Space",
            "PGUP" => "PageUp",
            "PGDN" => "PageDown",
            "DEL" => "Delete",
            "INS" => "Insert",
            _ => value.Trim()
        };
        if (!Enum.TryParse<Keys>(normalized, true, out var key) || (uint)key is 0 or > 0xff)
            throw new ArgumentException($"不支持的键盘按键：{value}", nameof(value));
        return key;
    }

    private static NativeInput KeyEvent(Keys key, bool keyUp, IntPtr layout)
    {
        var flags = keyUp ? 0x0002u : 0; // KEYEVENTF_KEYUP
        // Pause has an E1 sequence that cannot be represented by KEYEVENTF_EXTENDEDKEY.
        if (key == Keys.Pause)
            return new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = (ushort)key, Flags = flags } } };
        var scanCode = MapVirtualKeyEx((uint)key, MapVirtualKeyToScanCodeEx, layout);
        if (scanCode == 0) throw new ArgumentException($"Windows 无法映射按键码 {(int)key} 到扫描码。");
        if ((scanCode & 0xff00) == 0xe000) flags |= 0x0001; // KEYEVENTF_EXTENDEDKEY
        flags |= 0x0008; // KEYEVENTF_SCANCODE
        return new() { Type = 1, Data = new() { Keyboard = new() { ScanCode = (ushort)(scanCode & 0xff), Flags = flags } } };
    }
}
