using System.Text.Json;

namespace FileShift.Core;

public sealed class Settings
{
    public Dictionary<string, string> EngineOverrides { get; set; } = new();
    public Dictionary<string, string> AdobePresets { get; set; } = new();
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileShift");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
}

public sealed class EnginePaths
{
    private readonly Dictionary<string, string?> paths = new();
    public static readonly string[] ExternalEngines = [Formats.Media, Formats.Images, Formats.Office, Formats.Text, Formats.Ebook, Formats.Ghostscript, Formats.Adobe];
    public IReadOnlyDictionary<string, string> AdobePresets { get; }
    public string? this[string engine] => paths.GetValueOrDefault(engine);
    public bool Available(string engine) => engine.Split('+').All(e => e is Formats.Native or Formats.Archive || this[e] != null);
    public EnginePaths(Settings settings)
    {
        AdobePresets = new Dictionary<string, string>(settings.AdobePresets);
        paths[Formats.Media] = Find(Formats.Media, "ffmpeg.exe");
        paths[Formats.Images] = Find(Formats.Images, "magick.exe", FindProgram("ImageMagick*", "magick.exe"));
        paths[Formats.Office] = Find(Formats.Office, "soffice.com", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.com"));
        paths[Formats.Text] = Find(Formats.Text, "pandoc.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pandoc", "pandoc.exe"))
            ?? Existing(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pandoc", "pandoc.exe"));
        paths[Formats.Ghostscript] = Find(Formats.Ghostscript, "gswin64c.exe", FindNested("gs", "gs*", "bin", "gswin64c.exe"));
        paths[Formats.Adobe] = Find(Formats.Adobe, "Adobe Media Encoder.exe", FindNested("Adobe", "Adobe Media Encoder*", "", "Adobe Media Encoder.exe"));
        paths[Formats.Ebook] = Find(Formats.Ebook, "ebook-convert.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2", "ebook-convert.exe"));

        string? Find(string engine, string exe, string? common = null)
        {
            if (settings.EngineOverrides.TryGetValue(engine, out var custom) && !string.IsNullOrWhiteSpace(custom))
                return File.Exists(custom) ? Path.GetFullPath(custom) : null;
            var portable = Path.Combine(AppContext.BaseDirectory, "tools", engine, exe);
            if (File.Exists(portable)) return portable;
            if (common != null && File.Exists(common)) return common;
            return FindOnPath(exe);
        }
    }
    private static string? FindNested(string root, string pattern, string inner, string executable)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), root);
        if (!Directory.Exists(directory)) return null;
        return Directory.GetDirectories(directory, pattern).OrderDescending().Select(p => Path.Combine(p, inner, executable)).FirstOrDefault(File.Exists);
    }
    private static string? Existing(string path) => File.Exists(path) ? path : null;
    public static string? FindOnPath(string exe) => string.Join(Path.PathSeparator,
        Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
        Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links")).Split(Path.PathSeparator)
        .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), exe)).FirstOrDefault(File.Exists);
    private static string? FindProgram(string pattern, string relative)
    {
        try
        {
            return Directory.GetDirectories(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pattern)
                .OrderDescending().Select(p => Path.Combine(p, relative)).FirstOrDefault(File.Exists);
        }
        catch (IOException) { return null; }
    }
}
