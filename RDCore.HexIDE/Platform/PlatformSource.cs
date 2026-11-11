using System.Diagnostics;
using RDCore.HexIDE.Resources;
using RDCore.PlatformInstaller;
using RDCore.SDK.Platform;

namespace RDCore.HexIDE.Platform;

/// <summary>The platform a server runs from: its root, and the installed version when it is one, not a development root.</summary>
internal sealed record PlatformChoice(string Root, DeployedPlatform? Installed);

/// <summary>There is no platform to run; the message says why, in the user's language.</summary>
internal sealed class PlatformUnavailableException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// The platform RDCore runs from, found in this order:
/// <list type="number">
/// <item>the platform root the settings name: a development build, or an RDCore the user chose;</item>
/// <item>the platform in use in the installer's root;</item>
/// <item>an RDCore on the user's PATH outside that root, as an unpacked release archive leaves one, once the user chooses to
/// use it;</item>
/// <item>the newest release, downloaded only once the user agrees, or an RDCore the user locates instead.</item>
/// </list>
/// Nothing is installed when an RDCore on the machine can be used, so the add-in never makes a second copy of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only an RDCore that suits the add-in.</b> One found on the PATH, or located, is used only when it was built for this
/// machine, with an RDCore.SDK compatible with the add-in's own, as its <c>release.json</c> says. The add-in doesn't update
/// one it uses: it checks it again at each start, and when it doesn't suit the add-in, it says so and looks for another.
/// </para>
/// <para>
/// <b>One question at a time.</b> RDCore projects opened, and runs started, while the user is asked share the one question and
/// its answer.
/// </para>
/// <para>
/// <b>"Not now" is for the question nobody asked for.</b> It holds until HexIDE restarts for the question opening a project
/// asks: no RDCore project opened after it asks again. A run is the user asking for RDCore, so a run asks again.
/// </para>
/// <para>
/// <b>Off the UI thread.</b> The installation runs on the thread pool; only its progress and its outcome reach the UI.
/// </para>
/// <para>
/// <b>Never silent, never twice.</b> Whatever stops RDCore from having a platform, the user is told why, in their language,
/// unless it was their own "Not now" on opening a project. A run isn't told: it fails with the reason, which HexIDE shows.
/// </para>
/// <para>
/// <b>On the PATH.</b> Once an installation makes a version the one in use, its <c>rdc install --path-only</c> puts its
/// <c>rdc</c> on the user's PATH. The platform runs without it, so a failure there is traced, and nothing more.
/// </para>
/// </remarks>
/// <param name="settings">The add-in's settings, as read at start.</param>
/// <param name="settingsPath">Where the settings are saved when the user chooses an RDCore on the machine.</param>
/// <param name="installer">The installer, and its root.</param>
/// <param name="prompts">What the user is asked and told.</param>
/// <param name="machine">The PATH, and the processes the add-in runs.</param>
internal sealed class PlatformSource(AddinSettings settings, string settingsPath, PlatformInstaller.PlatformInstaller installer,
    IUserPrompts prompts, IMachine machine)
{
    /// <summary>The longest <c>rdc install --path-only</c> may run.</summary>
    internal static readonly TimeSpan PathTimeout = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    private readonly HashSet<string> onPath = new(StringComparer.Ordinal);
    private Task<Decision>? asking;
    private bool declined;
    private bool toldUnsuitable;
    private AddinSettings settings = settings;

    /// <summary>
    /// The platform for a project that opened, or null when there is none to run: the user declined the download, now or
    /// earlier, or there is nothing to download, or it failed, which the user has been told.
    /// </summary>
    /// <param name="cancellationToken">Ends the search, when the project closes.</param>
    public async Task<PlatformChoice?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FindAsync(askAgain: false, cancellationToken);
        }
        catch (PlatformUnavailableException unavailable)
        {
            await prompts.TellAsync(unavailable.Message);
            return null;
        }
    }

    /// <summary>
    /// The platform for a run the user started: as for a project that opened, except that an earlier "Not now" doesn't hold,
    /// so the user is asked again, and that the user isn't told why there is none: the run fails with the reason.
    /// </summary>
    /// <param name="cancellationToken">Ends the search, when the run is ended.</param>
    /// <exception cref="PlatformUnavailableException">There is none to run; the message says why, for HexIDE to show.</exception>
    /// <exception cref="OperationCanceledException">The run was ended before it had one.</exception>
    public async Task<PlatformChoice> GetToRunAsync(CancellationToken cancellationToken) =>
        await FindAsync(askAgain: true, cancellationToken) ?? throw new PlatformUnavailableException(Strings.DownloadDeclined);

    // Null for a declined download, said nowhere: the caller says it, or doesn't.
    private async Task<PlatformChoice?> FindAsync(bool askAgain, CancellationToken cancellationToken)
    {
        if (await ConfiguredAsync() is { } configured)
        {
            return configured;
        }

        if (installer.Current is { } installed)
        {
            return new PlatformChoice(installed.Root, installed);
        }

        lock (gate)
        {
            if (declined && !askAgain)
            {
                return null;
            }
        }

        try
        {
            var decision = await DecideAsync(ChooseAsync);
            if (decision.Use is { } use)
            {
                return use;
            }

            if (decision.Install is not { } release)
            {
                return null;
            }

            // The project may have closed while the question was open: then nothing is downloaded for it.
            cancellationToken.ThrowIfCancellationRequested();
            var deployed = await InstallAsync(release, cancellationToken);
            // The RDCore the settings name doesn't suit the add-in, or this wouldn't be reached: the installed one replaces it.
            if (settings.PlatformRoot is { Length: > 0 })
            {
                Save(null);
            }
            return new PlatformChoice(deployed.Root, deployed);
        }
        catch (Exception ex) when (ex is not PlatformUnavailableException && Reason(ex, cancellationToken) is { } reason)
        {
            throw new PlatformUnavailableException(reason, ex);
        }
    }

    // The platform root the settings name, while it runs. A development build has no release.json, and runs as it is. An RDCore
    // the user chose has one, and runs while it suits the add-in; when it doesn't, the user is told once, and another is found.
    private async Task<PlatformChoice?> ConfiguredAsync()
    {
        if (settings.PlatformRoot is not { Length: > 0 } root)
        {
            return null;
        }

        if (PlatformFolder.Read(root) is not { HasReleaseInfo: true } chosen || Unsuitable(chosen) is not { } why)
        {
            return new PlatformChoice(root, null);
        }

        bool tell;
        lock (gate)
        {
            tell = !toldUnsuitable;
            toldUnsuitable = true;
        }

        if (tell)
        {
            await prompts.TellAsync(Strings.ChosenPlatformUnsuitable(chosen.Version, chosen.Root, why));
        }
        return null;
    }

    // Where RDCore comes from, when the machine has no platform in use: one found on the PATH, the newest release, or one the
    // user locates. Nothing is downloaded here.
    private async Task<Decision> ChooseAsync()
    {
        var target = PlatformInstallerOptions.DefaultRoot;
        if (FindOnPath() is { } found)
        {
            switch (await prompts.ChooseFoundAsync(found, target))
            {
                case PlatformAnswer.UseFound:
                    return Use(found.Root);
                case PlatformAnswer.Locate:
                    return await LocateAsync();
                case PlatformAnswer.Install:
                    // On to the download question, which says what is downloaded, and from where.
                    break;
                default:
                    return Decision.None;
            }
        }

        var release = await installer.Feed.FindLatestAsync(CancellationToken.None)
            ?? throw new PlatformUnavailableException(Strings.NoRelease);
        return await prompts.ConfirmDownloadAsync(release, target, canLocate: true) switch
        {
            PlatformAnswer.Install => new Decision(null, release),
            PlatformAnswer.Locate => await LocateAsync(),
            _ => Decision.None,
        };
    }

    // A folder the user picks, until it is one the add-in can use, or they pick none. Each one refused says why.
    private async Task<Decision> LocateAsync()
    {
        while (await prompts.PickFolderAsync() is { } picked)
        {
            var folder = PlatformFolder.Read(picked);
            if ((folder is null ? Strings.NotAPlatformFolder : Unsuitable(folder)) is { } why)
            {
                await prompts.TellAsync(Strings.PlatformRootUnusable(picked, why));
                continue;
            }

            return Use(folder!.Root);
        }
        return Decision.None;
    }

    // The first rdc on the PATH whose platform suits the add-in, followed through a link to where it is. One in the installer's
    // root isn't offered: that root's platform in use was found already, when there is one.
    private PlatformFolder? FindOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "rdc.exe" : "rdc";
        foreach (var folder in machine.PathFolders)
        {
            string rdc;
            try
            {
                rdc = Path.GetFullPath(Path.Combine(folder, name));
                if (!File.Exists(rdc))
                {
                    continue;
                }

                rdc = new FileInfo(rdc).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? rdc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                continue;
            }

            // rdc is in the platform's RDCore.CLI folder, or at its root.
            var beside = Path.GetDirectoryName(rdc)!;
            var root = File.Exists(Path.Combine(beside, "rdcore.json")) ? beside : Path.GetDirectoryName(beside);
            if (root is null || IsInside(root, installer.Root))
            {
                continue;
            }

            if (PlatformFolder.Read(root) is { } platform && Unsuitable(platform) is null)
            {
                return platform;
            }
        }
        return null;
    }

    // Why the add-in can't use the platform, to follow a colon, or null when it can.
    private static string? Unsuitable(PlatformFolder platform)
    {
        var machineRid = PlatformInstallerOptions.MachineRuntimeIdentifier;
        if (!platform.IsFor(machineRid))
        {
            return Strings.OtherSystem(platform.RuntimeIdentifier!, machineRid);
        }

        if (platform.SdkVersion is not { } built)
        {
            return Strings.SdkUnknown;
        }

        var sdk = AddinSettings.SdkVersion;
        return platform.IsBuiltWith(sdk)
            ? null
            : Strings.SdkIncompatible(built.Text, sdk.CompatibleRange);
    }

    private static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var container = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(container, comparison) || full.StartsWith(container + Path.DirectorySeparatorChar, comparison);
    }

    // The RDCore at the root, kept in the settings as the user's choice, so the next start uses it without asking.
    private Decision Use(string root)
    {
        Save(root);
        return new Decision(new PlatformChoice(root, null), null);
    }

    // Saved for the next start; a choice that can't be saved still holds until HexIDE closes.
    private void Save(string? platformRoot)
    {
        lock (gate)
        {
            settings = settings with { PlatformRoot = platformRoot };
        }

        try
        {
            AddinSettings.SavePlatformRoot(settingsPath, platformRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Failures.Record(ex);
        }
    }

    /// <summary>
    /// Makes sure the platform <paramref name="choice"/> names is still there before its server starts: its manifest, and
    /// <paramref name="languageServer"/>, the executable the server's launch names. When either has gone, the same version is
    /// deployed again, as the server's launch names it, once the user agrees.
    /// </summary>
    /// <param name="choice">The platform the server runs from.</param>
    /// <param name="languageServer">The executable the server's launch names.</param>
    /// <param name="cancellationToken">Ends the check, when the server's start is ended.</param>
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
            if (release is not null)
            {
                var asked = release;
                agreed = (await DecideAsync(async () =>
                    await prompts.ConfirmDownloadAsync(asked, PlatformInstallerOptions.DefaultRoot, canLocate: false) == PlatformAnswer.Install
                        ? new Decision(null, asked)
                        : Decision.None)).Install is not null;
                if (agreed)
                {
                    await InstallAsync(release, cancellationToken);
                }
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
    /// <param name="choice">The platform.</param>
    /// <param name="languageServer">The executable the server's launch names.</param>
    public static bool IsWhole(PlatformChoice choice, string languageServer) => HasManifest(choice) && File.Exists(languageServer);

    private static bool HasManifest(PlatformChoice choice) => File.Exists(Path.Combine(choice.Root, "rdcore.json"));

    // Asked once for every project that wants it at the same time: a question still open is shared, and one answered is never
    // answered again, so a later question is asked afresh.
    private Task<Decision> DecideAsync(Func<Task<Decision>> decide)
    {
        lock (gate)
        {
            if (asking is { IsCompleted: false } open)
            {
                return open;
            }

            return asking = Decide();
        }

        async Task<Decision> Decide()
        {
            var decision = await decide();
            lock (gate)
            {
                declined = decision == Decision.None;
            }
            return decision;
        }
    }

    private async Task<DeployedPlatform> InstallAsync(PlatformRelease release, CancellationToken cancellationToken)
    {
        DeployedPlatform deployed;
        using (var view = prompts.ShowInstall(release))
        {
            deployed = await Task.Run(() => installer.InstallAsync(release, view, cancellationToken), cancellationToken);
        }

        await AddToPathAsync(deployed);
        return deployed;
    }

    // The version in use's rdc on the user's PATH, once for each version this add-in deployed and made the one in use. An older
    // version deployed beside the one in use isn't put there.
    private async Task AddToPathAsync(DeployedPlatform deployed)
    {
        if (installer.Current?.Version.Equals(deployed.Version) != true)
        {
            return;
        }

        lock (gate)
        {
            if (!onPath.Add(deployed.Root))
            {
                return;
            }
        }

        var rdc = PlatformFolder.Read(deployed.Root)?.HostService
            ?? Path.Combine(deployed.Root, "RDCore.CLI", OperatingSystem.IsWindows() ? "rdc.exe" : "rdc");
        if (!File.Exists(rdc))
        {
            Trace.TraceWarning($"RDCore for HexIDE: RDCore {deployed.Version.Text} has no rdc at {rdc}, so it isn't put on the PATH.");
            return;
        }

        try
        {
            var exitCode = await machine.RunAsync(rdc, ["install", "--path-only"], PathTimeout, CancellationToken.None);
            if (exitCode != 0)
            {
                Trace.TraceWarning($"RDCore for HexIDE: {rdc} install --path-only exited with {exitCode}; RDCore runs without it.");
            }
        }
        catch (Exception ex)
        {
            // It didn't start, or ran too long: RDCore runs without it.
            Failures.Record(ex);
        }
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

    // What the user decided: an RDCore to use, a release to install, or neither, which is "Not now".
    private sealed record Decision(PlatformChoice? Use, PlatformRelease? Install)
    {
        public static readonly Decision None = new(null, null);
    }
}
