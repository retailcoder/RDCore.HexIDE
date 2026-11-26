using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RDCore.HexIDE.Tests;

[TestClass]
public sealed class PackAddinTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rdcore-pack-" + Guid.NewGuid().ToString("N"));

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

    private string Package => Path.Combine(folder, "package");

    // The repository's root: the first folder above the tests' output that holds the solution.
    private static string Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RDCore.HexIDE.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new InvalidOperationException($"No RDCore.HexIDE.slnx above {AppContext.BaseDirectory}.");
    }

    private string Published => Path.Combine(folder, "published");

    // A copy of the add-in's own build output, beside these tests' in the same configuration: every assembly it runs with but
    // HexIDE's, as a publish has them.
    private string PublishedAddin()
    {
        var repository = Repository();
        var output = Path.Combine(repository, "RDCore.HexIDE",
            Path.GetRelativePath(Path.Combine(repository, "RDCore.HexIDE.Tests"), AppContext.BaseDirectory));
        foreach (var file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Published, Path.GetRelativePath(output, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return Published;
    }

    // Packs the add-in these tests are built with, as tools/Pack-Addin.ps1 packs an add-in already published, and reads its addin.json.
    private async Task<JsonElement> Pack()
    {
        PublishedAddin();
        var (exitCode, output) = await RunPack();
        Assert.AreEqual(0, exitCode, output);
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Package, "addin.json"), TestContext.CancellationToken));
        return manifest.RootElement.Clone();
    }

    private async Task<(int ExitCode, string Output)> RunPack()
    {
        var start = new ProcessStartInfo("pwsh")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-File", Path.Combine(Repository(), "tools", "Pack-Addin.ps1"),
                "-Published", Published, "-Destination", Package,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Plain text, so the output a test reads is the message as written.
        start.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        return (process.ExitCode, $"{await output}{await error}");
    }

    private IEnumerable<string> PackageFiles() => Directory.EnumerateFiles(Package, "*", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(Package, file).Replace('\\', '/'));

    [TestMethod]
    public async Task The_package_carries_every_assembly_the_add_in_runs_with_but_the_ones_HexIDE_shares()
    {
        await Pack();

        var files = PackageFiles().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The platform's protocol and its installer: without them, the add-in registers no server.
        foreach (var carried in new[] { "RDCore.HexIDE.dll", "RDCore.SDK.Protocols.dll", "RDCore.PlatformInstaller.dll" })
        {
            Assert.Contains(carried, files);
        }
        // The SDK and its language-server stack are the platform's: the protocol needs none of them.
        foreach (var platforms in new[] { "RDCore.SDK.dll", "RDCore.SDK.Protocol.dll", "OmniSharp.Extensions.LanguageProtocol.dll",
            "OmniSharp.Extensions.JsonRpc.dll", "MediatR.dll", "System.Reactive.dll" })
        {
            Assert.DoesNotContain(platforms, files);
        }
        // HexIDE's contract, Avalonia, and StreamJsonRpc with what it pulls in: HexIDE's own are loaded.
        foreach (var shared in new[]
            { "HexIDE.Core.dll", "Avalonia.Base.dll", "StreamJsonRpc.dll", "Nerdbank.Streams.dll", "Newtonsoft.Json.dll" })
        {
            Assert.DoesNotContain(shared, files);
        }
    }

    [TestMethod]
    public async Task An_assembly_the_add_in_needs_that_is_in_neither_the_package_nor_HexIDE_fails_the_pack_by_name()
    {
        File.Delete(Path.Combine(PublishedAddin(), "RDCore.SDK.Protocols.dll"));

        var (exitCode, output) = await RunPack();

        Assert.AreNotEqual(0, exitCode, output);
        Assert.Contains("RDCore.HexIDE needs RDCore.SDK.Protocols", output);
        Assert.IsFalse(Directory.Exists(Package), "nothing is packed");
    }

    [TestMethod]
    public async Task The_package_carries_the_duck_as_its_logo_and_lists_it_with_its_hash()
    {
        var manifest = await Pack();

        Assert.AreEqual("ducky.png", manifest.GetProperty("logoPath").GetString());
        var logo = Path.Combine(Package, "ducky.png");
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(Repository(), "assets", "ducky.png"), TestContext.CancellationToken),
            await File.ReadAllBytesAsync(logo, TestContext.CancellationToken));
        var listed = manifest.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("path").GetString() == "ducky.png");
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(logo, TestContext.CancellationToken))),
            listed.GetProperty("sha256").GetString());
    }

    [TestMethod]
    public async Task The_manifest_names_the_add_in_with_its_description_and_author_and_lists_every_file_of_the_package()
    {
        var manifest = await Pack();

        Assert.AreEqual(2, manifest.GetProperty("schema").GetInt32());
        Assert.AreEqual("rdcore", manifest.GetProperty("id").GetString());
        Assert.AreEqual("RDCore for HexIDE", manifest.GetProperty("title").GetString());
        Assert.AreEqual("RDCore's language server and runtime for RDCore projects (.rdproj).", manifest.GetProperty("description").GetString());
        Assert.AreEqual("9562-7303 Québec inc.", manifest.GetProperty("author").GetString());
        Assert.AreEqual("RDCore.HexIDE.dll", manifest.GetProperty("entry").GetString());
        var inPackage = PackageFiles().Where(file => file != "addin.json").Order(StringComparer.Ordinal);
        CollectionAssert.AreEqual(inPackage.ToArray(),
            manifest.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal).ToArray());
    }
}
