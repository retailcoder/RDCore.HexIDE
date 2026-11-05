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

    /// <summary>Adds a release whose zip holds <paramref name="entries"/>, by default a whole platform.</summary>
    public FakeGitHub Release(string tag, bool prerelease = false, bool draft = false, string rid = "win-x64",
        IReadOnlyDictionary<string, string>? entries = null, string? digest = null)
    {
        var version = tag.TrimStart('v');
        var zipName = $"rdcore-{version}-{rid}.zip";
        var zip = Zip(entries ?? Platform(version));
        var zipUrl = $"https://github.com/rubberduck-vba/RDCore/releases/download/{tag}/{zipName}";
        var sumsUrl = $"https://github.com/rubberduck-vba/RDCore/releases/download/{tag}/SHA256SUMS";
        files[zipUrl] = zip;
        files[sumsUrl] = Encoding.UTF8.GetBytes($"{digest ?? Convert.ToHexStringLower(SHA256.HashData(zip))}  {zipName}\n");
        releases.Add(new
        {
            tag_name = tag,
            draft,
            prerelease,
            assets = new object[]
            {
                new { name = zipName, browser_download_url = zipUrl, size = zip.Length },
                new { name = "SHA256SUMS", browser_download_url = sumsUrl, size = files[sumsUrl].Length },
            },
        });
        return this;
    }

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

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
    }
}
