using System.Runtime.InteropServices;
using System.Security.Principal;

namespace DiskSpace.App.Platform;

/// <summary>
/// The small amount of Win32 needed to make a WinForms window look like it belongs on a
/// modern Windows desktop: a dark title bar, dark scrollbars on native containers, and
/// rounded corners.
/// </summary>
internal static partial class NativeMethods
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int CornerPreferenceRound = 2;

    /// <summary>Sent when the user changes the system theme, among many other settings.</summary>
    internal const int WmSettingChange = 0x001A;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("uxtheme.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(
        IntPtr hwnd, string subAppName, string? subIdList);

    /// <summary>
    /// Applies the immersive dark title bar. Fails harmlessly on builds that predate the
    /// attribute, which is why the return code is ignored.
    /// </summary>
    internal static void ApplyTitleBarTheme(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero)
            return;

        var value = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));

        var corner = CornerPreferenceRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    /// <summary>Sets the window border colour, as a COLORREF (0x00BBGGRR).</summary>
    internal static void ApplyBorderColor(IntPtr handle, Color color)
    {
        if (handle == IntPtr.Zero)
            return;

        var colorRef = color.R | (color.G << 8) | (color.B << 16);
        DwmSetWindowAttribute(handle, DwmwaBorderColor, ref colorRef, sizeof(int));
    }

    /// <summary>
    /// Switches a native control to the dark Explorer theme. Without this, a ListView or
    /// TreeView keeps painting bright white scrollbars inside an otherwise dark window.
    /// </summary>
    internal static void ApplyExplorerTheme(IntPtr handle, bool dark)
    {
        if (handle == IntPtr.Zero)
            return;

        SetWindowTheme(handle, dark ? "DarkMode_Explorer" : "Explorer", null);
    }

    /// <summary>True when the process holds an elevated token.</summary>
    internal static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
