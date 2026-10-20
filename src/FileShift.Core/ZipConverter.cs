using System.IO.Compression;

namespace FileShift.Core;

public static class ZipConverter
{
    public static async Task<string> CreateAsync(IReadOnlyList<string> sources, string? outputDirectory, IProgress<double?>? progress, CancellationToken token)
    {
        if (sources.Count == 0) throw new ArgumentException("Select at least one item.");
        var entries = new List<(string Source, string Entry, long Length)>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("Archive input no longer exists.", full);
            CheckLink(full);
            var name = Path.GetFileName(full); var unique = name; var suffix = 2;
            while (!roots.Add(unique)) unique = $"{name} ({suffix++})";
            if (Directory.Exists(full)) Walk(full, unique);
            else entries.Add((full, unique, new FileInfo(full).Length));
        }
        void Walk(string directory, string entry)
        {
            token.ThrowIfCancellationRequested(); CheckLink(directory);
            entries.Add((directory, entry + "/", 0));
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); CheckLink(path);
                if (Directory.Exists(path)) Walk(path, entry + "/" + Path.GetFileName(path));
                else entries.Add((path, entry + "/" + Path.GetFileName(path), new FileInfo(path).Length));
                if (entries.Count > 100_000) throw new InvalidOperationException("ZIP batch exceeds the 100,000-entry limit.");
            }
        }
        var output = Path.GetFullPath(outputDirectory ?? Path.GetDirectoryName(Path.GetFullPath(sources[0]).TrimEnd(Path.DirectorySeparatorChar))!);
        Directory.CreateDirectory(output);
        var stage = Path.Combine(output, ".fileshift-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            var file = Path.Combine(stage, "archive.zip");
            var total = entries.Sum(e => e.Length); long written = 0;
            await using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                var buffer = new byte[1024 * 1024];
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var zipped = archive.CreateEntry(entry.Entry, CompressionLevel.Optimal);
                    if (entry.Entry.EndsWith('/')) continue;
                    await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
                    await using var destination = zipped.Open();
                    int count;
                    while ((count = await input.ReadAsync(buffer, token)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, count), token); written += count;
                        progress?.Report(total == 0 ? 0 : Math.Clamp(written * 100.0 / total, 0, 99));
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return Converter.Publish(file, output, sources.Count == 1 ? Path.GetFileNameWithoutExtension(sources[0].TrimEnd(Path.DirectorySeparatorChar)) : "Archive", "zip");
        }
        finally { try { Directory.Delete(stage, true); } catch (IOException) { } }
    }
    private static void CheckLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"ZIP cannot include links or cloud placeholders: {path}. Use downloaded regular files.");
    }
}
