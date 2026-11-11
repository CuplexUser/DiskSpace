using System.Buffers;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DiskSpace.Core.Scanning;

/// <summary>One file, as the survey saw it.</summary>
public sealed record FileEntry(string Path, long Size, DateTime LastWriteUtc);

/// <summary>Files with identical contents, each one a separate copy on disk.</summary>
public sealed record DuplicateGroup(long Size, IReadOnlyList<FileEntry> Files)
{
    /// <summary>What removing every copy but one would free.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>
/// Where a survey has got to. <paramref name="Total"/> is zero while the amount of work is
/// still unknown, which is the whole of the listing stage.
/// </summary>
public readonly record struct SurveyProgress(string Stage, long Done, long Total, string Current);

/// <summary>
/// Looks at individual files, which the size tree never does: it adds files up per folder and
/// keeps no record of any one of them. Two questions are answered here, the largest files under
/// a folder and the files that exist more than once.
///
/// Both are read-only. Nothing in this class deletes or modifies a file.
/// </summary>
public static class FileSurvey
{
    /// <summary>Bytes hashed from each end of a file before committing to reading all of it.</summary>
    private const int SampleSize = 64 * 1024;

    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// A cloud file that is not on this disk. It reports its full size and occupies none of
    /// it, and opening it would start a download.
    /// </summary>
    private const FileAttributes NotLocal =
        FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private static readonly EnumerationOptions ListingOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly record struct RawEntry(
        string Name, long Length, bool IsDirectory, FileAttributes Attributes, DateTime LastWriteUtc);

    /// <summary>
    /// Every file of at least <paramref name="minimumSize"/> bytes under
    /// <paramref name="root"/>, largest first.
    /// </summary>
    public static Task<IReadOnlyList<FileEntry>> FindLargeFilesAsync(
        string root,
        long minimumSize,
        IProgress<SurveyProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<FileEntry>>(
            () =>
            {
                var files = Walk(root, minimumSize, skipWindows: false, progress, cancellationToken).ToList();
                files.Sort((a, b) => b.Size.CompareTo(a.Size));
                return files;
            },
            cancellationToken);

    /// <summary>
    /// Groups of files under <paramref name="root"/> whose contents are byte-for-byte the same,
    /// most wasted space first.
    ///
    /// Candidates are narrowed in three steps, cheapest first: same size, then the same first
    /// and last 64 KB, then the same SHA-256 of the whole file. Only the last step reads every
    /// byte, and only files that survived the first two reach it. Hard links to one file are
    /// counted once, because a second name for the same data wastes nothing.
    /// </summary>
    public static Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        string root,
        long minimumSize,
        IProgress<SurveyProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DuplicateGroup>>(
            () => FindDuplicates(root, Math.Max(1, minimumSize), progress, cancellationToken),
            cancellationToken);

    private static List<DuplicateGroup> FindDuplicates(
        string root, long minimumSize, IProgress<SurveyProgress>? progress, CancellationToken cancellationToken)
    {
        // The Windows directory is left out: the component store is hard links and
        // deliberate copies from end to end, and none of it is the user's to tidy.
        var sameSize = Walk(root, minimumSize, skipWindows: true, progress, cancellationToken)
            .GroupBy(file => file.Size)
            .Where(group => group.Skip(1).Any())
            .ToList();

        var throttle = new Throttle(progress);

        // Step two: sample both ends, and note which file each path really is.
        var sampled = new List<List<FileEntry>>();
        long examined = 0;
        long toExamine = sameSize.Sum(group => (long)group.Count());

        foreach (var group in sameSize)
        {
            var bySample = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
            var seen = new HashSet<(uint Volume, ulong Index)>();

            foreach (var file in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throttle.Report("Comparing candidates", ++examined, toExamine, file.Path);

                if (Sample(file) is not { } sample)
                    continue;

                // A second path to a file already seen is a hard link, not a copy.
                if (sample.Identity is { } identity && !seen.Add(identity))
                    continue;

                if (!bySample.TryGetValue(sample.Hash, out var list))
                    bySample[sample.Hash] = list = [];

                list.Add(file);
            }

            sampled.AddRange(bySample.Values.Where(list => list.Count > 1));
        }

        // Step three: the whole file, for whatever is left.
        var groups = new List<DuplicateGroup>();
        long hashed = 0;
        long toHash = sampled.Sum(list => list.Sum(file => file.Size));

        foreach (var candidates in sampled)
        {
            var byContent = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);

            foreach (var file in candidates)
            {
                var done = hashed;

                var hash = HashWhole(
                    file,
                    bytes => throttle.Report("Reading files", done + bytes, toHash, file.Path),
                    cancellationToken);

                hashed += file.Size;

                if (hash is null)
                    continue;

                if (!byContent.TryGetValue(hash, out var list))
                    byContent[hash] = list = [];

                list.Add(file);
            }

            foreach (var copies in byContent.Values.Where(list => list.Count > 1))
            {
                // Oldest first, so the first row of a group is the likeliest original.
                copies.Sort((a, b) =>
                {
                    var byAge = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
                    return byAge != 0 ? byAge : string.CompareOrdinal(a.Path, b.Path);
                });

                groups.Add(new DuplicateGroup(copies[0].Size, copies));
            }
        }

        groups.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
        return groups;
    }

