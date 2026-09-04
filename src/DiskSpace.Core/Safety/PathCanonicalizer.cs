using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace.Core.Safety;

/// <summary>
/// Reduces a path to its true, final form: junctions and symlinks followed, 8.3 short names
/// expanded, relative segments removed, canonical casing applied.
///
/// This must run before any allow/deny comparison. A denylist that string-matches
/// <c>C:\Windows</c> is trivially defeated by <c>%TEMP%\..\..\..\Windows</c>, by the short name
/// <c>C:\PROGRA~1</c>, or by a junction pointing out of the directory being cleaned — and all
/// three are ordinary things to find on a real machine, not just attacks.
/// </summary>
public static class PathCanonicalizer
{
    private const uint FileShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint VolumeNameDos = 0x0;
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle handle, char[] buffer, uint bufferLength, uint flags);

    /// <summary>
    /// The canonical form of <paramref name="path"/>. Falls back to the deepest ancestor that
    /// exists — a path being deleted may already be gone, and a path being planned may not
    /// exist yet, but the ancestor chain still decides whether it sits somewhere allowed.
    /// </summary>
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);

        if (TryResolve(full) is { } resolved)
            return resolved;

        // Walk up to the nearest existing ancestor, resolve that, then re-attach the tail.
        var tail = new List<string>();
        var probe = full;

        while (true)
        {
            var parent = Path.GetDirectoryName(probe);
            if (string.IsNullOrEmpty(parent))
                return full;

            tail.Insert(0, Path.GetFileName(probe));

            if (TryResolve(parent) is { } resolvedParent)
                return Path.Combine([resolvedParent, .. tail]);

            probe = parent;
        }
    }

    /// <summary>The final path of an existing item, or null when it cannot be opened.</summary>
    private static string? TryResolve(string path)
    {
        try
        {
            // No access rights are requested, so this succeeds on items the caller may not
            // read; BACKUP_SEMANTICS is what allows a directory handle at all.
            using var handle = CreateFileW(
                path, 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

            if (handle.IsInvalid)
                return null;

            var buffer = new char[1024];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, VolumeNameDos);

            if (length == 0)
                return null;

            if (length > buffer.Length)
            {
                buffer = new char[length];
                length = GetFinalPathNameByHandleW(handle, buffer, length, VolumeNameDos);
                if (length == 0)
                    return null;
            }

            return StripExtendedPrefix(new string(buffer, 0, (int)length));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string StripExtendedPrefix(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.Ordinal))
            return @"\\" + path[ExtendedUncPrefix.Length..];

        return path.StartsWith(ExtendedPrefix, StringComparison.Ordinal)
            ? path[ExtendedPrefix.Length..]
            : path;
    }

    /// <summary>
    /// True when <paramref name="candidate"/> is <paramref name="ancestor"/> or sits inside it.
    /// Compares whole segments, so <c>C:\Users\bobby</c> is not treated as inside
    /// <c>C:\Users\bob</c>.
    /// </summary>
    public static bool IsInside(string candidate, string ancestor)
    {
        var a = Normalize(ancestor);
        var c = Normalize(candidate);

        if (string.Equals(a, c, StringComparison.OrdinalIgnoreCase))
            return true;

        return c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Segment count including the volume, so <c>C:\Users\me\x</c> counts 4.</summary>
    public static int Depth(string path)
    {
        var normalized = Normalize(path);
        var root = Path.GetPathRoot(normalized);

        if (string.IsNullOrEmpty(root))
            return normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

        var remainder = normalized[root.Length..];
        var segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length + 1;
    }

    private static string Normalize(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
