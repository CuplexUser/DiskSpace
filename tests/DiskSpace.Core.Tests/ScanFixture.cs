using System.Diagnostics;

namespace DiskSpace.Core.Tests;

/// <summary>
/// A throwaway directory tree under %TEMP%, deleted on dispose. Every test that touches the
/// file system builds one of these rather than reaching for a real profile directory.
/// </summary>
public sealed class ScanFixture : IDisposable
{
    public ScanFixture()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "DiskSpace.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Dir(string relative)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Creates a file of exactly <paramref name="bytes"/> length.</summary>
    public string File(string relative, int bytes)
    {
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        return full;
    }

    /// <summary>
    /// Creates an NTFS junction, throwing if that fails.
    ///
    /// Junctions need no elevation, so this is expected to work anywhere the tests run. It
    /// throws rather than returning false on purpose: the reparse-point cases are the ones
    /// that matter most, and a test that quietly skipped them would report green while having
    /// verified nothing.
    /// </summary>
    public string CreateJunction(string relativeLink, string targetFullPath)
    {
        var link = Path.Combine(Root, relativeLink);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);

        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{targetFullPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.");

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);

        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"mklink /J failed for {link}: {output.Trim()}");

        return link;
    }

    public void Dispose()
    {
        try
        {
            // Junctions must go first, or the recursive delete follows them out of the fixture.
            RemoveJunctions(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone; nothing to clean up.
        }
        catch (IOException)
        {
            // A stray lock in a test run should not fail the run itself.
        }
    }

    private static void RemoveJunctions(string directory)
    {
        foreach (var sub in Directory.EnumerateDirectories(directory))
        {
            var info = new DirectoryInfo(sub);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
                continue;
            }

            RemoveJunctions(sub);
        }
    }
}
