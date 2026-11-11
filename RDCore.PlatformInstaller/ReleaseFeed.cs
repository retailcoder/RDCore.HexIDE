using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller;

/// <summary>
/// The platform's published releases, read from GitHub's releases API without authentication. A draft release is never
/// offered: a tag's release is a draft until a maintainer publishes it, and an anonymous caller doesn't see it before then.
/// </summary>
/// <param name="http">Reads the releases, and the <c>release.json</c> and <c>SHA256SUMS</c> of each one it might offer.</param>
/// <param name="options">The repository, the channel, the runtime identifier, the RDCore.SDK version and the bounds.</param>
public sealed class ReleaseFeed(HttpClient http, PlatformInstallerOptions options)
{
    internal const string ChecksumsAsset = "SHA256SUMS";

    /// <summary>
    /// The newest release the channel offers that has a platform for the runtime identifier, or null when none has. The stable
    /// channel offers no release whose version is a prerelease's, whatever the release says it is. With
    /// <see cref="PlatformInstallerOptions.SdkVersion"/>, a release is offered only when its platform was built with a
    /// compatible RDCore.SDK: a newer one that wasn't is passed over for the newest that was.
    /// </summary>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <exception cref="HttpRequestException">The releases couldn't be reached.</exception>
    /// <exception cref="PlatformInstallException">What GitHub answered isn't a list of releases.</exception>
    public async Task<PlatformRelease?> FindLatestAsync(CancellationToken cancellationToken)
    {
        var offered = (await ReadAsync(cancellationToken).ConfigureAwait(false))
            .Where(release => options.Channel == ReleaseChannel.Preview || !(release.Prerelease || release.Release.Version.IsPrerelease))
            .Select(release => release.Release)
            .OrderByDescending(release => release.Version);
        foreach (var release in offered)
        {
            if (options.SdkVersion is not { } sdk || await IsBuiltWithAsync(release, sdk, cancellationToken).ConfigureAwait(false))
            {
                return release;
            }
        }
        return null;
    }

    /// <summary>
    /// The published release of <paramref name="version"/> for the runtime identifier, whatever the channel, or null when there
    /// is none: it was taken down, or never had a platform for this machine. It is found whatever RDCore.SDK it was built with,
    /// as a version deployed already is the one asked for.
    /// </summary>
    /// <param name="version">The version.</param>
    /// <param name="cancellationToken">Ends the search.</param>
    /// <exception cref="HttpRequestException">The releases couldn't be reached.</exception>
    /// <exception cref="PlatformInstallException">What GitHub answered isn't a list of releases.</exception>
    public async Task<PlatformRelease?> FindAsync(PlatformVersion version, CancellationToken cancellationToken) =>
        (await ReadAsync(cancellationToken).ConfigureAwait(false))
            .Select(release => release.Release)
            .FirstOrDefault(release => release.Version.Equals(version));

    // Whether the release's release.json says its platform was built with an RDCore.SDK compatible with the client's, by the
    // SDK's rule (PlatformVersion.IsCompatibleWith). One the release doesn't have, that isn't there, is bigger than one should be,
    // isn't a release manifest, or doesn't match the release's SHA256SUMS says nothing the client can rely on, and its release is
    // not offered.
    private async Task<bool> IsBuiltWithAsync(PlatformRelease release, PlatformVersion sdk, CancellationToken cancellationToken)
    {
        if (release.ReleaseInfo is not { } url)
        {
            return false;
        }

        try
        {
            if (await GetAsync(release.Checksums, options.MaxChecksumsBytes, cancellationToken).ConfigureAwait(false) is not { } sums
                || await GetAsync(url, options.MaxReleaseInfoBytes, cancellationToken).ConfigureAwait(false) is not { } info)
            {
                return false;
            }

            var expected = Sha256Sums.Find(Encoding.UTF8.GetString(sums), Path.GetFileName(url.AbsolutePath));
            return expected is not null && Convert.ToHexStringLower(SHA256.HashData(info)) == expected
                && PlatformReleaseManifest.Parse(info).IsBuiltWith(sdk);
        }
        catch (Exception ex) when (ex is PlatformInstallException or InvalidDataException)
        {
            // Bigger than it should be, or not a release manifest.
            return false;
        }
    }

    // The content at the url, bounded, or null when it isn't there.
    private async Task<byte[]?> GetAsync(Uri url, long limit, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await BoundedContent.ReadAsync(response, limit,
            size => new PlatformInstallException(InstallFailure.TooLarge,
                $"{Path.GetFileName(url.AbsolutePath)} is bigger than it should be: {size} bytes at least, where {limit} is the most."),
            cancellationToken).ConfigureAwait(false);
    }

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
        // Windows, a tar.gz for Linux and macOS. Its release.json beside it is named as it is.
        var zipSuffix = $"-{options.RuntimeIdentifier}.zip";
        var tarGzSuffix = $"-{options.RuntimeIdentifier}.tar.gz";
        var releaseInfoSuffix = $"-{options.RuntimeIdentifier}.release.json";
        (Uri Url, long Size)? archive = null;
        Uri? checksums = null;
        Uri? releaseInfo = null;
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
            else if (name.StartsWith("rdcore-", StringComparison.Ordinal) && name.EndsWith(releaseInfoSuffix, StringComparison.Ordinal))
            {
                releaseInfo = url;
            }
        }

        if (archive is not { } platform || checksums is null || !Trusted(platform.Url) || !Trusted(checksums))
        {
            return null;
        }

        return (new PlatformRelease(version, platform.Url, platform.Size, checksums)
        {
            ReleaseInfo = releaseInfo is not null && Trusted(releaseInfo) ? releaseInfo : null,
        }, prerelease);
    }

    private bool Trusted(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && options.AssetHosts.Contains(url.Host, StringComparer.OrdinalIgnoreCase);
}
