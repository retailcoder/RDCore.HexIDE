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

    // Packs the add-in these tests are built with, as tools/Pack-Addin.ps1 packs an add-in already published, and reads its addin.json.
    private async Task<JsonElement> Pack()
    {
        var published = Path.Combine(folder, "published");
        Directory.CreateDirectory(published);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "RDCore.HexIDE.dll"), Path.Combine(published, "RDCore.HexIDE.dll"));
        var start = new ProcessStartInfo("pwsh")
        {
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-File", Path.Combine(Repository(), "tools", "Pack-Addin.ps1"),
                "-Published", published, "-Destination", Package,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken);
        Assert.AreEqual(0, process.ExitCode, $"{await output}{await error}");
        using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(Package, "addin.json"), TestContext.CancellationToken));
        return manifest.RootElement.Clone();
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
        var inPackage = Directory.EnumerateFiles(Package, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(Package, file).Replace('\\', '/'))
            .Where(file => file != "addin.json")
            .Order(StringComparer.Ordinal);
        CollectionAssert.AreEqual(inPackage.ToArray(),
            manifest.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal).ToArray());
    }
}
