using System.Diagnostics;
using System.Text;

namespace FileShift.Core;

public record ProcessResult(int ExitCode, string Output);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, CancellationToken token,
        Action<string>? onLine = null, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory ?? Path.GetTempPath() };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout ?? TimeSpan.FromHours(2));
        token.ThrowIfCancellationRequested();
        process.Start();
        var log = new StringBuilder();
        async Task Drain(StreamReader reader)
        {
            var buffer = new char[2048];
            var pending = new StringBuilder();
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), limit.Token);
                if (count == 0) break;
                lock (log)
                {
                    log.Append(buffer, 0, count);
                    if (log.Length > 12000) log.Remove(0, log.Length - 12000);
                }
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] is '\n' or '\r') { if (pending.Length > 0) onLine?.Invoke(pending.ToString()); pending.Clear(); }
                    else if (pending.Length < 4096) pending.Append(buffer[i]);
                }
            }
            if (pending.Length > 0) onLine?.Invoke(pending.ToString());
        }
        var output = Drain(process.StandardOutput);
        var errors = Drain(process.StandardError);
        try
        {
            await process.WaitForExitAsync(limit.Token);
            await Task.WhenAll(output, errors);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, errors); } catch (OperationCanceledException) { }
            if (token.IsCancellationRequested) throw;
            throw new TimeoutException("The conversion engine exceeded its time limit.");
        }
        return new(process.ExitCode, log.ToString());
    }
}
