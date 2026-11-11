using System.Diagnostics;
using System.Runtime.InteropServices;
using DiskSpace.Core.Rules;
using Microsoft.Win32;

namespace DiskSpace.Core.Cleaning;

/// <summary>
/// Hosts one of the cleanup handlers Windows Disk Cleanup itself runs.
///
/// Every entry in Disk Cleanup's list is a COM object registered under the VolumeCaches key and
/// implementing <c>IEmptyVolumeCache</c>. Loading the same object here means the thing that
/// measures and removes, say, superseded Windows Update components is Windows' own code, which
/// is the only code that knows how. Nothing in this class deletes a file itself.
///
/// The handlers are apartment-threaded and some of them take minutes, so every operation gets
/// a fresh instance on its own STA thread and never touches the UI thread.
/// </summary>
public sealed class DiskCleanupHandler : ISystemCleaner
{
    /// <summary>Where Windows keeps the handler registrations, under HKEY_LOCAL_MACHINE.</summary>
    public const string VolumeCachesPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    private const int Ok = 0;
    private const int Abort = unchecked((int)0x80004004);

    private readonly RegistryKey _hive;
    private readonly string _parentPath;
    private readonly string _keyName;
    private readonly string _volume;

    /// <param name="keyName">The handler's subkey name, e.g. "Update Cleanup".</param>
    /// <param name="volume">The volume root the handler is asked about, e.g. <c>C:\</c>.</param>
    public DiskCleanupHandler(string keyName, string volume)
        : this(Registry.LocalMachine, VolumeCachesPath, keyName, volume)
    {
    }

    /// <summary>
    /// Reads the registration from somewhere other than the real VolumeCaches key. Tests use
    /// this to point a handler at a folder they own.
    /// </summary>
    public DiskCleanupHandler(RegistryKey hive, string parentPath, string keyName, string volume)
    {
        _hive = hive;
        _parentPath = parentPath;
        _keyName = keyName;
        _volume = volume;
    }

    public string Description =>
        $"Windows' own Disk Cleanup handler \"{_keyName}\". Windows measures this and removes "
        + "it; DiskSpace asks it to and deletes nothing itself.";

