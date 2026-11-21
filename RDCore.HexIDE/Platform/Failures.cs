using System.Diagnostics;
using System.Reflection;
using RDCore.HexIDE.Resources;
using RDCore.SDK.Platform;

namespace RDCore.HexIDE.Platform;

/// <summary>
/// Why something failed, as the user is told it: in their language, never an exception's own message, which is English. The
/// exception itself is traced, for whoever looks into it.
/// </summary>
internal static class Failures
{
    // ERROR_HANDLE_DISK_FULL and ERROR_DISK_FULL on Windows; ENOSPC elsewhere, which .NET gives as the HResult of an IOException.
    private const int HandleDiskFull = 39;
    private const int DiskFull = 112;
    private const int NoSpace = 28;

    /// <summary>Traces <paramref name="failure"/> and gives its <see cref="Reason"/>.</summary>
    public static string Describe(Exception failure)
    {
        Record(failure);
        return Reason(failure);
    }

    /// <summary>The reason <paramref name="failure"/> gives, to follow a colon: it ends with a period.</summary>
    public static string Reason(Exception failure) => failure switch
    {
        _ when MissingAssembly(failure) is { } assembly => Strings.AddinIncomplete(assembly),
        HttpRequestException { StatusCode: { } status } => Strings.HttpStatus((int)status),
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Strings.SecureConnectionFailed,
        HttpRequestException => Strings.NoConnection,
        UnauthorizedAccessException => Strings.AccessDenied,
        IOException when IsDiskFull(failure.HResult) => Strings.DiskFull,
        IOException => Strings.FileProblem,
        _ => Strings.UnexpectedFailure(failure.GetType().Name),
    };

    /// <summary>
    /// Why a launch couldn't be prepared from a platform's root, as <c>RDCoreLanguageServerLaunch.Prepare</c> and
    /// <c>RDCoreDebugAdapterLaunch.Prepare</c> say it, to follow a colon.
    /// </summary>
    public static string Unusable(Exception failure) => failure switch
    {
        _ when MissingAssembly(failure) is not null => Reason(failure),
        DirectoryNotFoundException => Strings.FolderMissing,
        FileNotFoundException missing when string.Equals(Path.GetFileName(missing.FileName), PlatformManifest.FileName, StringComparison.OrdinalIgnoreCase)
            => Strings.NoManifest,
        FileNotFoundException => Strings.LanguageServerMissing,
        InvalidDataException => Strings.ManifestUnusable,
        _ => Reason(failure),
    };

    /// <summary>Traces <paramref name="failure"/> whole, when the user is told a reason <see cref="Describe"/> didn't give.</summary>
    public static void Record(Exception failure) => Trace.TraceWarning($"RDCore for HexIDE: {failure}");

    // An assembly the add-in's package doesn't carry: its loader names it as the file, with its version, and a type that needed
    // it to initialize fails with it.
    private static string? MissingAssembly(Exception failure) => failure switch
    {
        TypeInitializationException { InnerException: { } inner } => MissingAssembly(inner),
        FileNotFoundException { FileName: { } file } => AssemblyNamed(file),
        FileLoadException { FileName: { } file } => AssemblyNamed(file),
        _ => null,
    };

    private static string? AssemblyNamed(string file)
    {
        try
        {
            return new AssemblyName(file) is { Version: not null, Name: { } name } ? name : null;
        }
        catch (Exception ex) when (ex is FileLoadException or ArgumentException)
        {
            // A file's path, which isn't an assembly's name.
            return null;
        }
    }

    private static bool IsDiskFull(int hresult) => OperatingSystem.IsWindows()
        ? hresult == FromWin32(DiskFull) || hresult == FromWin32(HandleDiskFull)
        : hresult == NoSpace;

    private static int FromWin32(int error) => unchecked((int)0x80070000) | error;
}
