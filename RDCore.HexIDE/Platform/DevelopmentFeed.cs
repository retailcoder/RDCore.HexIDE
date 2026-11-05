using System.Net;

namespace RDCore.HexIDE.Platform;

/// <summary>
/// GitHub's releases API and download hosts answered from a folder, for trying the download, the question and the installation
/// before a release is published (<see cref="AddinSettings.DevelopmentFeed"/>). A request for <c>https://&lt;host&gt;/&lt;path&gt;</c>
/// is answered with the file <c>&lt;folder&gt;/&lt;host&gt;/&lt;path&gt;</c>, its query left out, so the list of releases is
/// <c>api.github.com/repos/rubberduck-vba/RDCore/releases</c> under it. Anything the folder doesn't have is not found.
/// </summary>
/// <remarks>
/// Only the transport is replaced: the installer still reads the releases, refuses an asset anywhere but GitHub, and checks the
/// download against its checksums, as it does a published release. <c>tools/New-DevelopmentFeed.ps1</c> writes such a folder
/// from a built platform.
/// </remarks>
internal sealed class DevelopmentFeed(string folder) : HttpMessageHandler
{
    private readonly string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request.Method == HttpMethod.Get && FileFor(request.RequestUri) is { } file
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)),
                RequestMessage = request,
            }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
    }

    // The file a request names, or null when there is none, or the request would lead out of the folder.
    private string? FileFor(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(root, url.Host, Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/')));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(root, comparison) && File.Exists(path) ? path : null;
    }
}
