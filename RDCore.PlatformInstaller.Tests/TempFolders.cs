using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller.Tests;

/// <summary>
/// A test's own folder under the temp folder, as its real path: the platform checks and starts a path where the system finds it
/// (<see cref="PlatformRootConfinement.RealPath"/>), and on macOS the temp folder is under <c>/var</c>, a link to <c>/private/var</c>.
/// </summary>
public static class TempFolders
{
    /// <summary>
    /// A new folder's path, not created: the real path of the temp folder, then <paramref name="prefix"/> and a new GUID.
    /// </summary>
    /// <param name="prefix">The start of the folder's name, such as <c>rdcore-addin-</c>.</param>
    public static string New(string prefix) =>
        Path.Combine(PlatformRootConfinement.RealPath(Path.GetTempPath()), prefix + Guid.NewGuid().ToString("N"));
}
