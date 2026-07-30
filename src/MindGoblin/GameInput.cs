using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MindGoblin;

/// <summary>
/// The ONE place that sends input toward the game: a single hover-and-copy, performed
/// only inside the user's own F9 press.
///
/// The policy line, decided deliberately after reading GGG's macro statements: one
/// action per keypress. TradeMacro and Awakened PoE Trade are tolerated because a
/// human press maps to one item interaction; scripted sweeps and anything on a timer
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

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
        // KEYBDINPUT is the only arm used, but the Win32 union must be sized for
        // MOUSEINPUT or SendInput rejects the struct on 64-bit.
        private readonly long _pad1;
        private readonly long _pad2;
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

    /// <summary>One Ctrl+C to the focused window: the copy half.</summary>
    public static void SendCopy()
    {
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = VK_CONTROL;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = VK_C;
        inputs[2].type = INPUT_KEYBOARD; inputs[2].ki.wVk = VK_C;
        inputs[2].ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD; inputs[3].ki.wVk = VK_CONTROL;
        inputs[3].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
