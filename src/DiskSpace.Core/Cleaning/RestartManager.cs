using System.Runtime.InteropServices;

namespace DiskSpace.Core.Cleaning;

/// <summary>
/// Asks Windows which processes hold a file open.
///
/// "The process cannot access the file because it is being used by another process" is a
/// useless thing to show someone. Restart Manager turns that into "held by Code.exe", which is
/// actionable — the user can close it and run again.
/// </summary>
internal static class RestartManager
{
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint sessionHandle, int flags, string sessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint sessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint sessionHandle,
        uint fileCount,
        string[] files,
        uint applicationCount,
        RmUniqueProcess[]? applications,
        uint serviceCount,
        string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint sessionHandle,
        out uint procInfoNeeded,
        ref uint procInfo,
        [In, Out] RmProcessInfo[]? processInfo,
        ref uint rebootReasons);

    /// <summary>
    /// Names the processes holding <paramref name="path"/>, or null when nothing does or the
    /// question cannot be answered. Diagnostic only — never allowed to fail an operation.
    /// </summary>
    public static string? DescribeLockers(string path)
    {
        uint session = 0;

        try
        {
            var key = Guid.NewGuid().ToString("N");
            if (RmStartSession(out session, 0, key) != 0)
                return null;

            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
                return null;

            uint needed = 0;
            uint count = 0;
            uint reasons = 0;

            var result = RmGetList(session, out needed, ref count, null, ref reasons);
            if (result != ErrorMoreData || needed == 0)
                return null;

            count = needed;
            var infos = new RmProcessInfo[count];

            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                return null;

            var names = infos
                .Take((int)count)
                .Select(i => string.IsNullOrWhiteSpace(i.AppName) ? $"PID {i.Process.ProcessId}" : i.AppName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            return names.Count == 0 ? null : string.Join(", ", names);
        }
        catch (Exception)
        {
            // Restart Manager is unavailable or refused; the caller falls back to the raw error.
            return null;
        }
        finally
        {
            if (session != 0)
            {
                try
                {
                    RmEndSession(session);
                }
                catch (Exception)
                {
                    // Nothing useful to do if the session will not close.
                }
            }
        }
    }
}
