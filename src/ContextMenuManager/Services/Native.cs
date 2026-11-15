using System.Runtime.InteropServices;
using System.Text;

namespace ContextMenuManager.Services;

internal static partial class Native
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuf, int cch, IntPtr reserved);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackagePathByFullName(string fullName, ref uint len, StringBuilder? path);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWA_CAPTION_COLOR = 35;
    public const int DWMWA_TEXT_COLOR = 36;

    public static string? LoadIndirect(string s)
    {
        var sb = new StringBuilder(1024);
        return SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0 ? sb.ToString() : null;
    }

    public static string? PackagePath(string fullName)
    {
        uint len = 0;
        GetPackagePathByFullName(fullName, ref len, null);
        if (len == 0) return null;
        var sb = new StringBuilder((int)len);
        return GetPackagePathByFullName(fullName, ref len, sb) == 0 ? sb.ToString() : null;
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    public static int ColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
