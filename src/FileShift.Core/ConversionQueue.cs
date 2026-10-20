using System.Text.Json;
using System.Threading.Channels;

namespace FileShift.Core;

public enum JobState { Queued, Running, Completed, Failed, Cancelled, Interrupted }
public sealed record JobSnapshot(Guid Id, string Source, string Target, string? OutputDirectory, JobState State,
    double? Progress, string? Output, string? Error, DateTimeOffset Created, string[]? Sources = null)
{
    public string Name => Sources is { Length: > 1 } ? $"{Sources.Length} items → Archive.zip" : Path.GetFileName(Source);
    public string FormatLabel => Formats.Extension(Source).ToUpperInvariant() + " → " + Target.ToUpperInvariant();
    public string StatusLabel => State == JobState.Running ? (Progress.HasValue ? $"Converting · {Progress:0}%" : "Converting…") : State.ToString();
    public bool IsActive => State is JobState.Queued or JobState.Running;
    public bool CanRetry => State is JobState.Failed or JobState.Cancelled or JobState.Interrupted;
}

public sealed class ConversionQueue : IAsyncDisposable
{
    private sealed class Job(JobSnapshot snapshot)
    {
        public JobSnapshot Snapshot = snapshot;
        public readonly CancellationTokenSource Cancellation = new();
    }
    private readonly object gate = new();
    private readonly List<Job> jobs = [];
    private readonly Channel<Job> channel = Channel.CreateBounded<Job>(500);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task[] workers;
    private readonly Func<EnginePaths> engines;
    private readonly string historyPath;
    public string? PersistenceError { get; private set; }
    public ConversionQueue(Func<EnginePaths> engines, string? historyPath = null, int concurrency = 2)
    {
        this.engines = engines;
        this.historyPath = historyPath ?? Path.Combine(Settings.DataDirectory, "history.json");
        try
        {
            if (File.Exists(this.historyPath))
                jobs.AddRange((JsonSerializer.Deserialize<JobSnapshot[]>(File.ReadAllText(this.historyPath)) ?? [])
                    .TakeLast(200).Select(j => new Job(j.IsActive ? j with { State = JobState.Interrupted, Progress = null,
                        Error = "FileShift stopped before this conversion finished. Retry to start again." } : j)));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { PersistenceError = e.Message; }
        workers = Enumerable.Range(0, Math.Clamp(concurrency, 1, 4)).Select(_ => Task.Run(Work)).ToArray();
    }
    public IReadOnlyList<JobSnapshot> Snapshot() { lock (gate) return jobs.Select(j => j.Snapshot).Reverse().ToArray(); }
    public void RetryPersistence() { lock (gate) { if (PersistenceError != null) Save(); } }
    public Guid Enqueue(string source, string target, string? outputDirectory = null, string[]? archiveSources = null)
    {
        source = Path.GetFullPath(source);
        target = target.TrimStart('.').ToLowerInvariant();
        if (!File.Exists(source) && !(target == "zip" && Directory.Exists(source))) throw new FileNotFoundException("Source file not found.", source);
        Formats.Resolve(source, target, engines());
        var job = new Job(new(Guid.NewGuid(), source, target, outputDirectory, JobState.Queued, null, null, null, DateTimeOffset.Now, archiveSources));
        lock (gate)
        {
            if (stopping.IsCancellationRequested) throw new InvalidOperationException("The queue is shutting down.");
            jobs.Add(job);
            if (!channel.Writer.TryWrite(job)) { jobs.Remove(job); job.Cancellation.Dispose(); throw new InvalidOperationException("The queue is full. Wait for some conversions to finish."); }
            Trim(); Save();
        }
        return job.Snapshot.Id;
    }
    public void Cancel(Guid id)
    {
        lock (gate)
        {
            var job = jobs.FirstOrDefault(j => j.Snapshot.Id == id);
            if (job?.Snapshot.IsActive != true) return;
            job.Cancellation.Cancel();
            if (job.Snapshot.State == JobState.Queued) job.Snapshot = job.Snapshot with { State = JobState.Cancelled };
            Save();
        }
    }
    public void ClearFinished()
    {
        lock (gate)
        {
            foreach (var job in jobs.Where(j => !j.Snapshot.IsActive)) job.Cancellation.Dispose();
            jobs.RemoveAll(j => !j.Snapshot.IsActive); Save();
        }
    }
    private void Trim()
    {
        var finished = jobs.Where(j => !j.Snapshot.IsActive).ToArray();
        foreach (var job in finished.Take(Math.Max(0, finished.Length - 200))) { jobs.Remove(job); job.Cancellation.Dispose(); }
    }
    private async Task Work()
    {
        try
        {
            await foreach (var job in channel.Reader.ReadAllAsync(stopping.Token))
            {
                lock (gate)
                {
                    if (job.Snapshot.State != JobState.Queued) continue;
                    job.Snapshot = job.Snapshot with { State = JobState.Running }; Save();
                }
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, job.Cancellation.Token);
                try
                {
                    var update = new InlineProgress(value => { lock (gate) job.Snapshot = job.Snapshot with { Progress = value }; });
                    var output = job.Snapshot.Target == "zip"
                        ? await ZipConverter.CreateAsync(job.Snapshot.Sources ?? [job.Snapshot.Source], job.Snapshot.OutputDirectory, update, linked.Token)
                        : await new Converter(engines()).ConvertAsync(job.Snapshot.Source, job.Snapshot.Target, job.Snapshot.OutputDirectory, update, linked.Token);
                    lock (gate) job.Snapshot = job.Snapshot with { State = JobState.Completed, Output = output, Progress = 100 };
                }
                catch (OperationCanceledException) { lock (gate) job.Snapshot = job.Snapshot with { State = JobState.Cancelled, Progress = null }; }
                catch (Exception e) { lock (gate) job.Snapshot = job.Snapshot with { State = JobState.Failed, Error = e.Message, Progress = null }; }
                lock (gate) { Trim(); Save(); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private sealed class InlineProgress(Action<double?> update) : IProgress<double?> { public void Report(double? value) => update(value); }
    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            File.WriteAllText(historyPath + ".tmp", JsonSerializer.Serialize(jobs.Select(j => j.Snapshot).ToArray()));
            File.Move(historyPath + ".tmp", historyPath, true);
            PersistenceError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { PersistenceError = e.Message; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            channel.Writer.TryComplete(); stopping.Cancel();
            foreach (var job in jobs.Where(j => j.Snapshot.IsActive))
            {
                job.Cancellation.Cancel();
                if (job.Snapshot.State == JobState.Queued) job.Snapshot = job.Snapshot with { State = JobState.Cancelled };
            }
        }
        await Task.WhenAll(workers);
        lock (gate) { Save(); foreach (var job in jobs) job.Cancellation.Dispose(); }
        stopping.Dispose();
    }
}
