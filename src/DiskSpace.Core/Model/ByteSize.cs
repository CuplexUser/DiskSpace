using System.Globalization;

namespace DiskSpace.Core.Model;

/// <summary>Byte formatting shared by every surface, so sizes read identically everywhere.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count for display, scaling the precision so small and large values are
    /// both readable: <c>945 B</c>, <c>12.4 MB</c>, <c>1.06 GB</c>.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 0)
            return "-" + Format(-bytes);
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // One decimal below 10 keeps "9.4 GB" informative; none above keeps "412 MB" tidy.
        var format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    /// <summary>Formats a count with thousands separators.</summary>
    public static string Count(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
