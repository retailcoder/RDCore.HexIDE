using System.Globalization;
using System.Resources;

namespace RDCore.HexIDE.Resources;

/// <summary>What the add-in says to its user, in English and in French (<c>Strings.resx</c>, <c>Strings.fr.resx</c>).</summary>
internal static class Strings
{
    private static readonly ResourceManager Resources = new("RDCore.HexIDE.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string name) => Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] values) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), values);

    public static string DownloadTitle => Get(nameof(DownloadTitle));

    /// <summary>{0}: the version; {1}: its size; {2}: where it comes from; {3}: where it goes.</summary>
    public static string DownloadQuestion(string version, string size, string source, string target) =>
        Format(nameof(DownloadQuestion), version, size, source, target);

    public static string DownloadYes => Get(nameof(DownloadYes));

    public static string DownloadNo => Get(nameof(DownloadNo));

    /// <summary>{0}: the version.</summary>
    public static string Downloading(string version) => Format(nameof(Downloading), version);

    public static string Verifying => Get(nameof(Verifying));

    public static string Extracting => Get(nameof(Extracting));

    public static string Deploying => Get(nameof(Deploying));

    public static string NoRelease => Get(nameof(NoRelease));

    /// <summary>{0}: why.</summary>
    public static string ReleasesUnreachable(string reason) => Format(nameof(ReleasesUnreachable), reason);

    /// <summary>{0}: why.</summary>
    public static string InstallFailed(string reason) => Format(nameof(InstallFailed), reason);

    /// <summary>{0}: the folder; {1}: why.</summary>
    public static string PlatformRootUnusable(string root, string reason) => Format(nameof(PlatformRootUnusable), root, reason);

    public static string Ok => Get(nameof(Ok));

    public static string TimedOut => Get(nameof(TimedOut));

    public static string ChecksumMismatch => Get(nameof(ChecksumMismatch));

    public static string NotAPlatform => Get(nameof(NotAPlatform));

    public static string TooLarge => Get(nameof(TooLarge));

    public static string ReleasesUnreadable => Get(nameof(ReleasesUnreadable));

    public static string NoManifest => Get(nameof(NoManifest));

    /// <summary>{0}: the version.</summary>
    public static string VersionGone(string version) => Format(nameof(VersionGone), version);

    /// <summary>{0}: the version.</summary>
    public static string PlatformGone(string version) => Format(nameof(PlatformGone), version);

    /// <summary>{0}: why.</summary>
    public static string ServerNotRegistered(string reason) => Format(nameof(ServerNotRegistered), reason);

    public static string ProjectTypeTitle => Get(nameof(ProjectTypeTitle));

    /// <summary>{0}: the project file.</summary>
    public static string ProjectFileGone(string path) => Format(nameof(ProjectFileGone), path);

    /// <summary>{0}: the project file.</summary>
    public static string ProjectNotFollowed(string path) => Format(nameof(ProjectNotFollowed), path);

    /// <summary>{0}: the project file.</summary>
    public static string ProjectFileUnreadable(string path) => Format(nameof(ProjectFileUnreadable), path);

    /// <summary>{0}: the status code.</summary>
    public static string HttpStatus(int status) => Format(nameof(HttpStatus), status);

    public static string SecureConnectionFailed => Get(nameof(SecureConnectionFailed));

    public static string NoConnection => Get(nameof(NoConnection));

    public static string AccessDenied => Get(nameof(AccessDenied));

    public static string DiskFull => Get(nameof(DiskFull));

    public static string FileProblem => Get(nameof(FileProblem));

    /// <summary>{0}: the exception's type name, for a report.</summary>
    public static string UnexpectedFailure(string type) => Format(nameof(UnexpectedFailure), type);

    public static string FolderMissing => Get(nameof(FolderMissing));

    public static string ManifestUnusable => Get(nameof(ManifestUnusable));

    public static string LanguageServerMissing => Get(nameof(LanguageServerMissing));

    /// <summary>{0}: a size in megabytes.</summary>
    public static string Megabytes(double megabytes) => Format(nameof(Megabytes), megabytes);

    public static string NoPlatformToRun => Get(nameof(NoPlatformToRun));
}
