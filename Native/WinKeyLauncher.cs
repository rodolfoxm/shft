using System.Runtime.InteropServices;
using System.Threading;
using Shft.Core;

namespace Shft.Native;

/// <summary>Tells a Windows key tapped alone from one used in a shortcut. Pure, so it can be tested.</summary>
internal sealed class WinTap
{
    public const int LeftWin = 0x5B, RightWin = 0x5C;

    bool _held, _combined;

    /// <summary>Feeds one key event; true when it is the release of a Windows key pressed with nothing else.</summary>
    public bool Key(int vk, bool down)
    {
        if (vk is LeftWin or RightWin)
        {
            if (down)
            {
                if (!_held) _combined = false; // auto-repeat must not forget a shortcut already in progress
                _held = true;
                return false;
            }
            bool tap = _held && !_combined;
            _held = false;
            return tap;
        }
        if (_held && down) _combined = true;
        return false;
    }
}

/// <summary>
/// Opens Applications when the Windows key is tapped alone. Shortcuts such as Win+E pass through untouched.
/// The hook has its own thread because Windows drops a keyboard hook that answers late: a busy UI must never
/// slow down typing or games.
/// </summary>
public sealed class WinKeyLauncher : IDisposable
{
    const int WH_KEYBOARD_LL = 13, HC_ACTION = 0;
    const uint WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    const uint LLKHF_INJECTED = 0x10, KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2;
    const byte MaskKey = 0xE8; // an unassigned key: seen between Win down and up, it stops Start from opening

    readonly WinTap _tap = new();
    readonly Action _tapped;
    HookProc? _proc;
    uint _threadId;

    public WinKeyLauncher(Action tapped) => _tapped = tapped;

    public void Start()
    {
        if (_proc != null) return;
        _proc = OnKey;
        new Thread(Run) { IsBackground = true, Name = "windows key" }.Start();
    }

    public void Stop()
    {
        if (_proc == null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _proc = null;
        _threadId = 0;
    }

    void Run()
    {
        var proc = _proc;
        _threadId = GetCurrentThreadId();
        var hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc!, GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero)
        {
            Log.Write("windows key: hook not installed " + Marshal.GetLastWin32Error());
            return;
        }
        Log.Write("windows key: listening");
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(hook);
        GC.KeepAlive(proc);
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code != HC_ACTION) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        bool injected = (key.flags & LLKHF_INJECTED) != 0;
        bool isWin = key.vkCode is WinTap.LeftWin or WinTap.RightWin;
        // Our own events, and Win sent by tools such as a Stream Deck, are not a person tapping the key.
        if (key.vkCode == MaskKey || (injected && isWin)) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        uint message = (uint)wParam.ToInt64();
        if (!_tap.Key((int)key.vkCode, message is WM_KEYDOWN or WM_SYSKEYDOWN))
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        // Hold back the release, so the mask key lands before it, then release Win for real: it never stays stuck.
        keybd_event(MaskKey, 0, 0, UIntPtr.Zero);
        keybd_event(MaskKey, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event((byte)key.vkCode, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        _tapped();
        return new IntPtr(1);
    }

    public void Dispose() => Stop();

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")] static extern IntPtr GetModuleHandle(string? name);
}
