namespace Kirimdong.Core;

public sealed record OutgoingFile(string FullPath, string RelativePath, long Size);

public sealed record OutgoingBatch(IReadOnlyList<OutgoingFile> Files, IReadOnlyList<string> Folders)
{
    public long TotalBytes => Files.Sum(f => f.Size);
    public bool IsEmpty => Files.Count == 0 && Folders.Count == 0;
}

/// <summary>Turns what the user picked or dropped (files and folders) into the list that goes on the wire.</summary>
public static class FileCollector
{
    public static OutgoingBatch Collect(IEnumerable<string> paths)
    {
        var files = new List<OutgoingFile>();
        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in paths)
        {
            var path = Path.GetFullPath(input);
            if (File.Exists(path))
            {
                var name = Path.GetFileName(path);
                if (seen.Add(name))
                    files.Add(new OutgoingFile(path, name, new FileInfo(path).Length));
            }
            else if (Directory.Exists(path))
            {
                var root = new DirectoryInfo(path);
                var baseName = root.Name;
                var parent = root.Parent?.FullName ?? root.FullName;
                folders.Add(baseName);
                foreach (var dir in root.EnumerateDirectories("*", Walk))
                    folders.Add(ToRelative(parent, dir.FullName));
                foreach (var file in root.EnumerateFiles("*", Walk))
                {
                    var rel = ToRelative(parent, file.FullName);
                    if (seen.Add(rel))
                        files.Add(new OutgoingFile(file.FullName, rel, file.Length));
                }
            }
        }
        return new OutgoingBatch(files, folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Skip what cannot be read instead of giving up on the whole folder (Documents holds locked
    /// "My Music"-style junctions), and never follow links, which can loop.
    /// </summary>
    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint,
    };

    private static string ToRelative(string parent, string full) =>
        Path.GetRelativePath(parent, full).Replace('\\', '/');
}
