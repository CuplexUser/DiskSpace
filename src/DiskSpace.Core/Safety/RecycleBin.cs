using System.Runtime.InteropServices;

namespace DiskSpace.Core.Safety;

/// <summary>
/// Sends files a person picked by hand to the Recycle Bin.
///
/// Everything else in this tool deletes outright or quarantines, because it only ever removes
/// things a rule has vouched for. The large-file and duplicate lists vouch for nothing: they
/// show the person's own files, and the person decides. So the removal is the one Windows
/// already offers for that, and it can be undone from the Recycle Bin.
/// </summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;

    // Asks before a file too big for the Recycle Bin is deleted outright. Without it, the
    // no-confirmation flag lets the shell destroy such a file without a word.
    private const ushort FofWantNukeWarning = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool AnyOperationsAborted;

        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct operation);

    /// <summary>
    /// Why <paramref name="path"/> must not be offered for removal from a file list, or null
    /// when it may be. A big file under Windows or Program Files is large because it has to be.
    /// </summary>
    public static string? Refusal(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return "The path is not valid.";
        }

        if (IsUnder(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            return "It is part of Windows.";

        foreach (var programs in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (IsUnder(full, programs))
                return "It belongs to an installed program. Uninstall the program instead.";
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(full);
        }
        catch (Exception)
        {
            return "It is no longer there.";
        }

        if ((attributes & FileAttributes.Directory) != 0)
            return "It is a folder.";

        // The page file, the hibernation file and their kind are marked System.
        if ((attributes & FileAttributes.System) != 0)
            return "Windows marks it as a system file.";

        return null;
    }

    /// <summary>
    /// Moves <paramref name="paths"/> to the Recycle Bin in one operation, so the shell shows
    /// one progress window and asks at most once about anything too big to recycle. Returns
    /// the paths that are gone afterwards. Paths with a <see cref="Refusal"/> are skipped.
    /// </summary>
    public static IReadOnlyList<string> Send(IReadOnlyList<string> paths, IntPtr owner = default)
    {
        var allowed = paths.Where(p => Refusal(p) is null).Select(Path.GetFullPath).ToList();
        if (allowed.Count == 0)
            return [];

        var operation = new ShFileOpStruct
        {
            Window = owner,
            Function = FoDelete,

            // Double-null terminated: each path ends in a null and the list ends in another.
            From = string.Join('\0', allowed) + "\0\0",
            Flags = (ushort)(FofAllowUndo | FofNoConfirmation | FofWantNukeWarning),
        };

        // The result code is not trusted on its own: a declined warning or one locked file
        // fails the call while the rest went through. What is gone is what counts.
        SHFileOperation(ref operation);

        return [.. allowed.Where(p => !File.Exists(p))];
    }

    private static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrEmpty(folder))
            return false;

        var prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
