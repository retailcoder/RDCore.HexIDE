using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RDCore.PlatformInstaller.Tests;

/// <summary>
/// GitHub's releases API and download hosts, served from memory: each release a test adds is listed, with its archive, its
/// release.json and its SHA256SUMS. Every request is recorded, so a test can tell what was downloaded.
/// </summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly List<object> releases = [];
    private readonly Dictionary<string, byte[]> files = [];

    public List<Uri> Requests { get; } = [];

    /// <summary>Holds the next response to a download until it is released, so a test can act while one is in flight.</summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    /// <summary>
    /// Sends the first bytes of a download, then holds the rest until it is released, so a test can act while some of a file
    /// is written.
    /// </summary>
    public TaskCompletionSource? MidDownloadGate { get; set; }

    /// <summary>Sends every answer without a <c>Content-Length</c>, as a chunked response would be.</summary>
    public bool Unsized { get; set; }

    /// <summary>Adds a release whose archive holds <paramref name="entries"/>, by default a whole platform.</summary>
    /// <param name="listedSize">The size the release lists for its archive, when it isn't the archive's own.</param>
    /// <param name="host">The host its assets are on.</param>
    /// <param name="binaryMarker">Whether SHA256SUMS marks the archive as read in binary mode (<c>*name</c>).</param>
    /// <param name="tarGz">Whether its archive is a tar.gz, as a Linux or macOS release's is, rather than a zip.</param>
    /// <param name="archive">The archive's bytes, when they aren't those of <paramref name="entries"/>.</param>
    /// <param name="sdk">
    /// The RDCore.SDK version its <c>rdcore-&lt;version&gt;-&lt;rid&gt;.release.json</c> says the platform was built with, or
    /// null for a <c>release.json</c> without an <c>sdk</c>.
    /// </param>
    /// <param name="releaseInfo">Whether the release has its <c>release.json</c> beside its archive.</param>
    /// <param name="releaseInfoDigest">The digest SHA256SUMS lists for the <c>release.json</c>, when it isn't its own.</param>
    public FakeGitHub Release(string tag, bool prerelease = false, bool draft = false, string rid = "win-x64",
        IReadOnlyDictionary<string, string>? entries = null, string? digest = null, long? listedSize = null,
        string host = "github.com", bool binaryMarker = false, bool tarGz = false, byte[]? archive = null,
        string? sdk = Sdk, bool releaseInfo = true, string? releaseInfoDigest = null)
    {
        var version = tag.TrimStart('v');
        var download = $"https://{host}/rubberduck-vba/RDCore/releases/download/{tag}";
        var zipName = $"rdcore-{version}-{rid}.{(tarGz ? "tar.gz" : "zip")}";
        var zip = archive ?? (tarGz ? TarGzOf(entries ?? Platform(version, "")) : Zip(entries ?? Platform(version)));
        var zipUrl = $"{download}/{zipName}";
        var sumsUrl = $"{download}/SHA256SUMS";
        var infoName = $"rdcore-{version}-{rid}.release.json";
        var infoUrl = $"{download}/{infoName}";
        var info = Encoding.UTF8.GetBytes(ReleaseInfo(sdk));
        files[zipUrl] = zip;
        files[infoUrl] = info;
        files[sumsUrl] = Encoding.UTF8.GetBytes(
            $"{digest ?? Convert.ToHexStringLower(SHA256.HashData(zip))} {(binaryMarker ? "*" : " ")}{zipName}\n"
            + $"{releaseInfoDigest ?? Convert.ToHexStringLower(SHA256.HashData(info))}  {infoName}\n");
        List<object> assets =
        [
            new { name = zipName, browser_download_url = zipUrl, size = listedSize ?? zip.Length },
            new { name = "SHA256SUMS", browser_download_url = sumsUrl, size = files[sumsUrl].Length },
        ];
        if (releaseInfo)
        {
            assets.Add(new { name = infoName, browser_download_url = infoUrl, size = info.Length });
        }

        releases.Add(new { tag_name = tag, draft, prerelease, assets });
        return this;
    }

    /// <summary>The RDCore.SDK version a release's platform is built with unless a test says otherwise.</summary>
    public const string Sdk = "0.1.1-alpha";

    /// <summary>
    /// A release's <c>release.json</c>, as RDCore's release workflow writes one, saying the platform was built with RDCore.SDK
    /// <paramref name="sdk"/>; one without an <c>sdk</c> for null.
    /// </summary>
    public static string ReleaseInfo(string? sdk) => sdk is null
        ? """{ "schema": 1, "runtime": { "name": "Microsoft.NETCore.App", "minimumVersion": "10.0.0" } }"""
        : $$"""{ "schema": 1, "runtime": { "name": "Microsoft.NETCore.App", "minimumVersion": "10.0.0" }, "sdk": { "package": "RDCore.SDK", "version": "{{sdk}}" } }""";

    /// <summary>A zip of <paramref name="entries"/>, as a release's would be.</summary>
    public static byte[] ZipOf(IReadOnlyDictionary<string, string> entries) => Zip(entries);

    /// <summary>
    /// What a platform's archive holds: the manifest, and the language server where it names it, whose name ends with
    /// <paramref name="executableSuffix"/>: <c>.exe</c> for Windows, nothing for Linux and macOS.
    /// </summary>
    public static Dictionary<string, string> Platform(string version, string executableSuffix = ".exe") => new()
    {
        ["rdcore.json"] = $$"""{ "platformVersion": "{{version}}", "langService": "RDCore.LanguageServer/RDCore.LanguageServer{{executableSuffix}}" }""",
        [$"RDCore.LanguageServer/RDCore.LanguageServer{executableSuffix}"] = "not really a server",
        ["release.json"] = ReleaseInfo(Sdk),
    };

    /// <summary>
    /// A tar.gz of <paramref name="entries"/>, as RDCore's release workflow writes one: ustar, each folder an entry of its own
    /// before what is in it, the language server 0755 and every other file 0644. <paramref name="more"/> writes entries of its
    /// own after them.
    /// </summary>
    public static byte[] TarGzOf(IReadOnlyDictionary<string, string> entries, Action<TarWriter>? more = null)
    {
        const UnixFileMode executable = (UnixFileMode)0x1ED;
        const UnixFileMode regular = (UnixFileMode)0x1A4;
        var folders = entries.Keys.SelectMany(name => name.Split('/').SkipLast(1)
            .Select((_, depth) => string.Join('/', name.Split('/').Take(depth + 1)) + "/"));
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            foreach (var name in folders.Concat(entries.Keys).Distinct().Order(StringComparer.Ordinal))
            {
                if (name.EndsWith('/'))
                {
                    writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, name) { Mode = executable });
                    continue;
                }

                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode = name.StartsWith("RDCore.LanguageServer/RDCore.LanguageServer", StringComparison.Ordinal) ? executable : regular,
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(entries[name])),
                });
            }

            more?.Invoke(writer);
        }
        return buffer.ToArray();
    }

    private static byte[] Zip(IReadOnlyDictionary<string, string> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }
        return buffer.ToArray();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request.RequestUri!);
        }

        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == "https://api.github.com/repos/rubberduck-vba/RDCore/releases")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Answer(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(releases))) };
        }

        if (!files.TryGetValue(url, out var content))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        var isArchive = url.EndsWith(".zip", StringComparison.Ordinal) || url.EndsWith(".tar.gz", StringComparison.Ordinal);
        if (isArchive && DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (isArchive && MidDownloadGate is { } midway)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HeldStream(content, midway.Task)) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = Answer(content) };
    }

    // A stream that can't seek has no length to send.
    private HttpContent Answer(byte[] content) =>
        Unsized ? new StreamContent(new HeldStream(content, Task.CompletedTask)) : new ByteArrayContent(content);

    // The first half of its bytes at once, the rest once the gate opens.
    private sealed class HeldStream(byte[] content, Task gate) : Stream
    {
        private int position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (position >= content.Length / 2)
            {
                await gate.WaitAsync(cancellationToken);
            }

            var count = Math.Min(buffer.Length, position < content.Length / 2 ? content.Length / 2 - position : content.Length - position);
            content.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
