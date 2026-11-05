using System.Diagnostics;
using RDCore.HexIDE.Resources;

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
        HttpRequestException { StatusCode: { } status } => Strings.HttpStatus((int)status),
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Strings.SecureConnectionFailed,
        HttpRequestException => Strings.NoConnection,
        UnauthorizedAccessException => Strings.AccessDenied,
        IOException when IsDiskFull(failure.HResult) => Strings.DiskFull,
        IOException => Strings.FileProblem,
        _ => Strings.UnexpectedFailure(failure.GetType().Name),
    };

    /// <summary>Traces <paramref name="failure"/> whole, when the user is told a reason <see cref="Describe"/> didn't give.</summary>
    public static void Record(Exception failure) => Trace.TraceWarning($"RDCore for HexIDE: {failure}");

    private static bool IsDiskFull(int hresult) => OperatingSystem.IsWindows()
        ? hresult == FromWin32(DiskFull) || hresult == FromWin32(HandleDiskFull)
        : hresult == NoSpace;

    private static int FromWin32(int error) => unchecked((int)0x80070000) | error;
}
