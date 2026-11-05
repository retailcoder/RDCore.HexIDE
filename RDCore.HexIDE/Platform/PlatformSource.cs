using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>The platform a server runs from: its root, and the installed version when it is one, not a development root.</summary>
internal sealed record PlatformChoice(string Root, InstalledPlatform? Installed);

/// <summary>
/// The platform RDCore runs from: the development override when one is set, the installed platform otherwise, and, when
/// there is none, the newest release, downloaded only once the user agrees.
/// </summary>
/// <remarks>
/// <para>
/// <b>One question at a time.</b> RDCore projects opened while the user is asked share the one question and its answer.
/// "Not now" holds until HexIDE restarts: no RDCore project opened after it asks again.
/// </para>
/// <para>
/// <b>Off the UI thread.</b> The installation runs on the thread pool; only its progress and its outcome reach the UI.
/// </para>
/// <para>
/// <b>Never silent.</b> Whatever stops RDCore from having a platform, the user is told why, in their language, unless it was
/// their own "Not now".
/// </para>
/// </remarks>
internal sealed class PlatformSource(AddinSettings settings, PlatformInstaller.PlatformInstaller installer, IUserPrompts prompts)
{
    private readonly object gate = new();
    private Task<bool>? asking;
    private bool declined;

    /// <summary>
    /// The platform to run, or null when there is none to run: the user declined the download, or there is nothing to download,
    /// or it failed, which the user has been told.
    /// </summary>
    public async Task<PlatformChoice?> GetAsync(CancellationToken cancellationToken)
    {
        if (settings.PlatformRoot is { Length: > 0 } development)
        {
            return new PlatformChoice(development, null);
        }

        if (installer.Current is { } installed)
        {
            return new PlatformChoice(installed.Root, installed);
        }

        lock (gate)
        {
            if (declined)
            {
                return null;
            }
        }

        try
        {
            var release = await installer.Feed.FindLatestAsync(cancellationToken);
            if (release is null)
            {
                await prompts.TellAsync(Strings.NoRelease);
                return null;
            }

            if (!await AskAsync(release))
            {
                return null;
            }

            // The project may have closed while the question was open: then nothing is downloaded for it.
            cancellationToken.ThrowIfCancellationRequested();
            var deployed = await InstallAsync(release, cancellationToken);
            return new PlatformChoice(deployed.Root, deployed);
        }
        catch (Exception ex) when (Reason(ex, cancellationToken) is { } reason)
        {
            await prompts.TellAsync(reason);
            return null;
        }
    }

    /// <summary>
    /// Makes sure the platform <paramref name="choice"/> names is still there before its server starts: its manifest, and
    /// <paramref name="languageServer"/>, the executable the server's launch names. When either has gone, the same version is
    /// deployed again, as the server's launch names it, once the user agrees.
    /// </summary>
    /// <exception cref="InvalidOperationException">It is gone and can't be had again; the message says why, for HexIDE to show.</exception>
    public async Task EnsureStillThereAsync(PlatformChoice choice, string languageServer, CancellationToken cancellationToken)
    {
        if (IsWhole(choice, languageServer))
        {
            return;
        }

        if (choice.Installed is not { } installed)
        {
            throw new InvalidOperationException(Strings.PlatformRootUnusable(choice.Root,
                HasManifest(choice) ? Strings.LanguageServerMissing : Strings.NoManifest));
        }

        PlatformRelease? release;
        var agreed = false;
        try
        {
            release = await installer.Feed.FindAsync(installed.Version, cancellationToken);
            if (release is not null && (agreed = await AskAsync(release)))
            {
                await InstallAsync(release, cancellationToken);
            }
        }
        catch (Exception ex) when (Reason(ex, cancellationToken) is { } reason)
        {
            throw new InvalidOperationException(reason, ex);
        }

        if (release is null)
        {
            throw new InvalidOperationException(Strings.VersionGone(installed.Version.Text));
        }

        if (!agreed)
        {
            throw new InvalidOperationException(Strings.PlatformGone(installed.Version.Text));
        }
    }

    /// <summary>Whether the platform <paramref name="choice"/> names still has its manifest and <paramref name="languageServer"/>.</summary>
    public static bool IsWhole(PlatformChoice choice, string languageServer) => HasManifest(choice) && File.Exists(languageServer);

    private static bool HasManifest(PlatformChoice choice) => File.Exists(Path.Combine(choice.Root, "rdcore.json"));

    // Asked once for every project that wants it at the same time: a question still open is shared, and one answered is never
    // answered again, so a later question is asked afresh.
    private Task<bool> AskAsync(PlatformRelease release)
    {
        lock (gate)
        {
            if (asking is { IsCompleted: false } open)
            {
                return open;
            }

            return asking = Ask(release);
        }

        async Task<bool> Ask(PlatformRelease asked)
        {
            var agreed = await prompts.ConfirmDownloadAsync(asked, PlatformInstallerOptions.DefaultRoot);
            lock (gate)
            {
                declined = !agreed;
            }
            return agreed;
        }
    }

    private async Task<InstalledPlatform> InstallAsync(PlatformRelease release, CancellationToken cancellationToken)
    {
        using var view = prompts.ShowInstall(release);
        return await Task.Run(() => installer.InstallAsync(release, view, cancellationToken), cancellationToken);
    }

    // What to tell the user of a failure, in their language; null only for the cancellation of a project that closed.
    private static string? Reason(Exception failure, CancellationToken cancellationToken) => failure switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        // HttpClient's own time limit is a cancellation nobody asked for.
        OperationCanceledException => Strings.TimedOut,
        HttpRequestException => Strings.ReleasesUnreachable(Failures.Describe(failure)),
        PlatformInstallException { Failure: InstallFailure.ChecksumMismatch } => Strings.ChecksumMismatch,
        PlatformInstallException { Failure: InstallFailure.NotAPlatform } => Strings.NotAPlatform,
        PlatformInstallException { Failure: InstallFailure.TooLarge } => Strings.TooLarge,
        PlatformInstallException { Failure: InstallFailure.UnreadableReleases } => Strings.ReleasesUnreadable,
        _ => Strings.InstallFailed(Failures.Describe(failure)),
    };
}
