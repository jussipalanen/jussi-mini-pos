using System;
using System.Reflection;

namespace JussiMiniPos.Services;

/// <summary>
/// What the application knows about itself. Read once from the assembly, so
/// the version on screen can only ever be the version that was built.
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// The version as written in the project file, for example
    /// <c>1.0.0-beta.2</c>.
    /// </summary>
    /// <remarks>
    /// Comes from the informational version, because that is the only one that
    /// keeps the "-beta.2" — AssemblyVersion has to be numeric and would read
    /// as a flat 1.0.0.0. The SDK appends "+&lt;commit&gt;" to it when the
    /// build knows its source revision, which is useful in a crash report and
    /// noise on a start screen, so everything from the plus sign on is cut.
    /// </remarks>
    public static string Version { get; } = ReadVersion();

    /// <summary>The version as shown to the user, for example <c>v1.0.0-beta.2</c>.</summary>
    public static string DisplayVersion { get; } = $"v{Version}";

    private static string ReadVersion()
    {
        // The assembly this type lives in, rather than the entry assembly:
        // what is wanted is the version of the application, not of whatever
        // process happens to have loaded it.
        var assembly = typeof(AppInfo).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // Nothing sets this in practice, but a version of "" on screen
            // would look like a bug rather than a missing attribute.
            return assembly.GetName().Version?.ToString(3) ?? "?";
        }

        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
