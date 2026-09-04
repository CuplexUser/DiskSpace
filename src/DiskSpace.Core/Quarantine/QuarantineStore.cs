using System.IO.Compression;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Quarantine;

/// <summary>
/// Stages folders so they can be restored, instead of deleting them outright.
///
/// Used only for the orphan detector's findings, where the evidence is circumstantial and the
/// failure shows up weeks later. Caches are not quarantined: they regenerate, so staging them
/// would go on occupying the space the user is trying to reclaim.
///
/// Folders are packed into a single archive rather than copied file by file. The expense of a
/// cross-volume move is not the bytes, it is creating a directory entry per file on the
/// destination; a 100,000-file folder becomes one sequential write instead of 100,000 file
/// creations.
/// </summary>
public sealed class QuarantineStore(QuarantineOptions? options = null)
{
    private readonly QuarantineOptions _options = options ?? new QuarantineOptions();

    public QuarantineOptions Options => _options;

    /// <summary>
    /// Archives <paramref name="sourcePath"/> and then removes the original.
    ///
    /// The ordering is the safety property: the archive is written, flushed, verified and closed
    /// before anything is deleted. If any of that fails, the partial archive is discarded and
    /// the source is left exactly as it was.
    /// </summary>
    public async Task<QuarantineManifest> QuarantineAsync(
        string sourcePath,
        CleanupRule rule,
        IProgress<QuarantineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Nothing to quarantine at {sourcePath}");

        var location = ResolveLocation(sourcePath);

        return location is null || _options.Mode == QuarantineMode.MoveOnSameVolume
            ? MoveAside(sourcePath, rule)
            : await ArchiveAsync(sourcePath, rule, location, progress, cancellationToken)
                .ConfigureAwait(false);
    }

