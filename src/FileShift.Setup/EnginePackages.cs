using FileShift.Core;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace FileShift.Setup;

public record EnginePackage(string Name, string Id, string Purpose, string DownloadUrl, string LicenseUrl);
public static class EnginePackages
{
    public static readonly EnginePackage[] All =
    [
        new(Formats.Media, "Gyan.FFmpeg", "Audio, video & audio extraction", "https://ffmpeg.org/download.html", "https://ffmpeg.org/legal.html"),
        new(Formats.Office, "TheDocumentFoundation.LibreOffice", "Word, Excel, PowerPoint & PDF export", "https://www.libreoffice.org/download/download-libreoffice/", "https://www.libreoffice.org/about-us/licenses/"),
        new(Formats.Images, "ImageMagick.ImageMagick", "WebP, HEIC, AVIF, Photoshop & other images", "https://imagemagick.org/script/download.php", "https://imagemagick.org/script/license.php"),
        new(Formats.Ghostscript, "Artifex official download", "Illustrator, EPS & PDF image input (AGPL)", "https://ghostscript.com/releases/gsdnld.html", "https://www.ghostscript.com/licensing/index.html"),
        new(Formats.Text, "JohnMacFarlane.Pandoc", "Markdown, HTML, ebooks & document structure", "https://pandoc.org/installing.html", "https://pandoc.org/license.html"),
        new(Formats.Ebook, "calibre.calibre", "Kindle/EPUB and document reflow", "https://calibre-ebook.com/download_windows", "https://github.com/kovidgoyal/calibre/blob/master/LICENSE")
    ];
    public static async Task<string?> Install(EnginePackage package, Action<string> log)
    {
        if (package.Name == Formats.Ghostscript) return await InstallGhostscript(log);
        var winget = EnginePaths.FindOnPath("winget.exe") ?? throw new InvalidOperationException("Windows Package Manager is unavailable. Use the official download links, then rerun setup.");
        log($"Obtaining {package.Name} ({package.Id}) from the winget community repository…");
        var result = await ProcessRunner.RunAsync(winget,
            ["install", "--id", package.Id, "--exact", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
            CancellationToken.None, log, TimeSpan.FromMinutes(45));
        // Winget may report an already-installed package with a nonzero result; verify detection regardless.
        var detected = new EnginePaths(Settings.Load())[package.Name];
        if (detected != null) { log($"{package.Name} detected: {detected}"); return detected; }
        log($"{package.Name} was not detected after winget exited with {result.ExitCode}. Use its official download link or set a custom engine path.\n{result.Output}");
        return null;
    }
    private static async Task<string?> InstallGhostscript(Action<string> log)
    {
        // Ghostscript is not in the winget repository. Obtain the vendor's release and verify its published digest.
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("FileShift-Setup/0.1");
        using var release = JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/ArtifexSoftware/ghostpdl-downloads/releases/latest"));
        var asset = release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString()!.EndsWith("w64.exe", StringComparison.Ordinal));
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.GetProperty("digest").GetString() ?? "";
        if (!url.StartsWith("https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/", StringComparison.Ordinal) || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            throw new InvalidDataException("Ghostscript release has no usable vendor SHA-256 digest. Use the official manual download link.");
        var directory = Path.Combine(Path.GetTempPath(), "FileShift-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, "GhostscriptSetup.exe");
        try
        {
            log("Downloading Ghostscript from Artifex's official GitHub release…");
            await using (var input = await http.GetStreamAsync(url))
            await using (var output = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None)) await input.CopyToAsync(output);
            await using (var file = File.OpenRead(installer))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ghostscript download failed SHA-256 verification.");
            }
            log("SHA-256 verified. Opening Ghostscript's vendor installer; complete its installation window.");
            // The vendor handles elevation and its own AGPL/commercial licensing choices.
            using var process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }) ?? throw new IOException("Could not launch Ghostscript setup.");
            await process.WaitForExitAsync();
            var detected = new EnginePaths(Settings.Load())[Formats.Ghostscript];
            log(detected != null ? "Ghostscript detected: " + detected : "Ghostscript not detected. Installation may have been cancelled; use the manual download link or a custom engine path.");
            return detected;
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
