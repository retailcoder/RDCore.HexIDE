using RDCore.SDK.Platform;

namespace RDCore.PlatformInstaller.Tests;

[TestClass]
public sealed class PlatformFolderTests : IDisposable
{
    private readonly string root = TempFolders.New("rdcore-folder-");

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void Write(string path, string text)
    {
        var file = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    [TestMethod]
    public void A_platform_folder_says_its_version_system_rdc_and_RDCore_SDK()
    {
        Write("rdcore.json", """{ "platformVersion": "0.1.0", "rid": "linux-x64", "langService": "RDCore.LanguageServer/RDCore.LanguageServer", "hostService": "RDCore.CLI/rdc" }""");
        Write("RDCore.LanguageServer/RDCore.LanguageServer", "");
        Write("release.json", FakeGitHub.ReleaseInfo("0.1.1-alpha"));

        var folder = PlatformFolder.Read(root);

        Assert.IsNotNull(folder);
        Assert.AreEqual("0.1.0", folder.Version);
        Assert.AreEqual("linux-x64", folder.RuntimeIdentifier);
        Assert.AreEqual(Path.Combine(Path.GetFullPath(root), "RDCore.CLI", "rdc"), folder.HostService);
        Assert.IsTrue(folder.HasReleaseInfo);
        Assert.AreEqual("0.1.1-alpha", folder.SdkVersion?.Text);
        Assert.IsTrue(folder.IsBuiltWith(PlatformVersion.Parse("0.1.0-alpha")));
        Assert.IsFalse(folder.IsBuiltWith(PlatformVersion.Parse("0.2.0")));
        Assert.IsTrue(folder.IsFor("linux-x64"));
        Assert.IsFalse(folder.IsFor("win-x64"));
    }

    [TestMethod]
    public void A_development_build_has_no_release_json_and_says_no_RDCore_SDK()
    {
        Write("rdcore.json", """{ "platformVersion": "0.1", "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""");
        Write("RDCore.LanguageServer/RDCore.LanguageServer.exe", "");

        var folder = PlatformFolder.Read(root);

        Assert.IsNotNull(folder);
        Assert.IsFalse(folder.HasReleaseInfo);
        Assert.IsNull(folder.SdkVersion);
        Assert.IsFalse(folder.IsBuiltWith(PlatformVersion.Parse("0.1.1-alpha")));
        Assert.IsTrue(folder.IsFor("win-x64"), "one that names no system is taken to be for this one");
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("no manifest")]
    [DataRow("no language server")]
    [DataRow("not json")]
    public void A_folder_that_isn_t_a_platform_is_none(string flaw)
    {
        if (flaw != "missing")
        {
            Directory.CreateDirectory(root);
        }

        if (flaw is "no language server" or "not json")
        {
            Write("rdcore.json", flaw == "not json" ? "{ not json" : """{ "langService": "RDCore.LanguageServer/RDCore.LanguageServer.exe" }""");
        }

        Assert.IsNull(PlatformFolder.Read(root));
    }
}
