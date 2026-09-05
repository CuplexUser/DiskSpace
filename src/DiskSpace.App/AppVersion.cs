using System.Reflection;

namespace DiskSpace.App;

/// <summary>
/// The version shown in the title bar and compared against GitHub releases. Read from
/// <c>AssemblyInformationalVersionAttribute</c>, which Directory.Build.props stamps as
/// "$(Version)+$(SourceRevisionId)"; the commit suffix is dropped, since it identifies a
/// build rather than a release.
/// </summary>
internal static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informational))
            return "0.0.0";

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
