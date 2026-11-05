using HexIDE.Addins;
using HexIDE.Lsp;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;
using RDCore.SDK.Client;

namespace RDCore.HexIDE.Server;

/// <summary>
/// RDCore's language server in HexIDE, registered only while an RDCore project (<c>.rdproj</c>) is open: when the first one
/// opens, and taken out when the last one closes. HexIDE's own client keeps the connection; the add-in says hello on it
/// (<c>rdcore/platform/initialize</c>) after each start.
/// </summary>
/// <remarks>
/// HexIDE fixes a server's launch when it is registered, so the platform it runs from is found, or installed with the user's
/// consent, first (<see cref="PlatformSource"/>). An RDCore project opened while that is under way joins the same
/// registration.
/// </remarks>
internal sealed class RDCoreServer(IHexIdeHost host, PlatformSource platform, IUserPrompts prompts) : IDisposable
{
    /// <summary>The registration's id in HexIDE's Language Servers window.</summary>
    public const string Id = "rdcore";

    /// <summary>A start that has to deploy the platform again may download it whole, which takes a while on a slow link.</summary>
    public static readonly TimeSpan BeforeLaunchTimeout = TimeSpan.FromMinutes(15);

    private readonly HashSet<string> openProjects = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private CancellationTokenSource? registering;
    private IAddinServerConnection? connection;
    private IDisposable? lease;
    private bool disposed;

    /// <summary>The connection while the server is registered; null otherwise.</summary>
    public IAddinServerConnection? Connection => connection;

    /// <summary>Whether <paramref name="projectPath"/> is an RDCore project's file.</summary>
    public static bool IsRDCoreProject(string projectPath) =>
        string.Equals(Path.GetFileName(projectPath), ".rdproj", StringComparison.OrdinalIgnoreCase);

    /// <summary>A project opened: the first RDCore project registers the server.</summary>
    public Task ProjectLoadedAsync(string projectPath)
    {
        if (disposed || !IsRDCoreProject(projectPath) || !openProjects.Add(projectPath) || openProjects.Count > 1)
        {
            return Task.CompletedTask;
        }

        return RegisterAsync(projectPath);
    }

    /// <summary>A project closed: the last RDCore project to close takes the server out.</summary>
    public void ProjectUnloaded(string projectPath)
    {
        if (!openProjects.Remove(projectPath) || openProjects.Count > 0)
        {
            return;
        }

        TakeOut();
    }

    private async Task RegisterAsync(string projectPath)
    {
        var attempt = registering = new CancellationTokenSource();
        var choice = await platform.GetAsync(attempt.Token);
        // Closed while the platform was found or installed: there is nothing to register for.
        if (choice is null || attempt.IsCancellationRequested || openProjects.Count == 0)
        {
            return;
        }

        RDCoreLanguageServerLaunch launch;
        try
        {
            // Held while the server is registered, so no installer removes the version it runs from.
            lease = choice.Installed is { } installed ? PlatformInstaller.PlatformInstaller.Hold(installed) : null;
            launch = RDCoreLanguageServerLaunch.Prepare(new RDCoreLanguageServerLaunchOptions
            {
                PlatformRoot = choice.Root,
                Workspace = new Uri(Path.GetDirectoryName(Path.GetFullPath(projectPath)) + Path.DirectorySeparatorChar).AbsoluteUri,
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            ReleaseLease();
            await prompts.TellAsync(Strings.PlatformRootUnusable(choice.Root, ex.Message));
            return;
        }

        var channel = new ConnectionChannel();
        var client = new RDCorePlatformClient(channel);
        try
        {
            connection = host.RegisterLanguageServer(new AddinLanguageServer(Entry(launch))
            {
                BeforeLaunch = token => platform.EnsureStillThereAsync(choice, token),
                BeforeLaunchTimeout = BeforeLaunchTimeout,
                AfterStart = async (current, token) =>
                {
                    channel.Connect(current);
                    await client.InitializeAsync(CoreServerComponent.LanguageServer,
                        new CorePlatformClientCapabilities { LanguageServer = new() }, token);
                },
                MessagePrefix = "rdcore/",
                ServerMethods = channel.ServerMethods,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // The entry was refused, or another server has the id: HexIDE's Language Servers window says which too.
            ReleaseLease();
            await prompts.TellAsync(Strings.ServerNotRegistered(ex.Message));
        }
    }

    private void ReleaseLease()
    {
        lease?.Dispose();
        lease = null;
    }

    /// <summary>
    /// The entry HexIDE launches the server from. The pipe, HexIDE's process and the workspace are HexIDE's to fill in at each
    /// start, so the workspace the server is told is the one HexIDE's client names in <c>initialize</c>.
    /// </summary>
    internal static LanguageServerEntry Entry(RDCoreLanguageServerLaunch launch) => new()
    {
        Id = Id,
        DisplayName = "RDCore",
        Extensions = [".bas", ".cls", ".frm", ".doccls"],
        LanguageId = "vba",
        Transport = "pipe",
        PipeName = launch.PipeName,
        PipeRole = "connect",
        Command = launch.ExecutablePath,
        Arguments = new ServerArguments(null, ["--pipe-name", "{pipe}", "--workspace", "{workspaceUri}", "--client-process-id", "{pid}"]),
        Environment = new Dictionary<string, string>(launch.Environment),
        WorkingDirectory = launch.WorkingDirectory,
        Priority = 10,
    };

    private void TakeOut()
    {
        registering?.Cancel();
        registering = null;
        connection?.Dispose();
        connection = null;
        ReleaseLease();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        disposed = true;
        openProjects.Clear();
        TakeOut();
    }
}
