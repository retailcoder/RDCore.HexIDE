using System.Diagnostics;
using HexIDE.Addins;
using RDCore.HexIDE.Debugging;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Projects;
using RDCore.HexIDE.Server;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE;

/// <summary>
/// RDCore for HexIDE: RDCore's language server while an RDCore project is open, and its debugger as a way to run one, with the
/// platform they run from installed on the user's say-so.
/// </summary>
public sealed class RDCoreAddin : IAddin
{
    private IHexIdeHost? host;
    private HttpClient? http;
    private RDCoreServer? server;
    private RDCoreProjectType? projectType;
    private IDisposable? projectTypeRegistration;
    private IDisposable? debugLaunchRegistration;

    /// <inheritdoc/>
    public void Initialize(IHexIdeHost host)
    {
        this.host = host;
        var prompts = new AvaloniaPrompts();
        // HexIDE opens a .rdproj through the add-in, as a project whose file RDCore's language server writes.
        projectType = new RDCoreProjectType(prompts);
        projectTypeRegistration = host.RegisterProjectType(projectType.Type);

        var settings = AddinSettings.Load(AddinSettings.DefaultPath);
        http = settings.DevelopmentFeed is { Length: > 0 } feed ? new HttpClient(new DevelopmentFeed(feed)) : new HttpClient();
        var installer = new PlatformInstaller.PlatformInstaller(
            new PlatformInstallerOptions
            {
                Root = PlatformInstallerOptions.DefaultRoot,
                Channel = settings.EffectiveChannel,
                SdkVersion = AddinSettings.SdkVersion,
            }, http);
        // One source, so the server and a run share the one question and its answer.
        var platform = new PlatformSource(settings, installer, prompts);
        server = new RDCoreServer(host, platform, prompts);
        debugLaunchRegistration = host.RegisterDebugLaunch(new RDCoreDebugLaunch(platform).Launch);

        host.Events.ProjectLoaded += OnProjectLoaded;
        host.Events.ProjectUnloaded += OnProjectUnloaded;
        // Add-ins initialise once HexIDE is up, after any project it was started with has opened.
        foreach (var project in host.Project.GetProjects())
        {
            Observe(server.ProjectLoadedAsync(project.ProjectPath));
        }
    }

    private void OnProjectLoaded(AddinProjectEventArgs project) => Observe(server?.ProjectLoadedAsync(project.ProjectPath));

    private void OnProjectUnloaded(AddinProjectEventArgs project) => server?.ProjectUnloaded(project.ProjectPath);

    // HexIDE raises its events without awaiting them, so a failure is logged here rather than lost.
    private static void Observe(Task? task) => task?.ContinueWith(
        failed => Trace.TraceError($"RDCore for HexIDE: {failed.Exception}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (host is not null)
        {
            host.Events.ProjectLoaded -= OnProjectLoaded;
            host.Events.ProjectUnloaded -= OnProjectUnloaded;
        }
        server?.Dispose();
        debugLaunchRegistration?.Dispose();
        projectTypeRegistration?.Dispose();
        projectType?.Dispose();
        http?.Dispose();
    }
}
