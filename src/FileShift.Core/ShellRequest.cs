using System.Text.Json;

namespace FileShift.Core;

public sealed record ShellRequest(string? Target, string[] Files)
{
    public static string RequestsDirectory => Path.Combine(Settings.DataDirectory, "Requests");
    public static ShellRequest ReadAndDelete(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), RequestsDirectory, StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(path) != ".json" || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
            throw new InvalidDataException("Unrecognized Explorer request path.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("Explorer request is too large.");
            var request = JsonSerializer.Deserialize<ShellRequest>(stream) ?? throw new InvalidDataException("Explorer request was empty.");
            if (request.Files is not { Length: > 0 and <= 100 } || request.Files.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p)))
                throw new InvalidDataException("Select between 1 and 100 local files or folders.");
            if (request.Target != null && (request.Target.Length is < 1 or > 12 || request.Target.Any(c => !char.IsAsciiLetterOrDigit(c) || char.IsUpper(c))))
                throw new InvalidDataException("Invalid conversion target.");
            return request with { Files = request.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }
        finally { File.Delete(path); }
    }
    public IReadOnlyList<Guid> Enqueue(ConversionQueue queue, EnginePaths engines)
    {
        if (Target == null) throw new InvalidOperationException("This request opens the format picker.");
        if (Files is not { Length: > 0 and <= 100 }) throw new InvalidDataException("Select between 1 and 100 items.");
        foreach (var file in Files)
        {
            var directory = Directory.Exists(file);
            if (!directory && !File.Exists(file)) throw new FileNotFoundException("A selected item no longer exists.", file);
            if (!ShellMenu.Targets(file, engines, directory).Any(t => t.Target == Target))
                throw new InvalidOperationException($"{Target.ToUpperInvariant()} is no longer available for {Path.GetFileName(file)}. Rescan Engines & setup.");
        }
        if (Target == "zip") return [queue.Enqueue(Files[0], Target, archiveSources: Files)];
        return Files.Select(file => queue.Enqueue(file, Target)).ToArray();
    }
}