    /// <summary>
    /// Lists files depth-first without following junctions. Unreadable folders are skipped: a
    /// survey that reports fewer files than exist is still correct about the ones it reports.
    /// </summary>
    private static IEnumerable<FileEntry> Walk(
        string root,
        long minimumSize,
        bool skipWindows,
        IProgress<SurveyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var throttle = new Throttle(progress);
        var pending = new Stack<string>();
        long found = 0;

        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directory = pending.Pop();
            var files = new List<FileEntry>();

            try
            {
                var entries = new FileSystemEnumerable<RawEntry>(
                    directory,
                    static (ref FileSystemEntry entry) => new RawEntry(
                        entry.FileName.ToString(),
                        entry.IsDirectory ? 0L : entry.Length,
                        entry.IsDirectory,
                        entry.Attributes,
                        entry.LastWriteTimeUtc.UtcDateTime),
                    ListingOptions);

                foreach (var entry in entries)
                {
                    var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var path = Path.Combine(directory, entry.Name);

                    if (entry.IsDirectory)
                    {
                        if (isLink || IsSystemHolding(entry.Name))
                            continue;

                        if (skipWindows && string.Equals(path, windows, StringComparison.OrdinalIgnoreCase))
                            continue;

                        pending.Push(path);
                    }
                    else if (!isLink && (entry.Attributes & NotLocal) == 0 && entry.Length >= minimumSize)
                    {
                        files.Add(new FileEntry(path, entry.Length, entry.LastWriteUtc));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Listed as far as it would go.
            }

            found += files.Count;
            throttle.Report("Listing files", found, 0, directory);

            foreach (var file in files)
                yield return file;
        }
    }

    /// <summary>Folders Windows keeps for itself on every volume, which hold nobody's documents.</summary>
    private static bool IsSystemHolding(string name) =>
        name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
        || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase);

    private readonly record struct FileSample(string Hash, (uint Volume, ulong Index)? Identity);

    private static FileSample? Sample(FileEntry file)
    {
        try
        {
            using var handle = Open(file.Path);
            var buffer = ArrayPool<byte>.Shared.Rent(SampleSize);

            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var head = RandomAccess.Read(handle, buffer.AsSpan(0, SampleSize), 0);
                hash.AppendData(buffer, 0, head);

                if (file.Size > SampleSize)
                {
                    var tail = RandomAccess.Read(handle, buffer.AsSpan(0, SampleSize), file.Size - SampleSize);
                    hash.AppendData(buffer, 0, tail);
                }

                (uint, ulong)? identity = GetFileInformationByHandle(handle, out var information)
                    ? (information.VolumeSerialNumber,
                        ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow)
                    : null;

                return new FileSample(Convert.ToHexString(hash.GetHashAndReset()), identity);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // In use or unreadable: it cannot be compared, so it is not reported.
        }
    }

    private static string? HashWhole(FileEntry file, Action<long> onProgress, CancellationToken cancellationToken)
    {
        try
        {
            using var handle = Open(file.Path);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long offset = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = RandomAccess.Read(handle, buffer, offset);
                    if (read == 0)
                        break;

                    hash.AppendData(buffer, 0, read);
                    offset += read;
                    onProgress(offset);
                }

                // A file that changed size while it was read is not the file that was listed.
                return offset == file.Size ? Convert.ToHexString(hash.GetHashAndReset()) : null;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static SafeFileHandle Open(string path) =>
        File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            FileOptions.SequentialScan);

    /// <summary>Progress at most ten times a second, however fast the work goes.</summary>
    private sealed class Throttle(IProgress<SurveyProgress>? progress)
    {
        private long _last;

        public void Report(string stage, long done, long total, string current)
        {
            if (progress is null)
                return;

            var now = Stopwatch.GetTimestamp();
            if (_last != 0 && Stopwatch.GetElapsedTime(_last, now) < TimeSpan.FromMilliseconds(100))
                return;

            _last = now;
            progress.Report(new SurveyProgress(stage, done, total, current));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file, out ByHandleFileInformation information);
}
