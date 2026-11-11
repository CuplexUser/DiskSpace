using DiskSpace.Core.Safety;

namespace DiskSpace.Core.Tests;

/// <summary>
/// What the Files page may offer to send to the Recycle Bin. The move itself is not exercised
/// here: it would land test files in the Recycle Bin of whoever runs the tests.
/// </summary>
public sealed class RecycleBinTests
{
    [Fact]
    public void An_ordinary_file_may_be_recycled()
    {
        using var fixture = new ScanFixture();

        Assert.Null(RecycleBin.Refusal(fixture.File("video.mkv", 1000)));
    }

    [Fact]
    public void A_file_that_belongs_to_Windows_is_refused()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

        Assert.NotNull(RecycleBin.Refusal(notepad));
    }

    [Fact]
    public void A_file_that_belongs_to_an_installed_program_is_refused()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.NotNull(RecycleBin.Refusal(Path.Combine(programFiles, "Some Product", "data.pak")));
    }

    [Fact]
    public void A_file_marked_as_a_system_file_is_refused()
    {
        using var fixture = new ScanFixture();
        var file = fixture.File("swap.sys", 1000);
        File.SetAttributes(file, FileAttributes.System);

        try
        {
            Assert.NotNull(RecycleBin.Refusal(file));
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void A_folder_or_a_missing_file_is_refused()
    {
        using var fixture = new ScanFixture();

        Assert.NotNull(RecycleBin.Refusal(fixture.Dir("folder")));
        Assert.NotNull(RecycleBin.Refusal(Path.Combine(fixture.Root, "gone.bin")));
    }

    [Fact]
    public void Refused_files_are_never_sent()
    {
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

        Assert.Empty(RecycleBin.Send([notepad]));
        Assert.True(File.Exists(notepad));
    }
}
