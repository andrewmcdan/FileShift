using System.Diagnostics;

namespace FileShift.Core;

public sealed record ShellMenuTarget(string Target, string Label);

// Engine/filesystem discovery happens in the app; Explorer only consumes this snapshot.
public static class ShellMenu
{
    private static readonly ShellMenuTarget[] OrderedTargets =
    [
        new("pdf", "PDF document"), new("png", "PNG image"), new("jpg", "JPEG image"), new("webp", "WebP image"),
        new("avif", "AVIF image"), new("heic", "HEIC image"), new("tif", "TIFF image"), new("bmp", "BMP image"),
        new("gif", "GIF image / animation"), new("ico", "Windows icon"),
        new("mp4", "MP4 video"), new("mkv", "MKV video"), new("webm", "WebM video"), new("mov", "MOV video"), new("avi", "AVI video"),
        new("mp3", "MP3 audio"), new("wav", "WAV audio"), new("flac", "FLAC audio"), new("m4a", "M4A audio"),
        new("ogg", "OGG audio"), new("opus", "Opus audio"), new("aac", "AAC audio"), new("aiff", "AIFF audio"),
        new("docx", "Word document (DOCX)"), new("odt", "OpenDocument text (ODT)"), new("rtf", "Rich text (RTF)"), new("txt", "Plain text (TXT)"),
        new("xlsx", "Excel workbook (XLSX)"), new("ods", "OpenDocument spreadsheet (ODS)"), new("csv", "CSV spreadsheet"),
        new("pptx", "PowerPoint presentation (PPTX)"), new("odp", "OpenDocument presentation (ODP)"),
        new("md", "Markdown"), new("html", "HTML document"), new("epub", "EPUB ebook"), new("mobi", "MOBI ebook"),
        new("azw3", "Kindle ebook (AZW3)"), new("fb2", "FictionBook (FB2)"), new("rst", "reStructuredText"),
        new("tex", "LaTeX document"), new("org", "Org document"), new("zip", "ZIP archive")
    ];
    public static IReadOnlyList<ShellMenuTarget> Targets(string source, EnginePaths engines, bool directory = false)
    {
        var routes = Formats.Routes(directory ? "folder" : source);
        var sourceFormat = Formats.Extension(source) switch { "jpeg" or "jpe" => "jpg", "tiff" => "tif", var extension => extension };
        return OrderedTargets.Where(t => (t.Target == "zip" || directory || t.Target != sourceFormat) && routes.Any(r => r.Target == t.Target && Ready(r, engines))).ToArray();
    }
    public static string[] Snapshot(string executable, EnginePaths engines)
    {
        var routes = Formats.SourceExtensions.ToDictionary(e => e, e => Targets("source." + e, engines));
        var used = routes.Values.SelectMany(r => r).Select(r => r.Target).Append("zip").ToHashSet(StringComparer.Ordinal);
        return new[] { "V|1", "X|" + Path.GetFullPath(executable) }
            .Concat(OrderedTargets.Where(t => used.Contains(t.Target)).Select(t => "T|" + t.Target + "|" + t.Label))
            .Concat(routes.Select(r => "R|" + r.Key + "|" + string.Join(',', r.Value.Select(t => t.Target))))
            .Concat(["R|*|zip", "R|<folder>|zip"]).ToArray();
    }
    private static bool Ready(ConversionRoute route, EnginePaths engines)
    {
        if (!engines.Available(route.Engine)) return false;
        if (route.Engine != Formats.Adobe) return true;
        if (!engines.AdobePresets.TryGetValue(route.Target, out var preset) || !File.Exists(preset)) return false;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(engines[Formats.Adobe]!);
            return version.FileMajorPart > 22 || (version.FileMajorPart == 22 && version.FileMinorPart >= 5);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
