using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MindGoblin;

/// <summary>
/// The ONE place that sends input toward the game: a single hover-and-copy, performed
/// only inside the user's own F9 press.
///
/// The policy line, decided deliberately after reading GGG's macro statements: one
/// action per keypress. TradeMacro and Awakened PoE Trade are tolerated because a
/// human press maps to one item interaction; scripted slurps and anything on a timer
/// are not. So this class exposes exactly one composite gesture (move the cursor,
/// press Ctrl+C once), it is only ever called from a registered hotkey handler, and
/// nothing in this codebase may call it in a loop or from a timer. The copy itself is
/// client-side: Ctrl+C on a hovered item sends nothing to the server.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameInput
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_C = 0x43;

    // The union must be EXACT: SendInput validates cbSize and silently rejects the
    // whole batch on any mismatch (hand-padding it wrong is precisely how the first
    // version of this file sent nothing at all). Explicit layout with a real
    // MOUSEINPUT arm reproduces the Win32 sizes on both architectures.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>Move the cursor to a screen position: the hover half of the gesture.</summary>
    public static void HoverAt(int x, int y) => SetCursorPos(x, y);

    /// <summary>One Ctrl+C to the focused window: the copy half. True when Windows
    /// accepted all four key events -- false is a real failure worth reporting.</summary>
    public static bool SendCopy()
    {
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].u.ki.wVk = VK_CONTROL;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].u.ki.wVk = VK_C;
        inputs[2].type = INPUT_KEYBOARD; inputs[2].u.ki.wVk = VK_C;
        inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD; inputs[3].u.ki.wVk = VK_CONTROL;
        inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>())
               == inputs.Length;
    }
}
