using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
internal sealed partial record RDProject(string Name, IReadOnlyList<RDProjectMember> Members,
    IReadOnlyList<AddinProjectReference> References, IReadOnlyList<string> Folders)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>What HexIDE is given of it.</summary>
    public AddinProjectContent Content => new(Name)
    {
        Members = [.. Members.Select(member => new AddinProjectMember(member.Path, member.Kind))],
        References = References,
        Folders = Folders,
    };

    /// <summary>Reads the project file at <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">It isn't a project file.</exception>
    /// <exception cref="IOException">It can't be read.</exception>
    public static RDProject Read(string path)
    {
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        ProjectFile file;
        try
        {
            file = JsonSerializer.Deserialize<ProjectFile>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException($"{path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} isn't an RDCore project file: {ex.Message}", ex);
        }

        var project = file.ProjectInfo;
        var name = string.IsNullOrWhiteSpace(project.Name) ? System.IO.Path.GetFileName(root) : project.Name;
        List<RDProjectMember> members = [];
        foreach (var module in project.Modules)
        {
            if (Within(root, module.RelativeUri) is { } full)
            {
                var (source, digest) = ReadMember(full);
                members.Add(new RDProjectMember(full, KindOf(module.Super, source), digest));
            }
        }
        foreach (var other in project.OtherFiles)
        {
            if (Within(root, other.RelativeUri) is { } full)
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
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative))
        {
            return null;
        }

        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root,
            relative.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar)));
        var prefix = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(prefix, comparison) ? full : null;
    }

    /// <summary>
    /// What a module is, by RDCore's own rule (<c>ModuleKinds.Of</c> in RDCore.SDK): a document module when the project says whose
    /// document it is, whatever its text; otherwise a class module when its text has a <c>VERSION</c> header, and a standard
    /// module when it hasn't, or can't be read.
    /// </summary>
    internal static AddinMemberKind KindOf(DocClassType? super, string? source) =>
        super is not null ? AddinMemberKind.DocumentModule
        : source is { Length: > 0 } && VersionHeader().IsMatch(source) ? AddinMemberKind.ClassModule
        : AddinMemberKind.StandardModule;

    // RDCore.SDK's ModuleHeader.IsClassModule: the VERSION line VBA writes as a class or designer module's first line.
    [GeneratedRegex(@"^[ \t]*VERSION[ \t]+[0-9]+(?:\.[0-9]+)?(?:[ \t]+CLASS)?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionHeader();

    // Read as Latin-1, which loses no byte: the header is ASCII whatever the module's code page.
    private static (string? Source, string? Digest) ReadMember(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return (Encoding.Latin1.GetString(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // HexIDE says which member it couldn't read when it loads the project.
            return (null, null);
        }
    }
}