    /// <summary>
    /// Asks the handler how much it would remove. Null when it has nothing to offer on this
    /// volume, which is also what a handler that needs elevation answers without it.
    /// </summary>
    public async Task<long?> MeasureAsync(CancellationToken cancellationToken = default)
    {
        // WaitAsync hands back a task that already finished even when the token is cancelled,
        // so a handler that answers instantly would otherwise hide the cancellation.
        cancellationToken.ThrowIfCancellationRequested();

        var work = RunOnStaThread(() =>
        {
            using var session = Session.Open(_hive, _parentPath, _keyName, _volume);
            if (session is null)
                return (long?)null;

            var callback = new Callback(cancellationToken, null);
            var result = session.Cache.GetSpaceUsed(out var used, callback);

            if (result == Abort)
                cancellationToken.ThrowIfCancellationRequested();

            return result == Ok ? ClampToLong(used) : null;
        });

        // Not every handler reports progress while it measures, so a cancelled scan does not
        // wait for one: the thread is left to finish and its answer is dropped.
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the handler's cleanup and returns the bytes it freed: the figure the handler
    /// reported while working, or its own measurement from just before when it reported none.
    /// </summary>
    public Task<long> CleanAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<long>(cancellationToken)
            : RunOnStaThread(() =>
        {
            using var session = Session.Open(_hive, _parentPath, _keyName, _volume);
            if (session is null)
                return 0L;

            var callback = new Callback(cancellationToken, progress);

            // Not optional. A handler decides what Purge will remove while it measures; one
            // that is purged without being measured first removes nothing.
            var measured = session.Cache.GetSpaceUsed(out var used, callback);
            if (measured == Abort)
                cancellationToken.ThrowIfCancellationRequested();

            Marshal.ThrowExceptionForHR(measured);

            if (used == 0)
                return 0L;

            // A registration can name a command to run on either side of the purge, and it is
            // the host's job to run them, not the handler's. One handler uses the pair to stop
            // and restart the service that holds its files open, so the second runs even when
            // the purge in between fails.
            var commands = RunsRegisteredCommands ? session.Commands : default;
            int purged;

            RunCommand(commands.Before);
            try
            {
                purged = session.Cache.Purge(used, callback);
            }
            finally
            {
                RunCommand(commands.After);
            }

            if (purged == Abort)
                cancellationToken.ThrowIfCancellationRequested();

            Marshal.ThrowExceptionForHR(purged);

            return callback.Freed > 0 ? ClampToLong(callback.Freed) : ClampToLong(used);
        });

    /// <summary>
    /// Only for registrations under HKEY_LOCAL_MACHINE, which take administrator rights to
    /// write. This process runs elevated, so a command line read from anywhere a standard
    /// user can write would be a way to run anything as administrator.
    /// </summary>
    private bool RunsRegisteredCommands => ReferenceEquals(_hive, Registry.LocalMachine);

    private static void RunCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return;

        var (file, arguments) = SplitCommandLine(Environment.ExpandEnvironmentVariables(commandLine.Trim()));

        // These are written as bare names ("wermgr.exe -purgestores"). Resolved against
        // System32 and nowhere else, so a same-named file in the current directory is never
        // what gets started.
        if (!Path.IsPathFullyQualified(file))
            file = Path.Combine(Environment.SystemDirectory, file);

        if (!File.Exists(file))
            return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            process?.WaitForExit(TimeSpan.FromMinutes(2));
        }
        catch (Exception)
        {
            // A helper that will not start costs this handler some of its reach, not the run.
        }
    }

    private static (string File, string Arguments) SplitCommandLine(string commandLine)
    {
        if (commandLine.StartsWith('"'))
        {
            var close = commandLine.IndexOf('"', 1);
            return close < 0
                ? (commandLine.Trim('"'), string.Empty)
                : (commandLine[1..close], commandLine[(close + 1)..].Trim());
        }

        var space = commandLine.IndexOf(' ');
        return space < 0
            ? (commandLine, string.Empty)
            : (commandLine[..space], commandLine[(space + 1)..].Trim());
    }

    private static long ClampToLong(ulong value) => value > long.MaxValue ? long.MaxValue : (long)value;

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (OperationCanceledException ex)
            {
                completion.SetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Disk Cleanup handler",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task;
    }

    /// <summary>One initialized handler instance, released when disposed.</summary>
    private sealed class Session : IDisposable
    {
        private readonly RegistryKey _key;
        private readonly object _instance;

        private Session(RegistryKey key, object instance, IEmptyVolumeCache cache)
        {
            _key = key;
            _instance = instance;
            Cache = cache;
        }

        public IEmptyVolumeCache Cache { get; }

        /// <summary>The command lines registered to run before and after a purge, if any.</summary>
        public (string? Before, string? After) Commands =>
            (_key.GetValue("PreCleanupString") as string, _key.GetValue("CleanupString") as string);

        /// <summary>
        /// Creates and initializes the handler. Null when it is not registered, cannot be
        /// loaded, or says it has nothing to clean on this volume.
        /// </summary>
        public static Session? Open(RegistryKey hive, string parentPath, string keyName, string volume)
        {
            // Read-only: a handler takes its settings from this key and has no reason to write.
            var key = hive.OpenSubKey($@"{parentPath}\{keyName}");
            if (key is null)
                return null;

            object? instance = null;

            try
            {
                if (key.GetValue(null) is not string clsidText || !Guid.TryParse(clsidText, out var clsid))
                    return Fail(key, instance);

                var type = Type.GetTypeFromCLSID(clsid, throwOnError: false);
                if (type is null)
                    return Fail(key, instance);

                instance = Activator.CreateInstance(type);
                if (instance is not IEmptyVolumeCache cache)
                    return Fail(key, instance);

                var registryHandle = key.Handle.DangerousGetHandle();
                uint flags = 0;
                nint display, description, button = 0;

                // Disk Cleanup prefers the newer entry point when a handler has one, and some
                // handlers only fill in their state there.
                var result = instance is IEmptyVolumeCache2 extended
                    ? extended.InitializeEx(
                        registryHandle, volume, keyName, out display, out description, out button, ref flags)
                    : cache.Initialize(registryHandle, volume, out display, out description, ref flags);

                // The handler allocates these for its caller. The text is not used here, since
                // the catalog names each handler itself.
                Marshal.FreeCoTaskMem(display);
                Marshal.FreeCoTaskMem(description);
                Marshal.FreeCoTaskMem(button);

                // S_FALSE is a handler's way of saying there is nothing for it to do.
                return result == Ok ? new Session(key, instance, cache) : Fail(key, instance);
            }
            catch (Exception)
            {
                return Fail(key, instance);
            }
        }

        private static Session? Fail(RegistryKey key, object? instance)
        {
            Release(instance);
            key.Dispose();
            return null;
        }

        private static void Release(object? instance)
        {
            if (instance is not null && Marshal.IsComObject(instance))
                Marshal.FinalReleaseComObject(instance);
        }

        public void Dispose()
        {
            try
            {
                Cache.Deactivate(out _);
            }
            catch (Exception)
            {
                // The work is already done or abandoned; a failed goodbye changes nothing.
            }

            Release(_instance);
            _key.Dispose();
        }
    }

    /// <summary>What the handler calls back into while it works.</summary>
    private sealed class Callback(CancellationToken cancellationToken, IProgress<long>? progress)
        : IEmptyVolumeCacheCallBack
    {
        public ulong Freed { get; private set; }

        public int ScanProgress(ulong spaceUsed, uint flags, nint status) =>
            cancellationToken.IsCancellationRequested ? Abort : Ok;

        public int PurgeProgress(ulong spaceFreed, ulong spaceToFree, uint flags, nint status)
        {
            // Cumulative, so the last report is the total.
            Freed = spaceFreed;
            progress?.Report(ClampToLong(spaceFreed));

            return cancellationToken.IsCancellationRequested ? Abort : Ok;
        }
    }

    // Declared from emptyvc.h. The method order is the vtable order and must not change.

    [ComImport]
    [Guid("8FCE5227-04DA-11d1-A004-00805F8ABE06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache
    {
        [PreserveSig]
        int Initialize(
            nint registryKey,
            [MarshalAs(UnmanagedType.LPWStr)] string volume,
            out nint displayName,
            out nint description,
            ref uint flags);

        [PreserveSig]
        int GetSpaceUsed(out ulong spaceUsed, IEmptyVolumeCacheCallBack callback);

        [PreserveSig]
        int Purge(ulong spaceToFree, IEmptyVolumeCacheCallBack callback);

        [PreserveSig]
        int ShowProperties(nint window);

        [PreserveSig]
        int Deactivate(out uint flags);
    }

    [ComImport]
    [Guid("02B7E3BA-4DB3-11D2-B2D9-00C04F8EEC8C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCache2
    {
        [PreserveSig]
        int Initialize(
            nint registryKey,
            [MarshalAs(UnmanagedType.LPWStr)] string volume,
            out nint displayName,
            out nint description,
            ref uint flags);

        [PreserveSig]
        int GetSpaceUsed(out ulong spaceUsed, IEmptyVolumeCacheCallBack callback);

        [PreserveSig]
        int Purge(ulong spaceToFree, IEmptyVolumeCacheCallBack callback);

        [PreserveSig]
        int ShowProperties(nint window);

        [PreserveSig]
        int Deactivate(out uint flags);

        [PreserveSig]
        int InitializeEx(
            nint registryKey,
            [MarshalAs(UnmanagedType.LPWStr)] string volume,
            [MarshalAs(UnmanagedType.LPWStr)] string keyName,
            out nint displayName,
            out nint description,
            out nint buttonText,
            ref uint flags);
    }

    [ComImport]
    [Guid("6E793361-73C6-11D0-8469-00AA00442901")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEmptyVolumeCacheCallBack
    {
        [PreserveSig]
        int ScanProgress(ulong spaceUsed, uint flags, nint status);

        [PreserveSig]
        int PurgeProgress(ulong spaceFreed, ulong spaceToFree, uint flags, nint status);
    }
}
