namespace Kirimdong.Core;

/// <summary>
/// Paths in an offer come from another machine. They are never trusted: every one is reduced to a
/// relative path made of clean segments, and the final location is checked to stay inside the save folder.
/// </summary>
public static class PathSafety
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Returns the path as clean segments, or null when nothing safe is left.</summary>
    public static string[]? SanitizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var invalid = Path.GetInvalidFileNameChars();
        var segments = new List<string>();
        foreach (var raw in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var chars = raw.Select(c => invalid.Contains(c) || c == ':' ? '_' : c).ToArray();
            var segment = new string(chars).Trim().TrimEnd('.');
            if (segment.Length == 0 || segment == "." || segment == "..") continue;
            if (segment.All(c => c == '.')) continue;
            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem)) segment = "_" + segment;
            if (segment.Length > 120) segment = segment[..120];
            segments.Add(segment);
        }
        if (segments.Count == 0 || segments.Count > 32) return null;
        return segments.ToArray();
    }

    /// <summary>Resolves an untrusted relative path under <paramref name="root"/>; throws if it would escape.</summary>
    public static string Resolve(string root, string relative)
    {
        var segments = SanitizeRelative(relative) ?? throw new InvalidDataException($"Unsafe path '{relative}'.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(segments).ToArray()));
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path '{relative}' leaves the save folder.");
        return full;
    }

    /// <summary>"foto.jpg" → "foto (1).jpg" while the name is taken.</summary>
    public static string MakeUnique(string path, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (!exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
    }
}
