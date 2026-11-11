using System.IO.Compression;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Quarantine;

/// <summary>
/// Stages folders, and single files, so they can be restored instead of deleting them outright.
///
/// Used only for findings that rest on inference, such as the orphan detector's, where the
/// evidence is circumstantial and the failure shows up weeks later. Caches are not quarantined: they regenerate, so staging them
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
        if (!Directory.Exists(sourcePath) && !File.Exists(sourcePath))
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

        // A single file is archived under its own name, as an archive of one.
        var isFile = File.Exists(sourcePath);

        var files = isFile
            ? [sourcePath]
            : Directory.GetFiles(sourcePath, "*", new EnumerationOptions
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

                    var relative = isFile ? Path.GetFileName(file) : Path.GetRelativePath(sourcePath, file);

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
                foreach (var directory in isFile
                             ? []
                             : Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
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
            IsFile = isFile,
        };

        manifest.Save();

        // Only now, with a verified archive and a manifest on disk, does the original go.
        if (isFile)
        {
            Safety.SafeDelete.DeleteFile(sourcePath);

            if (File.Exists(sourcePath))
            {
                // Still there, so nothing was reclaimed and there is nothing to restore. An
                // archive beside a file that never left would only block a later attempt.
                TryDelete(archivePath);
                TryDelete(manifest.ManifestPath);
                throw new IOException($"{sourcePath} is in use and could not be removed.");
            }
        }
        else
        {
            // SafeDelete rather than Directory.Delete: the folder may contain a junction, whose
            // target was deliberately not archived and must not be deleted either.
            Safety.SafeDelete.DeleteDirectory(sourcePath, removeRoot: true, cancellationToken);
        }

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
        var isFile = File.Exists(sourcePath);

        if (isFile)
            File.Move(sourcePath, destination);
        else
            Directory.Move(sourcePath, destination);

        var (bytes, files) = isFile ? (new FileInfo(destination).Length, 1L) : Measure(destination);

        var manifest = new QuarantineManifest
        {
            Id = id,
            OriginalPath = sourcePath,
            ArchivePath = Path.Combine(root, id + ".zip"),
            MovedToPath = destination,
            OriginalSize = bytes,
            FileCount = files,
            QuarantinedAt = DateTimeOffset.Now,
            PurgeAfter = DateTimeOffset.Now + _options.Retention,
            RuleId = rule.Id,
            RuleName = rule.Name,
            IsFile = isFile,
        };

        manifest.Save();
        return manifest;
    }

    /// <summary>
    /// What a moved-aside folder holds, measured after the move rather than before it.
    ///
    /// A rename reports nothing about what it moved, and a manifest claiming zero bytes makes
    /// the quarantine page list the item as 0 B and offer to free nothing by purging it. This
    /// is a metadata-only walk, so it costs a fraction of the copy the move avoided.
    /// </summary>
    private static (long Bytes, long Files) Measure(string path)
    {
        long bytes = 0;
        long files = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // A junction's target was never moved here, so its bytes are not ours to claim.
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", options))
            {
                bytes += file.Length;
                files++;
            }
        }
        catch (Exception)
        {
            // The move already succeeded; an unmeasurable folder is reported as empty rather
            // than turned into a failure.
        }

        return (bytes, files);
    }

    /// <summary>Puts a quarantined folder back exactly where it came from.</summary>
    public async Task RestoreAsync(
        QuarantineManifest manifest,
        IProgress<QuarantineProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(manifest.OriginalPath) || File.Exists(manifest.OriginalPath))
            throw new IOException($"{manifest.OriginalPath} already exists.");

        if (manifest.MovedToPath is { } moved)
        {
            if (manifest.IsFile)
                File.Move(moved, manifest.OriginalPath);
            else
                Directory.Move(moved, manifest.OriginalPath);

            File.Delete(manifest.ManifestPath);
            return;
        }

        if (manifest.IsFile)
        {
            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(manifest.ArchivePath);
                Directory.CreateDirectory(Path.GetDirectoryName(manifest.OriginalPath)!);
                archive.Entries.Single().ExtractToFile(manifest.OriginalPath, overwrite: false);
            }, cancellationToken).ConfigureAwait(false);

            TryDelete(manifest.ArchivePath);
            TryDelete(manifest.ManifestPath);
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
        if (manifest.IsFile && manifest.MovedToPath is { } movedFile && File.Exists(movedFile))
        {
            Safety.SafeDelete.DeleteFile(movedFile);

            if (File.Exists(movedFile))
                throw new IOException($"{movedFile} could not be removed; it is still in use.");
        }

        if (manifest.MovedToPath is { } moved && Directory.Exists(moved))
        {
            // Not Directory.Delete: a moved-aside folder keeps the attributes it arrived with,
            // and that call throws on the first read-only file it meets. Application data is
            // full of them — git marks every pack file read-only — so the item would fail to
            // purge here and at every startup afterwards, with no way to get rid of it.
            Safety.SafeDelete.DeleteDirectory(moved, removeRoot: true);

            // SafeDelete skips what it cannot remove rather than throwing, so the manifest is
            // only dropped once the folder is actually gone. A half-emptied folder that still
            // has a manifest can at least be looked at and retried.
            if (Directory.Exists(moved))
            {
                throw new IOException(
                    $"{moved} could not be fully removed; some of it is still in use.");
            }
        }

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
        {
            yield return configured;

            if (!_options.SearchAllLocations)
                yield break;
        }

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
