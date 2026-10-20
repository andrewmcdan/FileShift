using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FileShift.Core;

if (args.FirstOrDefault() == "--helper")
{
    switch (args[1])
    {
        case "echo": Console.Write(JsonSerializer.Serialize(args.Skip(2).ToArray())); return 0;
        case "sleep": Console.WriteLine(Environment.ProcessId); await Task.Delay(TimeSpan.FromMinutes(1)); return 0;
        case "flood": for (var i = 0; i < 1000; i++) { Console.WriteLine(new string('a', 100)); Console.Error.WriteLine(new string('b', 100)); } return 7;
    }
}
var root = Path.Combine(Path.GetTempPath(), "FileShift tests ' & ü " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var engines = new EnginePaths(new Settings());
var converter = new Converter(engines);
var passed = 0; var failed = 0; var skipped = 0;
async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + "\n" + e); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
var source = Path.Combine(root, "test & ' ü.png");
using (var bitmap = new Bitmap(32, 24)) { bitmap.SetPixel(5, 5, Color.Red); bitmap.Save(source, ImageFormat.Png); }
var originalHash = SHA256.HashData(File.ReadAllBytes(source));
try
{
    await Test("logical targets and engine-aware fallback", () =>
    {
        Assert(Formats.Routes("movie.mp4").Any(r => r.Target == "mp3"), "Missing audio extraction");
        Assert(!Formats.Routes("song.wav").Any(r => r.Target == "mp4"), "Audio should not offer a video export");
        Assert(Formats.Routes("letter.docx").Any(r => r.Target == "pdf"), "Missing PDF route");
        Assert(Formats.Routes("art.psd").Any(r => r.Target == "png"), "Missing PSD route");
        Assert(Formats.Routes("art.ai").Any(r => r.Engine.Contains(Formats.Ghostscript)), "AI needs Ghostscript");
        Assert(Formats.Routes("edit.prproj").Any(r => r.Engine == Formats.Adobe), "Missing Premiere route");
        Assert(Formats.Routes("file.unknown").Single().Target == "zip", "Any file can be zipped");
        Assert(Formats.Resolve(source, "jpg", engines).Engine == Formats.Native, "Expected built-in image engine");
        return Task.CompletedTask;
    });
    await Test("PNG to JPEG with actual image readback", async () =>
    {
        var output = await converter.ConvertAsync(source, "jpg", null, null, CancellationToken.None);
        using var image = Image.FromFile(output); Assert(image.Width == 32 && image.Height == 24, "Wrong image dimensions");
        Assert(image.RawFormat.Guid == ImageFormat.Jpeg.Guid, "Wrong output encoding");
    });
    await Test("shell targets use installed engines, omit no-op targets, and keep ZIP for arbitrary inputs", () =>
    {
        var disabled = new EnginePaths(new Settings { EngineOverrides = EnginePaths.ExternalEngines.ToDictionary(e => e, _ => Path.Combine(root, "missing-engine.exe")) });
        var targets = ShellMenu.Targets("photo.JPG", disabled).Select(t => t.Target).ToArray();
        Assert(targets.Contains("png") && targets.Contains("zip") && !targets.Contains("jpg") && !targets.Contains("webp"), "Wrong image submenu");
        Assert(!ShellMenu.Targets("photo.JPEG", disabled).Any(t => t.Target == "jpg"), "JPEG alias was offered as a conversion");
        Assert(ShellMenu.Targets("movie.mp4", disabled).Select(t => t.Target).SequenceEqual(new[] { "zip" }), "Missing media engine was offered");
        Assert(ShellMenu.Targets("folder.jpg", engines, directory: true).Single().Target == "zip", "Dotted folder was treated as an image");
        Assert(ShellMenu.Targets("file.unknown", engines).Single().Target == "zip", "Unknown file lost ZIP");
        Assert(ShellMenu.Targets("archive.zip", engines).Single().Target == "zip", "Existing archives cannot be packed into a new ZIP");
        Assert(ShellMenu.Targets("edit.prproj", disabled).Single().Target == "zip", "Unconfigured Adobe adapter was offered");
        var snapshot = ShellMenu.Snapshot(Environment.ProcessPath!, disabled);
        Assert(snapshot[0] == "V|1" && snapshot.Any(s => s == "R|<folder>|zip") && snapshot.Any(s => s == "R|*|zip"), "Incomplete shell snapshot");
        Assert(snapshot.All(s => !s.Contains("\n")), "Unexpected snapshot record separator");
        return Task.CompletedTask;
    });
    await Test("shell request preserves Unicode/quoted paths, removes its own request, and rejects outside files", async () =>
    {
        Directory.CreateDirectory(ShellRequest.RequestsDirectory);
        var requestFile = Path.Combine(ShellRequest.RequestsDirectory, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(requestFile, JsonSerializer.Serialize(new ShellRequest("png", [source, source])));
        var request = ShellRequest.ReadAndDelete(requestFile);
        Assert(request.Files.Single() == source && !File.Exists(requestFile), "Shell request was changed or not consumed");
        var outside = Path.Combine(root, "outside.json"); await File.WriteAllTextAsync(outside, "preserve me");
        await Throws<InvalidDataException>(() => Task.Run(() => ShellRequest.ReadAndDelete(outside)));
        Assert(File.Exists(outside), "Request validation deleted an outside file");
        await File.WriteAllTextAsync(requestFile, "{\"Target\":\"../exe\",\"Files\":[]}");
        await Throws<InvalidDataException>(() => Task.Run(() => ShellRequest.ReadAndDelete(requestFile)));
        Assert(!File.Exists(requestFile), "Invalid owned request was not removed");
    });
    await Test("shell request validates every item before queuing a mixed batch", async () =>
    {
        var text = Path.Combine(root, "text.unknown"); await File.WriteAllTextAsync(text, "not an image");
        await using var queue = new ConversionQueue(() => engines, Path.Combine(root, "shell-rejected.json"));
        await Throws<InvalidOperationException>(() => Task.Run(() => new ShellRequest("jpg", [source, text]).Enqueue(queue, engines)));
        Assert(queue.Snapshot().Count == 0, "Part of an incompatible shell batch was queued");
    });
    await Test("shell ZIP request queues exactly one archive for the full selection", async () =>
    {
        var text = Path.Combine(root, "zip-input.txt"); await File.WriteAllTextAsync(text, "ZIP batch");
        await using var queue = new ConversionQueue(() => engines, Path.Combine(root, "shell-zip.json"));
        var ids = new ShellRequest("zip", [source, text]).Enqueue(queue, engines);
        Assert(ids.Count == 1, "Each selected file became a separate archive");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (queue.Snapshot().Any(j => j.IsActive)) await Task.Delay(50, timeout.Token);
        var job = queue.Snapshot().Single(); Assert(job.State == JobState.Completed && job.Sources?.Length == 2, "Shell ZIP failed");
        using var archive = ZipFile.OpenRead(job.Output!); Assert(archive.Entries.Count == 2, "Shell archive lost inputs");
    });
    await Test("simultaneous colliding outputs preserve both files and source", async () =>
    {
        var outputs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => converter.ConvertAsync(source, "bmp", null, null, CancellationToken.None)));
        Assert(outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4, "Output collision");
        Assert(outputs.All(File.Exists), "Missing concurrent output");
        Assert(SHA256.HashData(File.ReadAllBytes(source)).SequenceEqual(originalHash), "Source was changed");
    });
    await Test("cancelled conversion publishes no output", async () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => converter.ConvertAsync(source, "gif", null, null, cancel.Token));
        Assert(!File.Exists(Path.ChangeExtension(source, "gif")), "Published cancelled output");
    });
    await Test("bad image fails with no partial output", async () =>
    {
        var bad = Path.Combine(root, "bad.png"); await File.WriteAllTextAsync(bad, "this is not an image");
        await Throws<InvalidDataException>(() => converter.ConvertAsync(bad, "jpg", null, null, CancellationToken.None));
        Assert(!File.Exists(Path.ChangeExtension(bad, "jpg")), "Bad conversion produced output");
    });
    await Test("process arguments preserve spaces, punctuation and Unicode", async () =>
    {
        string[] values = ["a b", "a&b", "quote\"value", "'apostrophe'", "c:\\space dir\\", "ü文"];
        var result = await ProcessRunner.RunAsync(Environment.ProcessPath!, new[] { "--helper", "echo" }.Concat(values), CancellationToken.None);
        Assert(JsonSerializer.Deserialize<string[]>(result.Output)!.SequenceEqual(values), "Arguments were changed");
    });
    await Test("bounded process logs drain stdout/stderr and retain exit code", async () =>
    {
        var result = await ProcessRunner.RunAsync(Environment.ProcessPath!, ["--helper", "flood"], CancellationToken.None);
        Assert(result.ExitCode == 7 && result.Output.Length <= 12000, "Incorrect process result");
    });
    await Test("cancellation terminates a real subprocess", async () =>
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1)); int pid = 0;
        await Throws<OperationCanceledException>(() => ProcessRunner.RunAsync(Environment.ProcessPath!, ["--helper", "sleep"], cancel.Token, s => int.TryParse(s, out pid)));
        Assert(pid > 0, "Child did not start");
        try { using var process = Process.GetProcessById(pid); Assert(process.HasExited, "Child still running"); } catch (ArgumentException) { }
    });
    await Test("process timeout terminates subprocess", async () =>
    {
        await Throws<TimeoutException>(() => ProcessRunner.RunAsync(Environment.ProcessPath!, ["--helper", "sleep"], CancellationToken.None, timeout: TimeSpan.FromMilliseconds(600)));
    });
    await Test("ZIP batch preserves entries, Unicode, empty folders, and duplicate root names", async () =>
    {
        var a = Path.Combine(root, "folder A"); var b = Path.Combine(root, "folder B");
        Directory.CreateDirectory(Path.Combine(a, "empty")); Directory.CreateDirectory(b);
        await File.WriteAllTextAsync(Path.Combine(a, "same.txt"), "first ü");
        await File.WriteAllTextAsync(Path.Combine(b, "same.txt"), "second 文");
        var zip = await ZipConverter.CreateAsync([Path.Combine(a, "same.txt"), Path.Combine(b, "same.txt"), a], root, null, CancellationToken.None);
        using var archive = ZipFile.OpenRead(zip);
        Assert(archive.GetEntry("same.txt") != null && archive.GetEntry("same.txt (2)") != null, "Root collision was lost");
        Assert(archive.GetEntry("folder A/empty/") != null, "Empty folder lost");
        using var reader = new StreamReader(archive.GetEntry("folder A/same.txt")!.Open()); Assert(await reader.ReadToEndAsync() == "first ü", "ZIP content changed");
    });
    await Test("ZIP cancellation leaves no published output or staging directories", async () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => ZipConverter.CreateAsync([source], root, null, cancel.Token));
        Assert(!Directory.GetDirectories(root, ".fileshift-*").Any(), "Temporary files left behind");
    });
    await Test("queue completes a 20-file JPEG to PNG batch and persists output paths", async () =>
    {
        var history = Path.Combine(root, "batch-history.json");
        var jpeg = Path.ChangeExtension(source, "jpg");
        var outputDir = Path.Combine(root, "batch");
        await using (var queue = new ConversionQueue(() => engines, history))
        {
            for (var i = 0; i < 20; i++) queue.Enqueue(jpeg, "png", outputDir);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (queue.Snapshot().Any(j => j.IsActive)) await Task.Delay(50, timeout.Token);
            Assert(queue.Snapshot().Count(j => j.State == JobState.Completed) == 20, "Batch failed");
            Assert(queue.Snapshot().Select(j => j.Output).Distinct().Count() == 20, "Batch overwrote outputs");
        }
        await using var restored = new ConversionQueue(() => engines, history);
        Assert(restored.Snapshot().Count == 20 && restored.Snapshot().All(j => File.Exists(j.Output)), "History did not restore");
    });
    await Test("queue recovers interrupted work without automatically repeating it", async () =>
    {
        var history = Path.Combine(root, "interrupted.json");
        await File.WriteAllTextAsync(history, JsonSerializer.Serialize(new[] { new JobSnapshot(Guid.NewGuid(), source, "jpg", null, JobState.Running, 50, null, null, DateTimeOffset.Now) }));
        await using var queue = new ConversionQueue(() => engines, history);
        Assert(queue.Snapshot().Single().State == JobState.Interrupted, "Interrupted job was resumed unsafely");
    });
    await Test("queue retries history persistence after a temporary file-reader lock", async () =>
    {
        var history = Path.Combine(root, "locked-history.json"); await File.WriteAllTextAsync(history, "[]");
        await using var queue = new ConversionQueue(() => engines, history);
        using (var reader = new FileStream(history, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            queue.Enqueue(source, "jpg", Path.Combine(root, "locked-history-output"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (queue.Snapshot().Any(j => j.IsActive)) await Task.Delay(50, timeout.Token);
            Assert(queue.PersistenceError != null, "Expected a recoverable persistence lock error");
        }
        queue.RetryPersistence();
        var stored = JsonSerializer.Deserialize<JobSnapshot[]>(await File.ReadAllTextAsync(history))!;
        Assert(queue.PersistenceError == null && stored.Single().State == JobState.Completed, "Completed history was not recovered");
    });
    await Test("bad engine override is unavailable rather than silently replaced", () =>
    {
        var config = new Settings { EngineOverrides = new() { [Formats.Media] = Path.Combine(root, "missing.exe") } };
        Assert(!new EnginePaths(config).Available(Formats.Media), "Ignored an explicit engine override"); return Task.CompletedTask;
    });
    await Test("Adobe script uses encoded paths, a single-sequence check and real completion events", () =>
    {
        var script = AdobeConverter.BuildScript("C:\\file ' ü.prproj", "C:\\out.mp4", "C:\\preset.epr", "C:\\status.txt", "mp4");
        Assert(script.Contains("fsItems.length !== 1") && script.Contains("onEncodeFinished") && script.Contains("Done!"), "Missing Adobe outcome checks");
        Assert(!script.Contains("app.quit") && !script.Contains("stopBatch"), "Adapter should not stop an interactive Adobe session"); return Task.CompletedTask;
    });
    if (engines.Available(Formats.Media))
    {
        var wav = Path.Combine(root, "tone.wav"); var mp4 = Path.Combine(root, "clip.mp4");
        await Test("live FFmpeg WAV to MP3 with codec readback", async () =>
        {
            var fixture = await ProcessRunner.RunAsync(engines[Formats.Media]!, ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-y", wav], CancellationToken.None);
            Assert(fixture.ExitCode == 0, "Failed to generate fixture");
            var output = await converter.ConvertAsync(wav, "mp3", null, null, CancellationToken.None);
            var probe = await ProcessRunner.RunAsync(EnginePaths.FindOnPath("ffprobe.exe")!, ["-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", output], CancellationToken.None);
            Assert(probe.Output.Trim() == "mp3", "Wrong audio codec");
        });
        await Test("live FFmpeg MP4 to MP3 audio extraction", async () =>
        {
            var fixture = await ProcessRunner.RunAsync(engines[Formats.Media]!, ["-hide_banner", "-nostdin", "-f", "lavfi", "-i", "color=c=blue:s=32x24:d=1", "-i", wav, "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", "-y", mp4], CancellationToken.None);
            Assert(fixture.ExitCode == 0, "Failed to generate video fixture");
            var output = await converter.ConvertAsync(mp4, "mp3", null, null, CancellationToken.None);
            Assert(new FileInfo(output).Length > 1000, "Empty extraction");
        });
        await Test("live FFmpeg invalid media reports failure without output", async () =>
        {
            var corrupt = Path.Combine(root, "corrupt.wav"); await File.WriteAllTextAsync(corrupt, "not audio");
            await Throws<InvalidOperationException>(() => converter.ConvertAsync(corrupt, "mp3", null, null, CancellationToken.None));
            Assert(!File.Exists(Path.ChangeExtension(corrupt, "mp3")), "Published an invalid result");
        });
    }
    else { skipped += 3; Console.WriteLine("SKIP live FFmpeg tests — engine not installed"); }
    if (engines.Available(Formats.Images))
    {
        await Test("live ImageMagick PSD flattened PNG export", async () =>
        {
            var psd = Path.Combine(root, "art.psd");
            var fixture = await ProcessRunner.RunAsync(engines[Formats.Images]!, [source, psd], CancellationToken.None);
            Assert(fixture.ExitCode == 0, "Failed to create PSD fixture");
            var output = await converter.ConvertAsync(psd, "png", null, null, CancellationToken.None);
            using var image = Image.FromFile(output); Assert(image.Width == 32 && image.Height == 24, "PSD dimensions changed");
        });
        await Test("live ImageMagick WebP roundtrip", async () =>
        {
            var webp = await converter.ConvertAsync(source, "webp", null, null, CancellationToken.None);
            var output = await converter.ConvertAsync(webp, "png", null, null, CancellationToken.None);
            using var image = Image.FromFile(output); Assert(image.Width == 32, "WebP roundtrip failed");
        });
    }
    else { skipped += 2; Console.WriteLine("SKIP live ImageMagick tests — engine not installed"); }
    if (engines.Available(Formats.Office))
    {
        await Test("live LibreOffice DOCX to PDF", async () =>
        {
            var text = Path.Combine(root, "document.txt"); await File.WriteAllTextAsync(text, "FileShift live document conversion.");
            var docx = await converter.ConvertAsync(text, "docx", null, null, CancellationToken.None);
            var pdf = await converter.ConvertAsync(docx, "pdf", null, null, CancellationToken.None);
            Assert(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 5) == "%PDF-", "Invalid PDF output");
        });
    }
    else { skipped++; Console.WriteLine("SKIP live Office tests — install LibreOffice using setup"); }
    if (engines.Available(Formats.Text))
    {
        await Test("live Pandoc Markdown to EPUB", async () =>
        {
            var md = Path.Combine(root, "book.md"); await File.WriteAllTextAsync(md, "# FileShift\n\nAn ebook test.");
            var epub = await converter.ConvertAsync(md, "epub", null, null, CancellationToken.None);
            using var archive = ZipFile.OpenRead(epub); Assert(archive.GetEntry("mimetype") != null, "Invalid EPUB");
        });
    }
    else { skipped++; Console.WriteLine("SKIP live Pandoc tests — install Pandoc using setup"); }
    Assert(!Directory.GetDirectories(root, ".fileshift-*", SearchOption.AllDirectories).Any(), "Staging files were left behind");
}
finally
{
    var full = Path.GetFullPath(root);
    if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("FileShift tests ", StringComparison.Ordinal)) Directory.Delete(full, true);
}
Console.WriteLine($"\n{passed} passed, {failed} failed, {skipped} skipped.");
return failed == 0 ? 0 : 1;
