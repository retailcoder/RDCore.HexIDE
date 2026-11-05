using System.Text.Json;
using HexIDE.Addins;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.HexIDE.Server;
using RDCore.SDK.Client;

namespace RDCore.HexIDE.Debugging;

/// <summary>
/// RDCore's debugger as a way to run an RDCore project in HexIDE, under Run &gt; F5 Runs and Run &gt; Start Once With: the
/// platform's debug adapter, started for the project's folder, which runs the program from its entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>The language server's platform.</b> It is found, or installed with the user's consent, as the server's is
/// (<see cref="PlatformSource"/>), when the user asks for a run. A run asked for while the user is asked shares the question
/// and its answer. Without a platform the run ends, and HexIDE says why.
/// </para>
/// <para>
/// <b>What runs is the adapter's to find.</b> The launch names no module and no entry point, so the adapter runs <c>Main</c>
/// of the one standard module that declares it, and refuses the run, with the reason, when none does or more than one does.
/// </para>
/// <para>
/// <b>Held until the add-in goes.</b> HexIDE doesn't tell an add-in when a session ends, so an installed version a run starts
/// from stays held until then (<see cref="PlatformInstaller.PlatformInstaller.Hold"/>), once per version. A running adapter
/// keeps its own folder in place meanwhile; the lease covers the start.
/// </para>
/// </remarks>
internal sealed class RDCoreDebugLaunch(PlatformSource platform) : IDisposable
{
    /// <summary>What the Run menu calls it.</summary>
    public const string Title = "RDCore";

    private readonly Dictionary<string, IDisposable> leases = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private bool disposed;

    /// <summary>The launch HexIDE is given: one for RDCore projects only.</summary>
    public AddinDebugLaunch Launch => new(Title)
    {
        Claims = project => RDCoreServer.IsRDCoreProject(project.ProjectPath),
        CreateConfigurationAsync = CreateConfigurationAsync,
    };

    /// <summary>The configuration for one run of <paramref name="project"/>.</summary>
    /// <exception cref="InvalidOperationException">There is no platform to run, or it can't be used; the message says why, in the user's language.</exception>
    /// <exception cref="OperationCanceledException">The run was ended before it started.</exception>
    public async Task<AddinLaunchConfiguration> CreateConfigurationAsync(AddinProjectInfo project, CancellationToken cancellationToken)
    {
        var choice = await platform.GetAsync(cancellationToken) ?? throw new InvalidOperationException(Strings.NoPlatformToRun);
        cancellationToken.ThrowIfCancellationRequested();

        RDCoreDebugAdapterLaunch launch;
        try
        {
            Hold(choice);
            launch = RDCoreDebugAdapterLaunch.Prepare(new RDCoreDebugAdapterLaunchOptions
            {
                PlatformRoot = choice.Root,
                Workspace = Path.GetDirectoryName(Path.GetFullPath(project.ProjectPath))!,
            });
        }
        catch (Exception ex)
        {
            Failures.Record(ex);
            throw new InvalidOperationException(Strings.PlatformRootUnusable(choice.Root, Failures.Unusable(ex)), ex);
        }

        return new AddinLaunchConfiguration
        {
            Executable = launch.ExecutablePath,
            Arguments = launch.Arguments,
            WorkingDirectory = launch.WorkingDirectory,
            Environment = launch.Environment.ToDictionary(variable => variable.Key, string? (variable) => variable.Value),
            AdapterId = RDCoreDebugAdapterLaunch.AdapterId,
            Launch = JsonSerializer.SerializeToElement(launch.LaunchArguments),
        };
    }

    private void Hold(PlatformChoice choice)
    {
        if (choice.Installed is not { } installed)
        {
            return;
        }

        lock (leases)
        {
            if (!disposed && !leases.ContainsKey(installed.Root))
            {
                leases[installed.Root] = PlatformInstaller.PlatformInstaller.Hold(installed);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (leases)
        {
            disposed = true;
            foreach (var lease in leases.Values)
            {
                lease.Dispose();
            }

            leases.Clear();
        }
    }
}
