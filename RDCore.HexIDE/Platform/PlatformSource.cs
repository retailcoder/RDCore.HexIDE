using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>
/// The platform root RDCore runs from: the development override when one is set, the installed platform otherwise, and,
/// when there is none, the newest release, downloaded only once the user agrees.
/// </summary>
internal sealed class PlatformSource(AddinSettings settings, PlatformInstaller.PlatformInstaller installer, IUserPrompts prompts)
{
    /// <summary>
    /// The root to run, or null when there is none to run: the user declined the download, or there is nothing to download, or
    /// it failed. The user has been told why in every case but their own refusal.
    /// </summary>
    public async Task<string?> GetRootAsync(CancellationToken cancellationToken)
    {
        if (settings.PlatformRoot is { Length: > 0 } development)
        {
            return development;
        }

        if (installer.Current is { } installed)
        {
            return installed.Root;
        }

        PlatformRelease? release;
        try
        {
            release = await installer.Feed.FindLatestAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            await prompts.TellAsync(Strings.ReleasesUnreachable(ex.Message));
            return null;
        }
        if (release is null)
        {
            await prompts.TellAsync(Strings.NoRelease);
            return null;
        }
        if (!await prompts.ConfirmDownloadAsync(release, PlatformInstallerOptions.DefaultRoot))
        {
            return null;
        }
        // The project may have closed while the question was open: then nothing is downloaded for it.
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var view = prompts.ShowInstall(release);
            return (await installer.InstallAsync(release, view, cancellationToken)).Root;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await prompts.TellAsync(Strings.InstallFailed(ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Makes sure the platform at <paramref name="root"/> is still there before its server starts, and deploys the release
    /// again when it has gone: someone deleted it while the project was open.
    /// </summary>
    public async Task EnsureStillThereAsync(string root, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(root, "rdcore.json")) || settings.PlatformRoot is { Length: > 0 })
        {
            return;
        }

        if (await installer.Feed.FindLatestAsync(cancellationToken) is { } release)
        {
            await installer.InstallAsync(release, null, cancellationToken);
        }
    }
}
