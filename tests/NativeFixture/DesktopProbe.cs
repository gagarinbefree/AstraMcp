using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

// Reports the actual child thread desktop independently of the adapter's launch metadata.
internal static class DesktopProbe
{
    internal static void Report()
    {
        var name = Name(GetThreadDesktop(GetCurrentThreadId()));
        Console.WriteLine("Desktop: " + name);
        // A window is created only after isolation succeeds, never on the user's desktop.
        if (!name.StartsWith("AstraMcp-", StringComparison.Ordinal))
        {
            Console.WriteLine("Window created: no");
            return;
        }
        var window = CreateWindowExW(0, "STATIC", "Astra MCP isolation test", 0x90000000,
            0, 0, 100, 100, 0, 0, 0, 0);
        if (window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            Console.WriteLine("Window created: yes");
            Console.WriteLine("Window visible on child desktop: " + IsWindowVisible(window));
        }
        finally { DestroyWindow(window); }
    }

    private static string Name(nint desktop)
    {
        var buffer = new StringBuilder(256);
        if (!GetUserObjectInformationW(desktop, 2, buffer, buffer.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return buffer.ToString();
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder buffer, int length, out int needed);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
