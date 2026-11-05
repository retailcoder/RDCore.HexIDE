using System.Security.Cryptography;
using System.Text.Json;
using HexIDE.Addins;
using RDCore.SDK.Workspace;

namespace RDCore.HexIDE.Projects;

/// <summary>A member of an RDCore project as the add-in last read it: where it is, what it is, and what its file held.</summary>
/// <param name="Path">Its full path.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Digest">Its file's SHA-256 when it was read, or null when it couldn't be: how a move is told from a remove.</param>
internal sealed record RDProjectMember(string Path, AddinMemberKind Kind, string? Digest);

/// <summary>
/// An RDCore project file (<c>.rdproj</c>) read for HexIDE: the language server is its only writer, and the add-in only reads it.
/// </summary>
/// <param name="Name">The project's name.</param>
/// <param name="Members">Its modules and other files, in the order the file lists them.</param>
/// <param name="References">Its references, in priority order.</param>
/// <param name="Folders">Its folders, relative to the project file's folder.</param>
internal sealed record RDProject(string Name, IReadOnlyList<RDProjectMember> Members,
    IReadOnlyList<AddinProjectReference> References, IReadOnlyList<string> Folders)
{
    /// <summary>What HexIDE is given of it.</summary>
    public AddinProjectContent Content => new(Name)
    {
        Members = [.. Members.Select(member => new AddinProjectMember(member.Path, member.Kind))],
        References = References,
        Folders = Folders,
    };

    /// <summary>How the file system tells two paths apart: whatever their case on Windows, by their case elsewhere.</summary>
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reads the project file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">It isn't a project file.</exception>
    /// <exception cref="IOException">It can't be read.</exception>
    public static RDProject Read(string path)
    {
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        ProjectFile file;
        try
        {
            // Read whole first, so the platform's reader runs over memory and finishes before it returns.
            using var content = new MemoryStream(File.ReadAllBytes(path), writable: false);
            file = ProjectFile.ReadAsync(content, new Uri(root).AbsoluteUri).GetAwaiter().GetResult();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} isn't an RDCore project file: {ex.Message}", ex);
        }

        var project = file.ProjectInfo;
        var name = string.IsNullOrWhiteSpace(project.Name) ? System.IO.Path.GetFileName(root) : project.Name;
        // A file the project lists twice, under any spelling of its path, is one member: the first the file lists.
        var listed = new HashSet<string>(PathComparer);
        List<RDProjectMember> members = [];
        foreach (var module in project.Modules)
        {
            if (Within(root, module.RelativeUri) is { } full && listed.Add(full))
            {
                var (source, digest) = ReadMember(full);
                members.Add(new RDProjectMember(full, KindOf(module.Super, source), digest));
            }
        }
        foreach (var other in project.OtherFiles)
        {
            if (Within(root, other.RelativeUri) is { } full && listed.Add(full))
            {
                members.Add(new RDProjectMember(full, AddinMemberKind.Other, ReadMember(full).Digest));
            }
        }

        return new RDProject(name, members,
            [.. project.References.Select(reference => new AddinProjectReference(reference.Name, reference.Guid, reference.Major,
                reference.Minor, reference.AbsolutePath))],
            [.. project.Folders.Where(folder => Within(root, folder) is not null).Select(folder => folder.Replace('\\', '/'))]);
    }

    /// <summary>
    /// The full path of a path the project file gives relative to its folder, or null when it leads outside that folder: a
    /// project file never makes HexIDE read, or write on save, a file that isn't in its project.
    /// </summary>
    internal static string? Within(string root, string relative)
    {
        // A NUL is no path at all: Path.GetFullPath refuses it.
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0') || System.IO.Path.IsPathRooted(relative))
        {
            return null;
        }

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root,
            relative.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : null;
    }

    /// <summary>What a module is, by the platform's own rule (<see cref="ProjectModuleKinds"/>), as HexIDE names it.</summary>
    internal static AddinMemberKind KindOf(DocClassType? super, string? source) => ProjectModuleKinds.Of(super, source) switch
    {
        ProjectModuleKind.Document => AddinMemberKind.DocumentModule,
        ProjectModuleKind.Class => AddinMemberKind.ClassModule,
        _ => AddinMemberKind.StandardModule,
    };

    // Decoded as the environment host and the language server decode a module, so its kind is the one they find.
    private static (string? Source, string? Digest) ReadMember(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return (ProjectModuleKinds.ReadText(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // HexIDE says which member it couldn't read when it loads the project.
            return (null, null);
        }
    }
}
