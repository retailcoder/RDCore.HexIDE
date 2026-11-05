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
/// and its answer; a run after "Not now" asks again. A version that lost its language server isn't installed any more, as
/// far as the installer is concerned, so a run asks to download it again. Without a platform the run ends, and HexIDE says
/// why.
/// </para>
/// <para>
/// <b>What runs is the adapter's to find.</b> The launch names no module and no entry point, so the adapter runs <c>Main</c>
/// of the one standard module that declares it, and refuses the run, with the reason, when none does or more than one does.
/// </para>
/// <para>
/// <b>Nothing held.</b> A running adapter keeps its version in place itself: an installer renames a version out of the way
/// before it removes it, which fails while a process runs from it. Holding the version as well would keep it from being
/// deployed again when it breaks.
/// </para>
/// </remarks>
internal sealed class RDCoreDebugLaunch(PlatformSource platform)
{
    /// <summary>What the Run menu calls it.</summary>
    public const string Title = "RDCore";

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
        var choice = await platform.GetToRunAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        RDCoreDebugAdapterLaunch launch;
        try
        {
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
}
