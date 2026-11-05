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
    /// The newest release the channel offers that has a platform for the runtime identifier, or null when none has.
    /// </summary>
    /// <exception cref="HttpRequestException">The releases couldn't be read.</exception>
    public async Task<PlatformRelease?> FindLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            new Uri(options.ApiBase, $"repos/{options.Repository}/releases?per_page=100"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("RDCore.PlatformInstaller", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var releases = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);

        PlatformRelease? latest = null;
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (Read(release) is { } candidate && (latest is null || candidate.Version.CompareTo(latest.Version) > 0))
                latest = candidate;
        }
        return latest;
    }

    private PlatformRelease? Read(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            return null;
        var prerelease = release.TryGetProperty("prerelease", out var flag) && flag.GetBoolean();
        if (prerelease && options.Channel == ReleaseChannel.Stable)
            return null;
        if (!release.TryGetProperty("tag_name", out var tag) || !PlatformVersion.TryParse(tag.GetString(), out var version))
            return null;
        if (!release.TryGetProperty("assets", out var assets))
            return null;

        // The zip is named by the platform manifest's version and the runtime identifier it was built for.
        var archiveSuffix = $"-{options.RuntimeIdentifier}.zip";
        (Uri Url, long Size)? archive = null;
        Uri? checksums = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url))
                continue;
            if (name == ChecksumsAsset)
                checksums = url;
            else if (name.StartsWith("rdcore-", StringComparison.Ordinal) && name.EndsWith(archiveSuffix, StringComparison.Ordinal))
                archive = (url, asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
        }
        return archive is { } zip && checksums is not null ? new PlatformRelease(version, zip.Url, zip.Size, checksums) : null;
    }
}
