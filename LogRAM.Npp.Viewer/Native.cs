using System.Runtime.InteropServices;

namespace LogRAM.Npp;

internal static class Native
{
    internal const int ReadyMessage = 0x8001;
    internal const int HideMessage = 0x8002;
    internal const int CommandMessage = 0x8003;
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr child);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] internal static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr window, out RectangleNative rect);
    [StructLayout(LayoutKind.Sequential)] internal struct RectangleNative { public int Left, Top, Right, Bottom; }
}