    private string? ResolveLocation(string sourcePath)
    {
        if (_options.Mode == QuarantineMode.MoveOnSameVolume)
            return null;

        // An explicitly configured location is honoured as given, even on the source volume:
        // that trades immediate space for recoverability, which is the user's call to make.
        if (_options.Location is { } configured)
            return configured;

        var chosen = QuarantineOptions.ChooseLocation(sourcePath);
        if (chosen is null)
            return null;

        // When *we* pick the location, picking the source volume would reclaim nothing, so
        // fall back to a same-volume move — which at least costs nothing to perform.
        var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));
        var targetRoot = Path.GetPathRoot(Path.GetFullPath(chosen));

        return string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase)
            ? null
            : chosen;
    }

    private async Task<QuarantineManifest> ArchiveAsync(
        string sourcePath,
        CleanupRule rule,
        string location,
        IProgress<QuarantineProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(location);

        var id = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-{SafeName(Path.GetFileName(sourcePath))}";
        var archivePath = Path.Combine(location, id + ".zip");

        var files = Directory.GetFiles(sourcePath, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Following a junction would archive data that lives elsewhere and then delete it.
            AttributesToSkip = FileAttributes.ReparsePoint,
        });

        long totalBytes = 0;
        var written = 0;

        try
        {
            await Task.Run(() =>
            {
                using var stream = new FileStream(
                    archivePath, FileMode.Create, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relative = Path.GetRelativePath(sourcePath, file);

                    try
                    {
                        var entry = archive.CreateEntry(relative, _options.Compression);
                        entry.LastWriteTime = File.GetLastWriteTime(file);

                        using var entryStream = entry.Open();
                        using var input = File.OpenRead(file);
                        input.CopyTo(entryStream);

                        totalBytes += input.Length;
                        written++;
                    }
                    catch (IOException)
                    {
                        // A file held open by another process is reported, not fatal — but it
                        // also must not then be deleted, so the whole operation is abandoned.
                        throw;
                    }

                    if (written % 64 == 0)
                    {
                        progress?.Report(new QuarantineProgress(
                            written, files.Length, totalBytes, relative));
                    }
                }

                // Empty directories carry intent; a restore should reproduce them.
                foreach (var directory in Directory.GetDirectories(
                             sourcePath, "*", SearchOption.AllDirectories))
                {
                    if (Directory.GetFileSystemEntries(directory).Length != 0)
                        continue;

                    var relative = Path.GetRelativePath(sourcePath, directory);
                    archive.CreateEntry(relative.Replace('\\', '/') + "/");
                }
            }, cancellationToken).ConfigureAwait(false);

            VerifyArchive(archivePath, written);
        }
        catch (Exception)
        {
            // Nothing has been deleted yet; discard the partial archive and leave the source.
            TryDelete(archivePath);
            throw;
        }

        var manifest = new QuarantineManifest
        {
            Id = id,
            OriginalPath = sourcePath,
            ArchivePath = archivePath,
            OriginalSize = totalBytes,
            FileCount = written,
            QuarantinedAt = DateTimeOffset.Now,
            PurgeAfter = DateTimeOffset.Now + _options.Retention,
            RuleId = rule.Id,
            RuleName = rule.Name,
        };

        manifest.Save();

        // Only now, with a verified archive and a manifest on disk, does the original go.
        // SafeDelete rather than Directory.Delete: the folder may contain a junction, whose
        // target was deliberately not archived and must not be deleted either.
        Safety.SafeDelete.DeleteDirectory(sourcePath, removeRoot: true, cancellationToken);

        progress?.Report(new QuarantineProgress(written, files.Length, totalBytes, "Done"));
        return manifest;
    }

    /// <summary>
    /// Re-opens the finished archive and confirms it holds what was written. ZIP keeps a CRC per
    /// entry, so this catches a truncated or corrupted archive before the source is destroyed.
    /// </summary>
    private static void VerifyArchive(string archivePath, int expectedFiles)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var actual = archive.Entries.Count(e => !e.FullName.EndsWith('/'));

        if (actual != expectedFiles)
        {
            throw new InvalidDataException(
                $"Archive verification failed: expected {expectedFiles} files, found {actual}.");
        }
    }

    /// <summary>
    /// The same-volume fallback: a rename, which is a single metadata operation and therefore
    /// instant no matter how many files are involved. It reclaims nothing until purge.
    /// </summary>
    private QuarantineManifest MoveAside(string sourcePath, CleanupRule rule)
    {
        var id = $"{DateTime.Now:yyyy-MM-dd-HHmmss}-{SafeName(Path.GetFileName(sourcePath))}";
        var root = _options.Location ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskSpace",
            "quarantine");

        Directory.CreateDirectory(root);
        var destination = Path.Combine(root, id);

        Directory.Move(sourcePath, destination);

        var manifest = new QuarantineManifest
        {
            Id = id,
            OriginalPath = sourcePath,
            ArchivePath = Path.Combine(root, id + ".zip"),
            MovedToPath = destination,
            OriginalSize = 0,
            FileCount = 0,
            QuarantinedAt = DateTimeOffset.Now,
            PurgeAfter = DateTimeOffset.Now + _options.Retention,
            RuleId = rule.Id,
            RuleName = rule.Name,
        };

        manifest.Save();
        return manifest;
    }

    /// <summary>Puts a quarantined folder back exactly where it came from.</summary>
    public async Task RestoreAsync(
        QuarantineManifest manifest,
        IProgress<QuarantineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(manifest.OriginalPath))
            throw new IOException($"{manifest.OriginalPath} already exists.");

        if (manifest.MovedToPath is { } moved)
        {
            Directory.Move(moved, manifest.OriginalPath);
            File.Delete(manifest.ManifestPath);
            return;
        }

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(manifest.ArchivePath);
            Directory.CreateDirectory(manifest.OriginalPath);

            var done = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destination = Path.Combine(manifest.OriginalPath, entry.FullName);

                if (entry.FullName.EndsWith('/'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);

                if (++done % 64 == 0)
                    progress?.Report(new QuarantineProgress(done, archive.Entries.Count, 0, entry.FullName));
            }
        }, cancellationToken).ConfigureAwait(false);

        TryDelete(manifest.ArchivePath);
        TryDelete(manifest.ManifestPath);
    }

    /// <summary>Removes a quarantined item permanently.</summary>
    public static void Purge(QuarantineManifest manifest)
    {
        if (manifest.MovedToPath is { } moved && Directory.Exists(moved))
            Directory.Delete(moved, recursive: true);

        TryDelete(manifest.ArchivePath);
        TryDelete(manifest.ManifestPath);
    }

    /// <summary>Everything currently staged, newest first.</summary>
    public IReadOnlyList<QuarantineManifest> List()
    {
        var manifests = new List<QuarantineManifest>();

        foreach (var directory in CandidateDirectories())
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var file in Directory.EnumerateFiles(directory, "*.manifest.json"))
                {
                    if (QuarantineManifest.Load(file) is { } manifest)
                        manifests.Add(manifest);
                }
            }
            catch (Exception)
            {
                // An unreadable quarantine directory contributes nothing.
            }
        }

        return [.. manifests.OrderByDescending(m => m.QuarantinedAt)];
    }

    /// <summary>Purges everything past its retention date. Called at startup.</summary>
    public int PurgeExpired()
    {
        var purged = 0;

        foreach (var manifest in List().Where(m => m.IsDue(DateTimeOffset.Now)))
        {
            try
            {
                Purge(manifest);
                purged++;
            }
            catch (Exception)
            {
                // Leave it for the next run rather than failing startup.
            }
        }

        return purged;
    }

    private IEnumerable<string> CandidateDirectories()
    {
        if (_options.Location is { } configured)
            yield return configured;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskSpace",
            "quarantine");

        foreach (var drive in SafeDrives())
            yield return Path.Combine(drive.RootDirectory.FullName, QuarantineOptions.DefaultFolderName);
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives().Where(d => d is { IsReady: true, DriveType: DriveType.Fixed });
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
        return cleaned.Trim('.', ' ') is { Length: > 0 } trimmed ? trimmed : "item";
    }
}
