using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;

namespace FileShift.Core;

public sealed class Converter(EnginePaths engines)
{
    public async Task<string> ConvertAsync(string source, string target, string? outputDirectory, IProgress<double?>? progress, CancellationToken token)
    {
        if (target == "zip") return await ZipConverter.CreateAsync([source], outputDirectory, progress, token);
        source = Path.GetFullPath(source);
        if (!File.Exists(source)) throw new FileNotFoundException("The source file no longer exists.", source);
        var route = Formats.Resolve(source, target, engines);
        var directory = Path.GetFullPath(outputDirectory ?? Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".fileshift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var output = Path.Combine(stage, "result." + target);
        try
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(null);
            switch (route.Engine)
            {
                case Formats.Native:
                    await Task.Run(() => ConvertImage(source, output, target), token);
                    break;
                case Formats.Media:
                    await ConvertMedia(source, output, target, progress, token);
                    break;
                case Formats.Images:
                case Formats.Images + "+" + Formats.Ghostscript:
                    await Run(Formats.Images, ["-limit", "memory", "256MiB", "-limit", "map", "512MiB", source + "[0]", "-auto-orient", output], token);
                    break;
                case Formats.Adobe:
                    await AdobeConverter.RenderAsync(engines, source, output, target, progress, token);
                    break;
                case Formats.Office:
                    output = await ConvertOffice(source, target, stage, token);
                    break;
                case Formats.Text:
                    await ConvertText(source, output, target, token);
                    break;
                case Formats.Ebook:
                    await Run(Formats.Ebook, [source, output], token, timeout: TimeSpan.FromMinutes(10));
                    break;
                default:
                    var intermediate = Path.Combine(stage, "intermediate.docx");
                    await ConvertText(source, intermediate, "docx", token);
                    output = await ConvertOffice(intermediate, "pdf", stage, token);
                    break;
            }
            token.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("The engine did not produce a valid output file. The format or codec may be unsupported by this engine build.");
            var final = Publish(output, directory, Path.GetFileNameWithoutExtension(source), target);
            progress?.Report(100);
            return final;
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
    public static string Publish(string staged, string directory, string stem, string target)
    {
        for (var suffix = 0; ; suffix++)
        {
            var path = Path.Combine(directory, stem + (suffix == 0 ? "" : $" ({suffix})") + "." + target);
            try { File.Move(staged, path, overwrite: false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
    }
    private static void ConvertImage(string source, string output, string target)
    {
        using var image = LoadImage(source);
        if ((long)image.Width * image.Height > 100_000_000) throw new InvalidOperationException("Image exceeds the built-in engine's 100 megapixel limit.");
        var format = target switch { "jpg" => ImageFormat.Jpeg, "bmp" => ImageFormat.Bmp, "gif" => ImageFormat.Gif, "tif" => ImageFormat.Tiff, _ => ImageFormat.Png };
        using var canvas = new Bitmap(image.Width, image.Height);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(target == "jpg" ? Color.White : Color.Transparent);
            graphics.DrawImage(image, 0, 0, image.Width, image.Height);
        }
        canvas.Save(output, format);
    }
    private static Image LoadImage(string source)
    {
        try { return Image.FromFile(source); }
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { throw new InvalidDataException("Windows could not decode this image. It may be damaged, unsupported, or too large to load.", e); }
    }
    private async Task ConvertMedia(string source, string output, string target, IProgress<double?>? progress, CancellationToken token)
    {
        double duration = 0;
        var ffmpeg = engines[Formats.Media]!;
        var probe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        if (!File.Exists(probe)) probe = EnginePaths.FindOnPath("ffprobe.exe");
        if (probe != null)
        {
            try
            {
                var result = await ProcessRunner.RunAsync(probe, ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", source], token, timeout: TimeSpan.FromSeconds(30));
                double.TryParse(result.Output.Trim(), CultureInfo.InvariantCulture, out duration);
            }
            catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception or IOException) { }
        }
        var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", source, "-progress", "pipe:1", "-nostats", "-threads", "2" };
        args.AddRange(target switch
        {
            "mp3" => ["-vn", "-c:a", "libmp3lame", "-q:a", "2"],
            "wav" => ["-vn", "-c:a", "pcm_s16le"],
            "flac" => ["-vn", "-c:a", "flac"],
            "m4a" or "aac" => ["-vn", "-c:a", "aac", "-b:a", "192k"],
            "ogg" => ["-vn", "-c:a", "libvorbis", "-q:a", "5"],
            "opus" => ["-vn", "-c:a", "libopus", "-b:a", "128k"],
            "aiff" => ["-vn", "-c:a", "pcm_s16be"],
            "webm" => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-c:a", "libopus"],
            "avi" => ["-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame"],
            "gif" => ["-an", "-vf", "fps=12,scale='min(640,iw)':-1:flags=lanczos"],
            _ => new[] { "-c:v", "libx264", "-preset", "fast", "-crf", "23", "-pix_fmt", "yuv420p", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:a", "aac", "-b:a", "192k" }
        });
        if (target is "mp4" or "mov") args.AddRange(["-movflags", "+faststart"]);
        args.Add(output);
        await Run(Formats.Media, args, token, line =>
        {
            if (duration > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                double.TryParse(line[12..], CultureInfo.InvariantCulture, out var microseconds))
                progress?.Report(Math.Clamp(microseconds / 1_000_000 / duration * 100, 0, 99));
        });
    }
    private async Task<string> ConvertOffice(string source, string target, string stage, CancellationToken token)
    {
        var profile = Path.Combine(stage, "lo-profile");
        // Each conversion has a separate profile: it cannot attach to an open Office session.
        var filter = target == "txt" ? "txt:Text (encoded):UTF8" : target;
        await Run(Formats.Office, ["-env:UserInstallation=" + new Uri(profile).AbsoluteUri, "--headless", "--nologo", "--nodefault", "--norestore", "--convert-to", filter, "--outdir", stage, source], token, timeout: TimeSpan.FromMinutes(5));
        return Path.Combine(stage, Path.GetFileNameWithoutExtension(source) + "." + target);
    }
    private Task ConvertText(string source, string output, string target, CancellationToken token) =>
        Run(Formats.Text, ["--sandbox", "--from", Formats.Reader(source), "--to", Formats.WriterFormat(target), "--standalone", "--output", output, source], token, timeout: TimeSpan.FromMinutes(5));
    private async Task Run(string engine, IEnumerable<string> args, CancellationToken token, Action<string>? onLine = null, TimeSpan? timeout = null)
    {
        var result = await ProcessRunner.RunAsync(engines[engine]!, args, token, onLine, timeout);
        if (result.ExitCode != 0) throw new InvalidOperationException($"{engine} failed (exit {result.ExitCode}).\n{result.Output}");
    }
}
