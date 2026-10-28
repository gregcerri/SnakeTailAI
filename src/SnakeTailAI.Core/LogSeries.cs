namespace SnakeTailAI.Core;

/// <summary>
/// Rotated log sets: <c>viu.log</c> (active) with archives <c>viu.log.1</c> (newest) … <c>viu.log.N</c> (oldest).
/// </summary>
public static class LogSeries
{
    /// <summary>True for names like <c>viu.log.3</c>; gives the active file name and the archive number.</summary>
    public static bool IsArchiveName(string fileName, out string baseName, out int index)
    {
        baseName = fileName;
        index = 0;
        int dot = fileName.LastIndexOf('.');
        if (dot <= 0 || dot == fileName.Length - 1)
            return false;
        if (!int.TryParse(fileName.AsSpan(dot + 1), System.Globalization.NumberStyles.None, null, out index))
            return false;
        baseName = fileName[..dot];
        return true;
    }

    /// <summary>The active file for any member of a series (<c>viu.log.3</c> → <c>viu.log</c>).</summary>
    public static string GetActivePath(string path)
    {
        string name = Path.GetFileName(path);
        return IsArchiveName(name, out string baseName, out _)
            ? Path.Combine(Path.GetDirectoryName(path) ?? "", baseName)
            : path;
    }

    /// <summary>All files of the series, oldest first; the active file is last (even if it does not exist yet).</summary>
    public static IReadOnlyList<string> GetSeries(string path)
    {
        string active = GetActivePath(path);
        string dir = Path.GetDirectoryName(active) ?? ".";
        string activeName = Path.GetFileName(active);
        var archives = new List<(int Index, string Path)>();
        if (Directory.Exists(dir))
        {
            foreach (string file in Directory.EnumerateFiles(dir, activeName + ".*"))
            {
                if (IsArchiveName(Path.GetFileName(file), out string baseName, out int index) &&
                    string.Equals(baseName, activeName, StringComparison.OrdinalIgnoreCase))
                {
                    archives.Add((index, file));
                }
            }
        }
        archives.Sort((a, b) => b.Index.CompareTo(a.Index));
        var result = archives.Select(a => a.Path).ToList();
        result.Add(active);
        return result;
    }

    public static bool HasArchives(string path) => GetSeries(path).Count > 1;

    /// <summary>The live log of a device folder: the most recently written file that is not an archive.</summary>
    public static string? FindActiveLog(string folder)
    {
        if (!Directory.Exists(folder))
            return null;
        var files = new DirectoryInfo(folder).EnumerateFiles().ToList();
        var candidates = files.Where(f => !IsArchiveName(f.Name, out _, out _) && IsLogLike(f.Name)).ToList();
        if (candidates.Count == 0)
            candidates = files.Where(f => !IsArchiveName(f.Name, out _, out _)).ToList();
        return candidates.OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
    }

    public static bool IsLogLike(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".log" or ".txt" or ".out" or ".trace";
    }
}
