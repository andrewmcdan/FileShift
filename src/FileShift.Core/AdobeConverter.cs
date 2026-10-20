using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FileShift.Core;

/// <summary>Opt-in AME scripting adapter. Requires AME 22.5+ and a matching exported EPR preset.</summary>
public static class AdobeConverter
{
    public static async Task RenderAsync(EnginePaths engines, string source, string output, string target, IProgress<double?>? progress, CancellationToken token)
    {
        var executable = engines[Formats.Adobe]!;
        var version = FileVersionInfo.GetVersionInfo(executable);
        if (version.FileMajorPart < 22 || (version.FileMajorPart == 22 && version.FileMinorPart < 5))
            throw new InvalidOperationException("The experimental Premiere adapter requires Adobe Media Encoder 22.5 or newer. Older releases have known scripting problems.");
        if (!engines.AdobePresets.TryGetValue(target, out var preset) || !File.Exists(preset))
            throw new InvalidOperationException($"Choose an Adobe .epr export preset for {target.ToUpperInvariant()} in Engines & setup first.");
        if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)).Length > 0)
            throw new InvalidOperationException("Close Adobe Media Encoder before starting a FileShift Premiere render. FileShift cannot safely inject a script into an existing session.");
        token.ThrowIfCancellationRequested();
        var work = Path.Combine(Settings.DataDirectory, "AdobeJobs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var rendered = Path.Combine(work, "render." + target);
        var status = Path.Combine(work, "status.txt");
        var script = Path.Combine(work, "render.jsx");
        File.WriteAllText(script, BuildScript(source, rendered, preset, status, target));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        start.ArgumentList.Add("--console"); start.ArgumentList.Add("es.processFile"); start.ArgumentList.Add(script);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Adobe Media Encoder.");
        var started = Stopwatch.StartNew();
        var completed = false;
        try
        {
            while (started.Elapsed < TimeSpan.FromHours(2))
            {
                token.ThrowIfCancellationRequested();
                string state = "";
                try { if (File.Exists(status)) state = await File.ReadAllTextAsync(status, token); }
                catch (IOException) { }
                if (state.StartsWith("done", StringComparison.Ordinal))
                {
                    if (!File.Exists(rendered) || new FileInfo(rendered).Length == 0) throw new IOException("Adobe reported completion without an output file.");
                    File.Copy(rendered, output, overwrite: false); completed = true; return;
                }
                if (state.StartsWith("failed|", StringComparison.Ordinal)) throw new InvalidOperationException("Adobe: " + state[7..]);
                if (state.StartsWith("running|", StringComparison.Ordinal) && double.TryParse(state[8..], CultureInfo.InvariantCulture, out var percent)) progress?.Report(Math.Clamp(percent, 0, 99));
                if (state.Length == 0 && started.Elapsed > TimeSpan.FromMinutes(2)) throw new TimeoutException("Adobe did not acknowledge the render script. This adapter is experimental; check AME and its scripting support.");
                if (process.HasExited && state.Length == 0) throw new InvalidOperationException("Adobe closed before acknowledging the render request.");
                await Task.Delay(500, token);
            }
            throw new TimeoutException("Adobe render monitoring exceeded two hours. Check Media Encoder.");
        }
        finally
        {
            // AME is an interactive user app. Never kill it or remove its working files while it may render.
            if (completed) { try { Directory.Delete(work, true); } catch (IOException) { } }
        }
    }
    public static string BuildScript(string source, string output, string preset, string status, string target)
    {
        var format = target switch { "mp4" => "H.264", "mov" => "QuickTime", "mp3" => "MP3", "wav" => "Waveform Audio", _ => throw new ArgumentException("Unsupported Adobe target.") };
        return "var fsSource=" + JsonSerializer.Serialize(source) + ";\nvar fsOutput=" + JsonSerializer.Serialize(output) +
            ";\nvar fsPreset=" + JsonSerializer.Serialize(preset) + ";\nvar fsStatus=" + JsonSerializer.Serialize(status) +
            ";\nvar fsFormat=" + JsonSerializer.Serialize(format) + ";\n" + """
            function fsWrite(value) {
                var f = new File(fsStatus); f.encoding = "UTF-8";
                if (f.open("w")) { f.write(value); f.close(); }
            }
            try {
                fsWrite("starting");
                var fsFrontend = app.getFrontend();
                if (!fsFrontend) throw new Error("Media Encoder scripting frontend unavailable.");
                var fsItems = fsFrontend.getDLItemsAtRoot(fsSource);
                if (!fsItems || fsItems.length !== 1) throw new Error("This adapter requires exactly one root sequence. Choose a sequence in Media Encoder, or prepare a single-sequence project.");
                fsFrontend.addEventListener("onBatchItemCreationFailed", function(e) { fsWrite("failed|" + e.error); });
                var fsEncoder = fsFrontend.addDLToBatch(fsSource, fsFormat, fsPreset, fsItems[0], fsOutput);
                if (!fsEncoder) throw new Error("Could not queue the sequence. Check linked media and preset.");
                fsEncoder.addEventListener("onEncodeProgress", function(e) { fsWrite("running|" + e.result); });
                fsEncoder.addEventListener("onEncodeFinished", function(e) { fsWrite(e.result === "Done!" ? "done" : "failed|" + e.result); });
                app.getEncoderHost().runBatch();
            } catch (error) { fsWrite("failed|" + error.toString()); }
            """;
    }
}
