using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>What the user answers to a question about where RDCore's platform comes from.</summary>
internal enum PlatformAnswer
{
    /// <summary>Not now: no platform.</summary>
    NotNow,

    /// <summary>Install RDCore from its releases, in the installer's root.</summary>
    Install,

    /// <summary>Use the RDCore found on the machine.</summary>
    UseFound,

    /// <summary>Locate an RDCore on the machine, in a folder the user picks.</summary>
    Locate,
}

/// <summary>
/// What the add-in asks or tells its user while it gets the platform. HexIDE offers an add-in no dialogs of its own, so the
/// add-in shows these itself (<see cref="AvaloniaPrompts"/>).
/// </summary>
internal interface IUserPrompts
{
    /// <summary>
    /// Asks whether to download <paramref name="release"/> into <paramref name="target"/>: <see cref="PlatformAnswer.Install"/>
    /// when the user agrees, <see cref="PlatformAnswer.Locate"/> when they would rather locate an RDCore on the machine, which
    /// is offered only with <paramref name="canLocate"/>, and <see cref="PlatformAnswer.NotNow"/> otherwise.
    /// </summary>
    /// <param name="release">The release to download.</param>
    /// <param name="target">Where it goes.</param>
    /// <param name="canLocate">Whether to offer to locate an RDCore instead.</param>
    Task<PlatformAnswer> ConfirmDownloadAsync(PlatformRelease release, string target, bool canLocate);

    /// <summary>
    /// Asks what to do with <paramref name="found"/>, an RDCore already on the machine that the add-in can use:
    /// <see cref="PlatformAnswer.UseFound"/>, <see cref="PlatformAnswer.Install"/> in <paramref name="target"/> instead,
    /// <see cref="PlatformAnswer.Locate"/> another one, or <see cref="PlatformAnswer.NotNow"/>.
    /// </summary>
    /// <param name="found">The RDCore found.</param>
    /// <param name="target">Where an installation goes.</param>
    Task<PlatformAnswer> ChooseFoundAsync(PlatformFolder found, string target);

    /// <summary>Asks for the folder of an RDCore platform; null when the user picks none.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Tells the user something they can only acknowledge.</summary>
    /// <param name="message">What to tell.</param>
    Task TellAsync(string message);

    /// <summary>Shows how an installation of <paramref name="release"/> goes, until it is disposed.</summary>
    /// <param name="release">The release being installed.</param>
    IInstallView ShowInstall(PlatformRelease release);
}

/// <summary>An installation's progress, shown while it lasts.</summary>
internal interface IInstallView : IProgress<InstallProgress>, IDisposable;
