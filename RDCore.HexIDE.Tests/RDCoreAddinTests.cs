using HexIDE.Addins;
using NSubstitute;
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
}
