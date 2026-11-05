namespace RDCore.PlatformInstaller;

/// <summary>A release's <c>SHA256SUMS</c>, in <c>sha256sum</c>'s format: a lowercase hex digest, two spaces, a file name.</summary>
internal static class Sha256Sums
{
    /// <summary>The digest the file lists for <paramref name="fileName"/>, or null when it lists none.</summary>
    public static string? Find(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var entry = line.TrimEnd('\r');
            var space = entry.IndexOf(' ');
            if (space != 64)
                continue;
            // sha256sum marks a file read in binary mode with '*' before its name.
            var name = entry[(space + 1)..].TrimStart(' ').TrimStart('*');
            if (name == fileName)
                return entry[..space].ToLowerInvariant();
        }
        return null;
    }
}
