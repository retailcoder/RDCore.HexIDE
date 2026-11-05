using System.Collections.Concurrent;
using System.Diagnostics;
using HexIDE.Addins;
using RDCore.HexIDE.Platform;
using RDCore.HexIDE.Resources;

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
    // Rereads report one at a time, in order, outside the gate: HexIDE may take a report on the thread that closes the
    // project, and a close waits for the gate.
    private readonly object reporting = new();
    private readonly string path;
    private readonly IAddinProjectChanges changes;
    private readonly IAddinProjectChanges whileOpen;
    private readonly TimeProvider time;
    private readonly Action<string> tell;
    private readonly ITimer settle;
    private FileSystemWatcher? files;
    private FileSystemWatcher? folder;
    private CancellationTokenRegistration closed;
    private RDProject? last;
    private DateTimeOffset? waitingSince;
    private int attempt;
    private bool told;
    private bool disposed;

    /// <param name="path">The project file.</param>
    /// <param name="changes">Where HexIDE takes the changes.</param>
    /// <param name="time">The clock a write settles by.</param>
    /// <param name="tell">Tells the user what they need to know: that the file is gone, or can't be read.</param>
    public RDProjectWatcher(string path, IAddinProjectChanges changes, TimeProvider time, Action<string> tell)
    {
        this.path = Path.GetFullPath(path);
        this.changes = changes;
        whileOpen = new WhileOpen(this, changes);
        this.time = time;
        this.tell = tell;
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
        Follow();
        closed = changes.Closed.Register(Dispose);
    }

    /// <summary>Whether the file system reports changes to the project's folder.</summary>
    internal bool IsFollowing
    {
        get
        {
            lock (gate)
            {
                return files is { EnableRaisingEvents: true };
            }
        }
    }

    // Starts the file system's watchers, or starts them again in place of the ones there were.
    private void Follow()
    {
        var root = Path.GetDirectoryName(path)!;
        // The whole folder: the project file, and the modules it lists before they are written.
        var below = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.CreationTime,
        };
        below.Changed += (_, e) => Noticed(e.FullPath);
        below.Created += (_, e) => Noticed(e.FullPath);
        below.Deleted += (_, e) => Noticed(e.FullPath);
        below.Renamed += (_, e) =>
        {
            Noticed(e.OldFullPath);
            Noticed(e.FullPath);
        };
        below.Error += (_, e) => Lost(e.GetException());
        // The folder itself, which a rename or a delete takes the project file away with, and which may come back.
        FileSystemWatcher? around = null;
        if (Path.GetDirectoryName(root) is { } parent)
        {
            around = new FileSystemWatcher(parent, Path.GetFileName(root)) { NotifyFilter = NotifyFilters.DirectoryName };
            around.Renamed += (_, e) =>
            {
                if (RDProject.PathComparer.Equals(e.FullPath, root))
                {
                    FolderBack();
                }
                else
                {
                    Schedule();
                }
            };
            around.Deleted += (_, _) => Schedule();
            around.Created += (_, _) => FolderBack();
            around.Error += (_, e) => Lost(e.GetException());
        }

        FileSystemWatcher? oldBelow;
        FileSystemWatcher? oldAround;
        lock (gate)
        {
            if (disposed)
            {
                below.Dispose();
                around?.Dispose();
                return;
            }

            (oldBelow, oldAround) = (files, folder);
            (files, folder) = (below, around);
        }
        oldBelow?.Dispose();
        oldAround?.Dispose();
        below.EnableRaisingEvents = true;
        if (around is not null)
        {
            around.EnableRaisingEvents = true;
        }
    }

    // Starts the watchers again, which an error turned off or a folder that went took with it, and rereads the project
    // for what they missed; tells the user when they can't be.
    private void FollowAgain()
    {
        try
        {
            Follow();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"RDCore for HexIDE can no longer follow {path}: {ex.Message}");
            if (IsOpen)
            {
                Tell(Strings.ProjectNotFollowed(path));
            }
            return;
        }
        Schedule();
    }

    /// <summary>What the project's folder coming back does: it is followed again, and the project reread.</summary>
    internal void FolderBack() => FollowAgain();

    /// <summary>
    /// What the file system's report of a change to <paramref name="changed"/> does: rereads the project when that is the
    /// project file, or a module the project lists that couldn't be read when it last was.
    /// </summary>
    internal void Noticed(string changed)
    {
        lock (gate)
        {
            if (RDProject.PathComparer.Equals(changed, path)
                || last?.Members.Any(member => member.Digest is null && RDProject.PathComparer.Equals(member.Path, changed)) == true)
            {
                Schedule();
            }
        }
    }

    /// <summary>
    /// What an error of the file system's watcher does. An overflow lost reports, and the reread finds whatever they said; any
    /// other error turned the watcher off, so it is started again too.
    /// </summary>
    internal void Lost(Exception? error)
    {
        Trace.TraceWarning($"RDCore for HexIDE missed changes in the folder of {path}, and reads it again: {error?.Message}");
        if (error is InternalBufferOverflowException)
        {
            Schedule();
        }
        else
        {
            FollowAgain();
        }
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
    /// traced, and the watcher goes on from what it read. A file it can't read after a few attempts, or that is gone, is told
    /// to the user once, until it is read again.
    /// </remarks>
    internal void Reread()
    {
        lock (reporting)
        {
            string? message;
            (RDProject Before, RDProject After)? change;
            lock (gate)
            {
                message = RereadLocked(out change);
            }

            if (change is var (before, after))
            {
                try
                {
                    RDProjectChanges.Report(before, after, whileOpen);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"RDCore for HexIDE couldn't report a change to {path}: {ex}");
                }
            }

            if (message is not null)
            {
                Tell(message);
            }
        }
    }

    private void Tell(string message)
    {
        try
        {
            tell(message);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RDCore for HexIDE couldn't tell the user \"{message}\": {ex}");
        }
    }

    // What to tell the user, if anything, and the change to report.
    private string? RereadLocked(out (RDProject Before, RDProject After)? change)
    {
        change = null;
        if (disposed)
        {
            return null;
        }

        // This read covers every write before it.
        waitingSince = null;
        if (last is null)
        {
            // The first read is still going: what it finds is what this would have, so the reread waits for it.
            settle.Change(Settle, Timeout.InfiniteTimeSpan);
            return null;
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
                    return null;
                }

                attempt = 0;
                Trace.TraceWarning($"RDCore for HexIDE couldn't read {path} after it changed: {ex.Message}");
                if (told)
                {
                    return null;
                }

                told = true;
                return File.Exists(path) ? Strings.ProjectFileUnreadable(path) : Strings.ProjectFileGone(path);
            }
            attempt = 0;
            told = false;
            // What HexIDE is told stands even when it throws partway, so the next change isn't reported twice.
            change = (last, now);
            last = now;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RDCore for HexIDE couldn't read {path} after it changed: {ex}");
        }
        return null;
    }

    private bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return !disposed;
            }
        }
    }

    // HexIDE's change channel while the project is followed: a reread that ends after HexIDE closed the project reports
    // nothing more into it.
    private sealed class WhileOpen(RDProjectWatcher watcher, IAddinProjectChanges changes) : IAddinProjectChanges
    {
        public CancellationToken Closed => changes.Closed;

        public void MemberAdded(AddinProjectMember member)
        {
            if (watcher.IsOpen)
            {
                changes.MemberAdded(member);
            }
        }

        public void MemberRemoved(string path)
        {
            if (watcher.IsOpen)
            {
                changes.MemberRemoved(path);
            }
        }

        public void MemberMoved(string oldPath, string newPath)
        {
            if (watcher.IsOpen)
            {
                changes.MemberMoved(oldPath, newPath);
            }
        }

        public void ReferencesChanged(IReadOnlyList<AddinProjectReference> references)
        {
            if (watcher.IsOpen)
            {
                changes.ReferencesChanged(references);
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
        files?.Dispose();
        folder?.Dispose();
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
/// <param name="prompts">What tells the user that a project's file is gone, or can't be read.</param>
/// <param name="time">The clock its watchers settle writes by; the system's when null.</param>
internal sealed class RDCoreProjectType(IUserPrompts prompts, TimeProvider? time = null) : IDisposable
{
    private readonly ConcurrentDictionary<RDProjectWatcher, byte> watchers = new();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private bool disposed;

    // The watcher tells from its timer's thread and doesn't wait for the user, so a failure to show it is traced here.
    private void Tell(string message) => prompts.TellAsync(message).ContinueWith(
        failed => Trace.TraceError($"RDCore for HexIDE couldn't tell the user \"{message}\": {failed.Exception}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    /// <summary>What the Open Project dialog calls the files, in the user's language when the add-in starts.</summary>
    public static string Title => Strings.ProjectTypeTitle;

    /// <summary>The type to register with HexIDE.</summary>
    public AddinProjectType Type => new(Title)
    {
        FileNames = [RDCore.SDK.Workspace.ProjectFile.FileName],
        OpenAsync = (path, changes, cancellationToken) => Task.Run(() =>
        {
            RDProjectWatcher watcher;
            lock (watchers)
            {
                // The add-in is going: a project opened now would be followed by a watcher nothing stops.
                ObjectDisposedException.ThrowIf(disposed, this);
                watcher = new RDProjectWatcher(path, changes, time, Tell);
                watchers[watcher] = 0;
            }
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
        lock (watchers)
        {
            disposed = true;
        }
        foreach (var watcher in watchers.Keys)
        {
            watcher.Dispose();
        }
    }
}
