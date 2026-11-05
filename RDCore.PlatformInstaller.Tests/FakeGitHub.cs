using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RDCore.PlatformInstaller.Tests;

/// <summary>
/// GitHub's releases API and download hosts, served from memory: each release a test adds is listed, with its zip and its
/// SHA256SUMS. Every request is recorded, so a test can tell what was downloaded.
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

    /// <summary>Adds a release whose zip holds <paramref name="entries"/>, by default a whole platform.</summary>
    /// <param name="listedSize">The size the release lists for its zip, when it isn't the zip's own.</param>
    /// <param name="host">The host its assets are on.</param>
    /// <param name="binaryMarker">Whether SHA256SUMS marks the zip as read in binary mode (<c>*name</c>).</param>
    public FakeGitHub Release(string tag, bool prerelease = false, bool draft = false, string rid = "win-x64",
        IReadOnlyDictionary<string, string>? entries = null, string? digest = null, long? listedSize = null,
        string host = "github.com", bool binaryMarker = false)
    {
        var version = tag.TrimStart('v');
        var zipName = $"rdcore-{version}-{rid}.zip";
        var zip = Zip(entries ?? Platform(version));
        var zipUrl = $"https://{host}/rubberduck-vba/RDCore/releases/download/{tag}/{zipName}";
        var sumsUrl = $"https://{host}/rubberduck-vba/RDCore/releases/download/{tag}/SHA256SUMS";
        files[zipUrl] = zip;
        files[sumsUrl] = Encoding.UTF8.GetBytes(
            $"{digest ?? Convert.ToHexStringLower(SHA256.HashData(zip))} {(binaryMarker ? "*" : " ")}{zipName}\n");
        releases.Add(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = new object[]
            {
                new { name = zipName, browser_download_url = zipUrl, size = listedSize ?? zip.Length },
                new { name = "SHA256SUMS", browser_download_url = sumsUrl, size = files[sumsUrl].Length },
            },
        });
        return this;
    }

    /// <summary>A zip of <paramref name="entries"/>, as a release's would be.</summary>
    public static byte[] ZipOf(IReadOnlyDictionary<string, string> entries) => Zip(entries);

    /// <summary>What a platform's zip holds: the manifest, and the language server where it names it.</summary>
    public static Dictionary<string, string> Platform(string version) => new()
    {
        ["rdcore.json"] = $$"""{ "platformVersion": "{{version}}", "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""",
        ["RDCore.LanguageServer/RDCore.LanguageServer.exe"] = "not really a server",
        ["release.json"] = """{ "schema": 1, "runtime": { "name": "Microsoft.NETCore.App", "minimumVersion": "10.0.0" } }""",
    };

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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(releases)) };
        }

        if (!files.TryGetValue(url, out var content))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (url.EndsWith(".zip", StringComparison.Ordinal) && DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (url.EndsWith(".zip", StringComparison.Ordinal) && MidDownloadGate is { } midway)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HeldStream(content, midway.Task)) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
    }

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
