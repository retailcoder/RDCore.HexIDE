using System.Globalization;
using HexIDE.Addins;
using NSubstitute;
using RDCore.HexIDE.Debugging;
using RDCore.HexIDE.Projects;

namespace RDCore.HexIDE.Tests;

[TestClass]
public class RDCoreAddinTests
{
    [TestMethod]
    public void The_add_in_contributes_the_rdproj_project_type_and_takes_it_out_when_it_goes()
    {
        var host = Substitute.For<IHexIdeHost>();
        host.Project.GetProjects().Returns([]);
        var registration = Substitute.For<IDisposable>();
        AddinProjectType? type = null;
        host.RegisterProjectType(Arg.Do<AddinProjectType>(registered => type = registered)).Returns(registration);
        var addin = new RDCoreAddin();

        addin.Initialize(host);

        Assert.IsNotNull(type);
        Assert.AreEqual(RDCoreProjectType.Title, type.Title);
        CollectionAssert.AreEqual(new[] { ".rdproj" }, type.FileNames.ToArray());
        addin.Dispose();
        registration.Received(1).Dispose();
    }

    [TestMethod]
    public void The_add_in_contributes_RDCore_s_debugger_as_a_way_to_run_and_takes_it_out_when_it_goes()
    {
        var host = Substitute.For<IHexIdeHost>();
        host.Project.GetProjects().Returns([]);
        var registration = Substitute.For<IDisposable>();
        AddinDebugLaunch? launch = null;
        host.RegisterDebugLaunch(Arg.Do<AddinDebugLaunch>(registered => launch = registered)).Returns(registration);
        var addin = new RDCoreAddin();

        addin.Initialize(host);

        Assert.IsNotNull(launch);
        Assert.AreEqual(RDCoreDebugLaunch.Title, launch.Title);
        Assert.IsTrue(launch.Claims(new AddinProjectInfo("Ledger", Path.Combine(Path.GetTempPath(), "Ledger", ".rdproj"), [])));
        addin.Dispose();
        registration.Received(1).Dispose();
    }

    [TestMethod]
    [DataRow("en", "RDCore projects")]
    [DataRow("fr-CA", "Projets RDCore")]
    public void The_project_type_is_named_in_the_user_s_language(string culture, string title)
    {
        var was = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            Assert.AreEqual(title, RDCoreProjectType.Title);
        }
        finally
        {
            CultureInfo.CurrentUICulture = was;
        }
    }
}
