using RDCore.PlatformInstaller;

namespace RDCore.HexIDE.Platform;

/// <summary>
/// What the add-in asks or tells its user while it gets the platform. HexIDE offers an add-in no dialogs of its own, so the
/// add-in shows these itself (<see cref="AvaloniaPrompts"/>).
/// </summary>
internal interface IUserPrompts
{
    /// <summary>Asks whether to download <paramref name="release"/> into <paramref name="target"/>; true when the user agrees.</summary>
    Task<bool> ConfirmDownloadAsync(PlatformRelease release, string target);

    /// <summary>Tells the user something they can only acknowledge.</summary>
    Task TellAsync(string message);

    /// <summary>Shows how an installation of <paramref name="release"/> goes, until it is disposed.</summary>
    IInstallView ShowInstall(PlatformRelease release);
}

/// <summary>An installation's progress, shown while it lasts.</summary>
internal interface IInstallView : IProgress<InstallProgress>, IDisposable;
