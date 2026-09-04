using DiskSpace.App.Theme;

namespace DiskSpace.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppTheme.Refresh();
        Application.Run(new MainForm());
    }
}
