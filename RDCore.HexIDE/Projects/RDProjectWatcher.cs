using System.Collections.Concurrent;
using System.Diagnostics;
using HexIDE.Addins;

namespace RDCore.HexIDE.Projects;

/// <summary>
/// Follows an open RDCore project's file, which RDCore's language server writes, and reports to HexIDE what changed in it:
/// members added, removed or moved, and references. Stops when HexIDE closes the project, or the add-in goes.
/// </summary>
internal sealed class RDProjectWatcher : IDisposable
{
    /// <summary>A write is reread once the file has been quiet this long, so a burst of writes is one report.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    // A file caught mid-write can't be read whole; it is read again, a few times, before the add-in gives up on that write.
    private const int Attempts = 5;

    private readonly object gate = new();
    private readonly string path;
    private readonly IAddinProjectChanges changes;
    private readonly FileSystemWatcher watcher;
    private readonly Timer settle;
    private readonly CancellationTokenRegistration closed;
    private RDProject last;
    private int attempt;
    private bool disposed;

    public RDProjectWatcher(string path, RDProject read, IAddinProjectChanges changes)
    {
        this.path = Path.GetFullPath(path);
        this.changes = changes;
        last = read;
        settle = new Timer(_ => Reread(), null, Timeout.Infinite, Timeout.Infinite);
        watcher = new FileSystemWatcher(Path.GetDirectoryName(this.path)!, Path.GetFileName(this.path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Changed += (_, _) => Schedule();
        watcher.Created += (_, _) => Schedule();
        watcher.Renamed += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
        closed = changes.Closed.Register(Dispose);
    }

    /// <summary>Raised when the watcher stops on its own.</summary>
    public event Action? Stopped;

    private void Schedule()
    {
        lock (gate)
        {
            if (!disposed)
                settle.Change(Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the project file again and reports how it differs from the last read.</summary>
    internal void Reread()
    {
        lock (gate)
        {
            if (disposed)
                return;
            RDProject now;
            try
            {
                now = RDProject.Read(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (++attempt < Attempts)
                    settle.Change(Settle, Timeout.InfiniteTimeSpan);
                else
                {
                    attempt = 0;
                    Trace.TraceWarning($"RDCore for HexIDE couldn't read {path} after it changed: {ex.Message}");
                }
                return;
            }
            attempt = 0;
            RDProjectChanges.Report(last, now, changes);
            last = now;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }
        watcher.Dispose();
        settle.Dispose();
        closed.Dispose();
        Stopped?.Invoke();
    }
}

/// <summary>How one read of a project differs from the one before, as HexIDE's change channel says it.</summary>
internal static class RDProjectChanges
{
    /// <summary>
    /// Reports what changed from <paramref name="before"/> to <paramref name="after"/>: moves first, then removals, then
    /// additions, so a module moved or replaced never meets its own name; then the references, when they changed.
    /// </summary>
    /// <remarks>
    /// A member that left and one that joined are the same member moved when the old file is gone and the new one has its
    /// content, byte for byte, and they are of one kind. A member whose kind changed is removed and added again.
    /// </remarks>
    public static void Report(RDProject before, RDProject after, IAddinProjectChanges changes)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var was = before.Members.ToDictionary(member => member.Path, comparer);
        var now = after.Members.ToDictionary(member => member.Path, comparer);
        var removed = before.Members.Where(member => !now.TryGetValue(member.Path, out var same) || same.Kind != member.Kind).ToList();
        var added = after.Members.Where(member => !was.TryGetValue(member.Path, out var same) || same.Kind != member.Kind).ToList();

        foreach (var gone in removed.ToList())
        {
            if (gone.Digest is null || File.Exists(gone.Path))
                continue;
            if (added.FirstOrDefault(joined => joined.Kind == gone.Kind && joined.Digest == gone.Digest) is not { } moved)
                continue;
            changes.MemberMoved(gone.Path, moved.Path);
            removed.Remove(gone);
            added.Remove(moved);
        }
        foreach (var gone in removed)
            changes.MemberRemoved(gone.Path);
        foreach (var joined in added)
            changes.MemberAdded(new AddinProjectMember(joined.Path, joined.Kind));
        if (!before.References.SequenceEqual(after.References))
            changes.ReferencesChanged(after.References);
    }
}

/// <summary>
/// The project type HexIDE opens <c>.rdproj</c> files with, and the watchers of the projects it opened, which go with the
/// add-in.
/// </summary>
internal sealed class RDCoreProjectType : IDisposable
{
    private readonly ConcurrentDictionary<RDProjectWatcher, byte> watchers = new();

    /// <summary>What the Open Project dialog calls the files.</summary>
    public const string Title = "RDCore projects";

    /// <summary>The type to register with HexIDE.</summary>
    public AddinProjectType Type => new(Title)
    {
        FileNames = [RDCore.SDK.Workspace.ProjectFile.FileName],
        OpenAsync = (path, changes, cancellationToken) => Task.Run(() =>
        {
            var project = RDProject.Read(path);
            var watcher = new RDProjectWatcher(path, project, changes);
            watchers[watcher] = 0;
            watcher.Stopped += () => watchers.TryRemove(watcher, out _);
            return project.Content;
        }, cancellationToken),
    };

    /// <summary>The projects being followed.</summary>
    public int Watching => watchers.Count;

    public void Dispose()
    {
        foreach (var watcher in watchers.Keys)
            watcher.Dispose();
    }
}
