using System.Security.Cryptography;
using System.Text.Json;
using RDCore.HexIDE.Platform;
using RDCore.PlatformInstaller;
using RDCore.PlatformInstaller.Tests;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class DevelopmentFeedTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rdcore-feed-" + Guid.NewGuid().ToString("N"));

    public TestContext TestContext { get; set; } = null!;

    public void Dispose()
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Nothing was written.
        }
    }

    private string Feed => Path.Combine(folder, "feed");

    // A feed of one release, laid out as tools/New-DevelopmentFeed.ps1 writes one.
    private void Publish(string version, string? digest = null, string? sdk = null)
    {
        var zipName = $"rdcore-{version}-win-x64.zip";
        var infoName = $"rdcore-{version}-win-x64.release.json";
        var download = $"rubberduck-vba/RDCore/releases/download/v{version}";
        var zip = FakeGitHub.ZipOf(FakeGitHub.Platform(version));
        Write($"github.com/{download}/{zipName}", zip);
        var info = System.Text.Encoding.UTF8.GetBytes(FakeGitHub.ReleaseInfo(sdk ?? AddinSettings.SdkVersion.Text));
        Write($"github.com/{download}/{infoName}", info);
        var sums = System.Text.Encoding.UTF8.GetBytes($"{digest ?? Convert.ToHexStringLower(SHA256.HashData(zip))}  {zipName}\n"
            + $"{Convert.ToHexStringLower(SHA256.HashData(info))}  {infoName}\n");
        Write($"github.com/{download}/SHA256SUMS", sums);
        Write("api.github.com/repos/rubberduck-vba/RDCore/releases", JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new
            {
                tag_name = $"v{version}",
                draft = false,
                prerelease = true,
                assets = new object[]
                {
                    new { name = zipName, browser_download_url = $"https://github.com/{download}/{zipName}", size = zip.Length },
                    new { name = "SHA256SUMS", browser_download_url = $"https://github.com/{download}/SHA256SUMS", size = sums.Length },
                    new { name = infoName, browser_download_url = $"https://github.com/{download}/{infoName}", size = info.Length },
                },
            },
        }));

        void Write(string path, byte[] content)
        {
            var file = Path.Combine(Feed, path);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, content);
        }
    }

    // As the add-in makes its own, with the RDCore.SDK version it is built with.
    private PlatformInstaller.PlatformInstaller Installer() => new(
        new PlatformInstallerOptions
        {
            Root = Path.Combine(folder, "installed"),
            Channel = ReleaseChannel.Preview,
            RuntimeIdentifier = "win-x64",
            SdkVersion = AddinSettings.SdkVersion,
        },
        new HttpClient(new DevelopmentFeed(Feed)));

    [TestMethod]
    public async Task A_release_in_the_folder_built_with_an_RDCore_SDK_the_add_in_isn_t_compatible_with_is_not_offered()
    {
        var sdk = AddinSettings.SdkVersion.Core;
        Publish("0.1.0-dev.1", sdk: sdk.Major == 0 ? $"0.{sdk.Minor + 1}.0" : $"{sdk.Major + 1}.0.0");

        Assert.IsNull(await Installer().Feed.FindLatestAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task A_release_in_the_folder_is_found_downloaded_checked_and_installed_as_a_published_one_is()
    {
        Publish("0.1.0-dev.1");
        var installer = Installer();

        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);
        var installed = await installer.InstallAsync(release, null, TestContext.CancellationToken);

        Assert.AreEqual("0.1.0-dev.1", installer.Current?.Version.Text);
        Assert.IsTrue(File.Exists(Path.Combine(installed.Root, "RDCore.LanguageServer", "RDCore.LanguageServer.exe")));
    }

    [TestMethod]
    public async Task A_download_from_the_folder_is_still_checked_against_its_checksums()
    {
        Publish("0.1.0-dev.1", digest: new string('0', 64));
        var installer = Installer();
        var release = await installer.Feed.FindLatestAsync(TestContext.CancellationToken);
        Assert.IsNotNull(release);

        var refusal = await Assert.ThrowsExactlyAsync<PlatformInstallException>(() =>
            installer.InstallAsync(release, null, TestContext.CancellationToken));

        Assert.AreEqual(InstallFailure.ChecksumMismatch, refusal.Failure);
    }

    [TestMethod]
    [DataRow("https://github.com/..%2F..%2Fsecret.txt")]
    [DataRow("https://github.com/%2E%2E/%2E%2E/secret.txt")]
    [DataRow("http://github.com/rubberduck-vba/RDCore/releases/download/v0.1.0-dev.1/SHA256SUMS")]
    [DataRow("https://github.com/rubberduck-vba/RDCore/releases/download/v0.1.0-dev.1/missing.zip")]
    [DataRow("https://github.com/rubberduck-vba/RDCore/releases/download/v0.1.0-dev.1/SHA256SUMS%00.zip")]
    public async Task What_isn_t_in_the_folder_over_https_is_not_found(string url)
    {
        Publish("0.1.0-dev.1");
        File.WriteAllText(Path.Combine(folder, "secret.txt"), "outside the feed");
        using var http = new HttpClient(new DevelopmentFeed(Feed));

        using var response = await http.GetAsync(url, TestContext.CancellationToken);

        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task A_request_s_query_is_left_out()
    {
        Publish("0.1.0-dev.1");
        using var http = new HttpClient(new DevelopmentFeed(Feed));

        using var response = await http.GetAsync("https://api.github.com/repos/rubberduck-vba/RDCore/releases?per_page=100",
            TestContext.CancellationToken);

        Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }
}
