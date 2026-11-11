using System.Net.Http.Headers;
using System.Text.Json;

namespace RDCore.PlatformInstaller;

/// <summary>
/// The platform's published releases, read from GitHub's releases API without authentication. A draft release is never
/// offered: a tag's release is a draft until a maintainer publishes it, and an anonymous caller doesn't see it before then.
/// </summary>
public sealed class ReleaseFeed(HttpClient http, PlatformInstallerOptions options)
{
    internal const string ChecksumsAsset = "SHA256SUMS";

    /// <summary>
    /// The newest release the channel offers that has a platform for the runtime identifier, or null when none has. The stable
    /// channel offers no release whose version is a prerelease's, whatever the release says it is.
    /// </summary>
    /// <exception cref="HttpRequestException">The releases couldn't be reached.</exception>
    /// <exception cref="PlatformInstallException">What GitHub answered isn't a list of releases.</exception>
    public async Task<PlatformRelease?> FindLatestAsync(CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false))
            .Where(release => options.Channel == ReleaseChannel.Preview || !(release.Prerelease || release.Release.Version.IsPrerelease))
            .Select(release => release.Release)
            .MaxBy(release => release.Version);

    /// <summary>
    /// The published release of <paramref name="version"/> for the runtime identifier, whatever the channel, or null when there
    /// is none: it was taken down, or never had a platform for this machine.
    /// </summary>
    /// <exception cref="HttpRequestException">The releases couldn't be reached.</exception>
    /// <exception cref="PlatformInstallException">What GitHub answered isn't a list of releases.</exception>
    public async Task<PlatformRelease?> FindAsync(PlatformVersion version, CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false))
            .Select(release => release.Release)
            .FirstOrDefault(release => release.Version.Equals(version));

    private async Task<IReadOnlyList<(PlatformRelease Release, bool Prerelease)>> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(options.ApiBase, $"repos/{options.Repository}/releases?per_page=100"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RDCore.PlatformInstaller", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await BoundedContent.ReadAsync(response, options.MaxReleasesBytes,
            size => new PlatformInstallException(InstallFailure.UnreadableReleases,
                $"What GitHub answered for {options.Repository}'s releases is bigger than a list of them should be: {size} bytes at least, where {options.MaxReleasesBytes} is the most."),
            cancellationToken).ConfigureAwait(false);
        try
        {
            using var releases = JsonDocument.Parse(body);
            List<(PlatformRelease, bool)> published = [];
            foreach (var release in releases.RootElement.EnumerateArray())
            {
                if (Read(release) is { } candidate)
                {
                    published.Add(candidate);
                }
            }
            return published;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new PlatformInstallException(InstallFailure.UnreadableReleases,
                $"What GitHub answered for {options.Repository}'s releases isn't a list of releases: {ex.Message}", ex);
        }
    }

    private (PlatformRelease, bool)? Read(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        var prerelease = release.TryGetProperty("prerelease", out var flag) && flag.GetBoolean();
        if (!release.TryGetProperty("tag_name", out var tag) || !PlatformVersion.TryParse(tag.GetString(), out var version))
        {
            return null;
        }

        if (!release.TryGetProperty("assets", out var assets))
        {
            return null;
        }

        // The archive is named by the platform manifest's version and the runtime identifier it was built for: a zip for
        // Windows, a tar.gz for Linux and macOS.
        var zipSuffix = $"-{options.RuntimeIdentifier}.zip";
        var tarGzSuffix = $"-{options.RuntimeIdentifier}.tar.gz";
        (Uri Url, long Size)? archive = null;
        Uri? checksums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url))
            {
                continue;
            }

            if (name == ChecksumsAsset)
            {
                checksums = url;
            }
            else if (name.StartsWith("rdcore-", StringComparison.Ordinal)
                && (name.EndsWith(zipSuffix, StringComparison.Ordinal) || name.EndsWith(tarGzSuffix, StringComparison.Ordinal)))
            {
                archive = (url, asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
            }
        }

        if (archive is not { } platform || checksums is null || !Trusted(platform.Url) || !Trusted(checksums))
        {
            return null;
        }

        return (new PlatformRelease(version, platform.Url, platform.Size, checksums), prerelease);
    }

    private bool Trusted(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && options.AssetHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
}
