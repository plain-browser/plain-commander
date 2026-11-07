using System.Runtime.InteropServices;
using Serilog;
using Windows.System;

namespace PlainCommander.App.Helpers;

/// <summary>
/// Keys typed while the keyboard has no proper home: a dialog that is closing (its animation runs for ~200 ms) or still
/// opening, or focus that WinUI parked in the inactive panel after a list was replaced. Such a key would be lost or
/// would act on the wrong thing (Enter on the other panel's breadcrumb, Enter on the list behind an opening dialog).
/// They are held and typed again, in order, once focus has settled, so they reach whatever then has the keyboard.
/// </summary>
internal sealed partial class HeldKeys
{
    private const int MaxHeld = 32;
    private readonly List<(VirtualKey Key, VirtualKeyModifiers Modifiers)> _keys = [];

    /// <summary>Keys sent by <see cref="Replay(int)"/> that have not come back through the window yet.</summary>
    private readonly Queue<(VirtualKey Key, long Ticks)> _inFlight = new();

    /// <summary>Held keys whose release has not come yet: it is swallowed too (a menu bar item opens on it).</summary>
    private readonly HashSet<VirtualKey> _awaitingUp = [];

    public bool Any => _keys.Count > 0;

    /// <summary>Modifier keys on their own are never held: they are pressed again as part of each chord.</summary>
    public static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu
        or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>Keys that must keep their normal path even then (Alt+F4 closes the window).</summary>
    public static bool IsExempt(VirtualKey key, VirtualKeyModifiers modifiers) =>
        IsModifier(key) || (key == VirtualKey.F4 && modifiers.HasFlag(VirtualKeyModifiers.Menu));

    /// <summary>
    /// True when this key press is one this class typed again (so it is not held a second time behind the later ones).
    /// Entries older than a second are stale: the key went to a dialog or another window and never came back.
    /// </summary>
    public bool IsReplayed(VirtualKey key)
    {
        var now = Environment.TickCount64;
        while (_inFlight.TryPeek(out var first) && now - first.Ticks > 1000)
        {
            _inFlight.Dequeue();
        }

        if (_inFlight.TryPeek(out var next) && next.Key == key)
        {
            _inFlight.Dequeue();
            return true;
        }

        return false;
    }

    /// <summary>A replayed key that still found no home goes back to the front, before the keys typed after it.</summary>
    public void HoldAgain(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        _keys.Insert(0, (key, modifiers));
        _awaitingUp.Add(key);
        Log.Debug("Holding replayed key {Modifiers}+{Key} again", modifiers, key);
    }

    public void Hold(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        if (_keys.Count < MaxHeld)
        {
            _keys.Add((key, modifiers));
            _awaitingUp.Add(key);
            Log.Debug("Holding key {Modifiers}+{Key} until focus settles", modifiers, key);
        }
    }

    /// <summary>True when this is the release of a held key; the caller marks it handled.</summary>
    public bool SwallowRelease(VirtualKey key) => _awaitingUp.Remove(key);

    public void Clear()
    {
        if (_keys.Count > 0)
        {
            Log.Debug("Dropping {Count} held key(s)", _keys.Count);
            _keys.Clear();
        }
    }

    /// <summary>Types all held keys again (SendInput), into whatever has the keyboard now (a dialog's text box).</summary>
    public void Replay() => Replay(_keys.Count);

    /// <summary>
    /// Types only the oldest held key: in a panel each key may start a navigation, and the next one must act on the
    /// listing it leads to, not on the one still on screen.
    /// </summary>
    public void ReplayNext() => Replay(Math.Min(1, _keys.Count));

    private void Replay(int count)
    {
        if (count == 0)
        {
            return;
        }

        var chords = _keys.Take(count).ToList();
        _keys.RemoveRange(0, count);
        var inputs = new List<Input>();
        foreach (var (key, modifiers) in chords)
        {
            // Modifiers the user still holds stay as they are; the others are pressed around this key only.
            var added = new List<ushort>();
            foreach (var (flag, vk) in new[] { (VirtualKeyModifiers.Control, (ushort)0x11), (VirtualKeyModifiers.Shift, (ushort)0x10), (VirtualKeyModifiers.Menu, (ushort)0x12) })
            {
                if (modifiers.HasFlag(flag) && (GetAsyncKeyState(vk) & 0x8000) == 0)
                {
                    added.Add(vk);
                    inputs.Add(KeyInput(vk, up: false));
                }
            }

            inputs.Add(KeyInput((ushort)key, up: false));
            inputs.Add(KeyInput((ushort)key, up: true));
            _inFlight.Enqueue((key, Environment.TickCount64));
            for (var i = added.Count - 1; i >= 0; i--)
            {
                inputs.Add(KeyInput(added[i], up: true));
            }
        }

        Log.Debug("Replaying {Count} held key(s): {Keys}", chords.Count, string.Join(", ", chords.Select(k => k.Modifiers == VirtualKeyModifiers.None ? k.Key.ToString() : $"{k.Modifiers}+{k.Key}")));
        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
        if (sent != array.Length)
        {
            Log.Warning("SendInput sent {Sent} of {Count} key events", sent, array.Length);
        }
    }

    private static Input KeyInput(ushort vk, bool up)
    {
        var flags = up ? KeyEventFUp : 0u;
        if (IsExtended(vk))
        {
            flags |= KeyEventFExtended;
        }

        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion { Keyboard = new KeybdInput { Vk = vk, Scan = (ushort)MapVirtualKey(vk, 0), Flags = flags } },
        };
    }

    /// <summary>Keys from the navigation block (and numpad divide, right-hand modifiers) carry the extended-key flag.</summary>
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 // PageUp, PageDown, End, Home, arrows
        or 0x2D or 0x2E // Insert, Delete
        or 0x6F // numpad divide
        or 0x5B or 0x5C or 0x5D // Windows keys, Application (menu) key
        or 0x90 // NumLock
        or 0xA3 or 0xA5; // right Ctrl, right Alt

    private const uint InputKeyboard = 1;
    private const uint KeyEventFExtended = 0x0001;
    private const uint KeyEventFUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>The union is as large as its largest member (MOUSEINPUT), which SendInput checks through cbSize.</summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeybdInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static partial uint MapVirtualKey(uint code, uint mapType);
}
