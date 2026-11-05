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

    /// <summary>A burst of writes that never goes quiet is reread at the latest this long after its first write.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    // A file caught mid-write can't be read whole; it is read again, a few times, before the add-in gives up on that write.
    private const int Attempts = 5;

    private readonly object gate = new();
    private readonly string path;
    private readonly IAddinProjectChanges changes;
    private readonly TimeProvider time;
    private readonly ITimer settle;
    private FileSystemWatcher? watcher;
    private CancellationTokenRegistration closed;
    private RDProject? last;
    private DateTimeOffset? waitingSince;
    private int attempt;
    private bool disposed;

    /// <param name="path">The project file.</param>
    /// <param name="changes">Where HexIDE takes the changes.</param>
    /// <param name="time">The clock a write settles by.</param>
    public RDProjectWatcher(string path, IAddinProjectChanges changes, TimeProvider time)
    {
        this.path = Path.GetFullPath(path);
        this.changes = changes;
        this.time = time;
        settle = time.CreateTimer(_ => Reread(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised when the watcher stops: HexIDE closed the project, or the add-in goes.</summary>
    public event Action? Stopped;

    /// <summary>
    /// Starts following the file, before the project is first read (<see cref="Begin"/>) so that a write while HexIDE opens
    /// it isn't missed; stops at once when HexIDE has closed the project already.
    /// </summary>
    public void Start()
    {
        var files = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        files.Changed += (_, _) => Schedule();
        files.Created += (_, _) => Schedule();
        files.Renamed += (_, _) => Schedule();
        lock (gate)
        {
            if (disposed)
            {
                files.Dispose();
                return;
            }

            watcher = files;
        }
        files.EnableRaisingEvents = true;
        closed = changes.Closed.Register(Dispose);
    }

    /// <summary>The project as it was first read, which the changes after it are told against.</summary>
    public void Begin(RDProject read)
    {
        lock (gate)
        {
            last ??= read;
        }
    }

    /// <summary>
    /// Rereads the file once it has been quiet for <see cref="Settle"/>, or <see cref="MaxWait"/> after the first write it
    /// waits on: what the file system's reports of it do.
    /// </summary>
    internal void Schedule()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var now = time.GetUtcNow();
            waitingSince ??= now;
            var left = waitingSince.Value + MaxWait - now;
            settle.Change(left < TimeSpan.Zero ? TimeSpan.Zero : left < Settle ? left : Settle, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Reads the project file again and reports how it differs from the last read.</summary>
    /// <remarks>
    /// Nothing escapes it: it runs on a timer's thread, where an exception would end HexIDE. A change it fails to report is
    /// traced, and the watcher goes on from what it read.
    /// </remarks>
    internal void Reread()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            // This read covers every write before it.
            waitingSince = null;
            if (last is null)
            {
                // The first read is still going: what it finds is what this would have, so the reread waits for it.
                settle.Change(Settle, Timeout.InfiniteTimeSpan);
                return;
            }

            try
            {
                RDProject now;
                try
                {
                    now = RDProject.Read(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (++attempt < Attempts)
                    {
                        settle.Change(Settle, Timeout.InfiniteTimeSpan);
                    }
                    else
                    {
                        attempt = 0;
                        Trace.TraceWarning($"RDCore for HexIDE couldn't read {path} after it changed: {ex.Message}");
                    }
                    return;
                }
                attempt = 0;
                // What HexIDE was told stands even when it throws partway, so the next change isn't reported twice.
                var before = last;
                last = now;
                RDProjectChanges.Report(before, now, changes);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"RDCore for HexIDE couldn't report a change to {path}: {ex}");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }
        watcher?.Dispose();
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
        var was = before.Members.ToDictionary(member => member.Path, RDProject.PathComparer);
        var now = after.Members.ToDictionary(member => member.Path, RDProject.PathComparer);
        var removed = before.Members.Where(member => !now.TryGetValue(member.Path, out var same) || same.Kind != member.Kind).ToList();
        var added = after.Members.Where(member => !was.TryGetValue(member.Path, out var same) || same.Kind != member.Kind).ToList();

        foreach (var gone in removed.ToList())
        {
            if (gone.Digest is null || File.Exists(gone.Path))
            {
                continue;
            }

            if (added.FirstOrDefault(joined => joined.Kind == gone.Kind && joined.Digest == gone.Digest) is not { } moved)
            {
                continue;
            }

            changes.MemberMoved(gone.Path, moved.Path);
            removed.Remove(gone);
            added.Remove(moved);
        }
        foreach (var gone in removed)
        {
            changes.MemberRemoved(gone.Path);
        }

        foreach (var joined in added)
        {
            changes.MemberAdded(new AddinProjectMember(joined.Path, joined.Kind));
        }

        if (!before.References.SequenceEqual(after.References))
        {
            changes.ReferencesChanged(after.References);
        }
    }
}

/// <summary>
/// The project type HexIDE opens <c>.rdproj</c> files with, and the watchers of the projects it opened, which go with the
/// add-in.
/// </summary>
/// <param name="time">The clock its watchers settle writes by; the system's when null.</param>
internal sealed class RDCoreProjectType(TimeProvider? time = null) : IDisposable
{
    private readonly ConcurrentDictionary<RDProjectWatcher, byte> watchers = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;

    /// <summary>What the Open Project dialog calls the files.</summary>
    public const string Title = "RDCore projects";

    /// <summary>The type to register with HexIDE.</summary>
    public AddinProjectType Type => new(Title)
    {
        FileNames = [RDCore.SDK.Workspace.ProjectFile.FileName],
        OpenAsync = (path, changes, cancellationToken) => Task.Run(() =>
        {
            var watcher = new RDProjectWatcher(path, changes, time);
            watchers[watcher] = 0;
            watcher.Stopped += () => watchers.TryRemove(watcher, out _);
            try
            {
                watcher.Start();
                var project = RDProject.Read(path);
                watcher.Begin(project);
                return project.Content;
            }
            catch
            {
                watcher.Dispose();
                throw;
            }
        }, cancellationToken),
    };

    /// <summary>The projects being followed.</summary>
    public int Watching => watchers.Count;

    public void Dispose()
    {
        foreach (var watcher in watchers.Keys)
        {
            watcher.Dispose();
        }
    }
}
