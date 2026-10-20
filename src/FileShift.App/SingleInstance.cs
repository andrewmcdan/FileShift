using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FileShift.App;

internal sealed class SingleInstance : IDisposable
{
    private readonly string name = "FileShift-" + WindowsIdentity.GetCurrent().User!.Value + "-" + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private readonly Mutex mutex;
    private readonly CancellationTokenSource stop = new();
    public bool IsPrimary { get; }
    public SingleInstance()
    {
        mutex = new Mutex(true, @"Local\" + name, out var primary);
        IsPrimary = primary;
    }
    public async Task<bool> Send(string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(args);
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token);
            await client.WriteAsync(bytes, timeout.Token);
            await client.FlushAsync(timeout.Token);
            var ack = new byte[1];
            await client.ReadExactlyAsync(ack, timeout.Token);
            return ack[0] == 1;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { return false; }
    }
    public async Task Listen(Action<string[]> receive)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await pipe.ReadExactlyAsync(header, timeout.Token);
                var length = BitConverter.ToInt32(header);
                if (length is < 2 or > 65536) continue;
                var bytes = new byte[length];
                await pipe.ReadExactlyAsync(bytes, timeout.Token);
                var args = JsonSerializer.Deserialize<string[]>(bytes);
                if (args != null) receive(args);
                await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        }
    }
    public void Dispose()
    {
        stop.Cancel();
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
