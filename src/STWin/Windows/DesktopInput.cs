using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace STWin.Windows;

internal static class DesktopInput
{
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MapVirtualKeyToScanCodeEx = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public MouseInput Mouse; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

    public static void MoveMouse(int x, int y) => SendMouse([MouseMove(x, y)]);

    public static void ClickMouse(int x, int y, string button, int count)
    {
        if (count is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(count), "点击次数只能为 1 或 2。");
        var flags = button.ToLowerInvariant() switch
        {
            "left" => (Down: 0x0002u, Up: 0x0004u),
            "right" => (Down: 0x0008u, Up: 0x0010u),
            "middle" => (Down: 0x0020u, Up: 0x0040u),
            _ => throw new ArgumentException("鼠标按键只能是 left、right 或 middle。", nameof(button))
        };
        var inputs = new List<NativeInput> { MouseMove(x, y) };
        for (var i = 0; i < count; i++)
        {
            inputs.Add(new() { Mouse = new() { Flags = flags.Down } });
            inputs.Add(new() { Mouse = new() { Flags = flags.Up } });
        }
        SendMouse(inputs.ToArray());
    }

    public static void Scroll(int x, int y, int delta)
    {
        if (delta is < -1200 or > 1200) throw new ArgumentOutOfRangeException(nameof(delta), "滚轮值需介于 -1200 至 1200。");
        SendMouse([MouseMove(x, y), new() { Mouse = new() { Data = unchecked((uint)delta), Flags = 0x0800 } }]);
    }

    private static NativeInput MouseMove(int x, int y)
    {
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Contains(x, y))
            throw new ArgumentOutOfRangeException(nameof(x), "鼠标坐标不在分身桌面内。");
        return new()
        {
            Mouse = new()
            {
                X = checked((int)(((long)(x - bounds.Left) * 65536 + 32768) / bounds.Width)),
                Y = checked((int)(((long)(y - bounds.Top) * 65536 + 32768) / bounds.Height)),
                Flags = 0x0001 | 0x4000 | 0x8000 // MOVE | VIRTUALDESK | ABSOLUTE
            }
        };
    }

    private static void SendMouse(NativeInput[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
        {
            var code = Marshal.GetLastWin32Error();
            throw new Win32Exception(code, $"Windows 未能完整发送分身鼠标事件（Windows {code}）。");
        }
    }

    public static void PressKey(RdpHost host, Keys key, IReadOnlyList<Keys> modifiers)
    {
        var strokes = new List<Keys>(modifiers.Count * 2 + 2);
        foreach (var modifier in modifiers) strokes.Add(modifier);
        strokes.Add(key);
        var events = new List<(Keys Key, bool Up)>(strokes.Count * 2);
        foreach (var stroke in strokes) events.Add((stroke, false));
        for (var i = strokes.Count - 1; i >= 0; i--) events.Add((strokes[i], true));
        SendEvents(host, events);
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
        if (!Enum.TryParse<Keys>(normalized, true, out var key) || key == Keys.None || (int)key > 0xff)
            throw new ArgumentException($"不支持的键盘按键：{value}", nameof(value));
        return key;
    }

    private static void SendEvents(RdpHost host, IReadOnlyList<(Keys Key, bool Up)> events)
    {
        if (events.Count > 20) throw new ArgumentOutOfRangeException(nameof(events), "RDP 一次最多发送 20 个按键事件。");
        var viewport = host.RemoteViewport;
        var layout = GetKeyboardLayout(0);
        var altHeld = false;
        foreach (var item in events) if (item.Key == Keys.Menu && !item.Up) altHeld = true;
        for (var i = 0; i < events.Count; i++)
        {
            var keyData = GetKeyData((ushort)events[i].Key, events[i].Up, altHeld, layout);
            var message = altHeld
                ? events[i].Up ? WmSysKeyUp : WmSysKeyDown
                : events[i].Up ? WmKeyUp : WmKeyDown;
            host.PostRemoteKey(viewport.Handle, message, events[i].Key, keyData);
        }
    }

    private static int GetKeyData(ushort virtualKey, bool keyUp, bool altContext, IntPtr layout)
    {
        var scanCode = MapVirtualKeyEx(virtualKey, MapVirtualKeyToScanCodeEx, layout);
        if (scanCode == 0) throw new ArgumentException($"Windows 无法映射按键码 {virtualKey} 到扫描码。");
        var result = 1 | ((int)(scanCode & 0xff) << 16);
        if ((scanCode & 0xff00) is 0xe000 or 0xe100) result |= 1 << 24;
        if (altContext) result |= 1 << 29;
        if (keyUp) result |= (1 << 30) | (1 << 31);
        return result;
    }
}
