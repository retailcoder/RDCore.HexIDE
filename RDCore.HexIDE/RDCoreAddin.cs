using System.Diagnostics;
using HexIDE.Addins;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Server;
using RDCore.PlatformInstaller;

namespace RDCore.HexIDE;

/// <summary>
/// RDCore for HexIDE: RDCore's language server while an RDCore project is open, with the platform it runs from installed on
/// the user's say-so.
/// </summary>
public sealed class RDCoreAddin : IAddin
{
    private IHexIdeHost? host;
    private HttpClient? http;
    private RDCoreServer? server;

    /// <inheritdoc/>
    public void Initialize(IHexIdeHost host)
    {
        this.host = host;
        var settings = AddinSettings.Load(AddinSettings.DefaultPath);
        http = new HttpClient();
        var installer = new PlatformInstaller.PlatformInstaller(
            new PlatformInstallerOptions { Root = PlatformInstallerOptions.DefaultRoot, Channel = settings.EffectiveChannel }, http);
        var prompts = new AvaloniaPrompts();
        server = new RDCoreServer(host, new PlatformSource(settings, installer, prompts), prompts);

        host.Events.ProjectLoaded += OnProjectLoaded;
        host.Events.ProjectUnloaded += OnProjectUnloaded;
        // Add-ins initialise once HexIDE is up, after any project it was started with has opened.
        foreach (var project in host.Project.GetProjects())
            Observe(server.ProjectLoadedAsync(project.ProjectPath));
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
        http?.Dispose();
    }
}
