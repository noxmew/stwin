using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace STWin.Windows;

internal static class DesktopInput
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLeftButtonDown = 0x0201;
    private const uint WmLeftButtonUp = 0x0202;
    private const uint WmRightButtonDown = 0x0204;
    private const uint WmRightButtonUp = 0x0205;
    private const uint WmMiddleButtonDown = 0x0207;
    private const uint WmMiddleButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint MkLeftButton = 0x0001;
    private const uint MkRightButton = 0x0002;
    private const uint MkMiddleButton = 0x0010;
    private const uint MapVirtualKeyToScanCodeEx = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr keyboardLayout);

    public static void MoveMouse(RdpHost host, IntPtr window, int x, int y, int screenX, int screenY)
        => host.PostRemoteMouse(window, WmMouseMove, UIntPtr.Zero, x, y, screenX, screenY);

    public static void ClickMouse(RdpHost host, IntPtr window, int x, int y, int screenX, int screenY, string button, int count)
    {
        host.PostRemoteMouse(window, WmMouseMove, UIntPtr.Zero, x, y, screenX, screenY);
        var messages = button.ToLowerInvariant() switch
        {
            "left" => (Down: WmLeftButtonDown, Up: WmLeftButtonUp, State: MkLeftButton),
            "right" => (Down: WmRightButtonDown, Up: WmRightButtonUp, State: MkRightButton),
            "middle" => (Down: WmMiddleButtonDown, Up: WmMiddleButtonUp, State: MkMiddleButton),
            _ => throw new ArgumentException("鼠标按键只能是 left、right 或 middle。", nameof(button))
        };
        for (var i = 0; i < count; i++)
        {
            host.PostRemoteMouse(window, messages.Down, new UIntPtr(messages.State), x, y, screenX, screenY);
            host.PostRemoteMouse(window, messages.Up, UIntPtr.Zero, x, y, screenX, screenY);
        }
    }

    public static void Scroll(RdpHost host, IntPtr window, int x, int y, int screenX, int screenY, int delta)
    {
        var wheel = unchecked((uint)(ushort)(short)delta << 16);
        host.PostRemoteMouse(window, WmMouseWheel, new UIntPtr(wheel), x, y, screenX, screenY, true);
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
