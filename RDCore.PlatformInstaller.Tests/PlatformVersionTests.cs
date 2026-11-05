namespace RDCore.PlatformInstaller.Tests;

[TestClass]
public class PlatformVersionTests
{
    [TestMethod]
    public void Versions_are_ordered_as_semantic_versioning_orders_them()
    {
        // Semantic Versioning 2.0's own example, then releases.
        string[] ascending =
        [
            "0.1.0-alpha", "0.1.0-alpha.1", "0.1.0-alpha.beta", "0.1.0-beta", "0.1.0-beta.2", "0.1.0-beta.11", "0.1.0-rc.1",
            "0.1.0", "0.2.0", "0.10.0", "1.0.0",
        ];

        var sorted = ascending.Reverse().Select(PlatformVersion.Parse).Order().Select(version => version.Text).ToArray();

        CollectionAssert.AreEqual(ascending, sorted);
    }

    [TestMethod]
    public void A_tag_s_v_and_build_metadata_are_not_part_of_the_version()
    {
        var version = PlatformVersion.Parse("v0.2.0-alpha.3+4d9d4ba");

        Assert.AreEqual("0.2.0-alpha.3", version.Text);
        Assert.IsTrue(version.IsPrerelease);
        Assert.AreEqual(PlatformVersion.Parse("0.2.0-alpha.3"), version);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("0.1")]
    [DataRow("0.1.0.0")]
    [DataRow("a.b.c")]
    [DataRow("0.1.0-")]
    [DataRow("0.1.0-alpha..1")]
    [DataRow("latest")]
    public void What_is_not_a_version_is_refused(string text)
    {
        Assert.IsFalse(PlatformVersion.TryParse(text, out _));
    }
}
