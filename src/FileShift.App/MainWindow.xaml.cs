using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileShift.Core;
using Forms = System.Windows.Forms;

namespace FileShift.App;

public partial class MainWindow : Window
{
    private Settings settings = Settings.Load();
    private EnginePaths engines;
    private readonly ConversionQueue queue;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly DispatcherTimer timer;
    private readonly List<string> pending = [];
    private readonly Dictionary<string, TextBox> pathBoxes = new();
    private readonly Dictionary<string, TextBox> adobeBoxes = new();
    private IReadOnlyList<JobSnapshot> last = [];
    private bool quitting;
    private readonly string exe = Environment.ProcessPath!;
    private sealed record TargetChoice(string Target, bool Available)
    {
        public override string ToString() => Target.ToUpperInvariant() + (Available ? "" : " · needs engine");
    }
    public MainWindow()
    {
        InitializeComponent();
        engines = new EnginePaths(settings);
        if (ShellIntegration.IsRegistered(exe)) ShellIntegration.RefreshPolicy(exe, engines);
        queue = new ConversionQueue(() => engines);
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/FileShift;component/Assets/FileShift.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = "FileShift — ready", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Conversion status", null, (_, _) => Dispatcher.Invoke(OpenWindow));
        menu.Items.Add("Add files…", null, (_, _) => Dispatcher.Invoke(() => { OpenWindow(); AddFiles(this, new RoutedEventArgs()); }));
        menu.Items.Add("Exit FileShift", null, async (_, _) => await Dispatcher.InvokeAsync(Quit));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(OpenWindow); };
        tray.BalloonTipClicked += (_, _) => Dispatcher.Invoke(OpenWindow);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshJobs();
        timer.Start();
        RefreshEngines(); RefreshJobs();
    }
    public void Receive(string[] args)
    {
        if (args.Contains("--quit")) { _ = Quit(); return; }
        var requestIndex = Array.IndexOf(args, "--shell-request");
        if (requestIndex >= 0)
        {
            try
            {
                if (requestIndex + 1 >= args.Length) throw new InvalidDataException("Explorer request is missing.");
                var request = ShellRequest.ReadAndDelete(args[requestIndex + 1]);
                if (request.Target == null) { AddPending(request.Files); OpenWindow(); }
                else
                {
                    engines = new EnginePaths(settings);
                    var count = request.Enqueue(queue, engines).Count;
                    StatusMessage.Text = $"Queued {count} {request.Target.ToUpperInvariant()} conversion(s) from Explorer. Outputs go beside the originals.";
                    RefreshJobs();
                    tray.ShowBalloonTip(3000, "FileShift", StatusMessage.Text, Forms.ToolTipIcon.Info);
                }
            }
            catch (Exception error)
            {
                StatusMessage.Text = error.Message;
                tray.ShowBalloonTip(5000, "Could not queue conversion", error.Message, Forms.ToolTipIcon.Warning);
                OpenWindow();
            }
            return;
        }
        var index = Array.IndexOf(args, "--convert");
        if (index >= 0) AddPending(args.Skip(index + 1).Take(100));
        if (!args.Contains("--background") || index >= 0) OpenWindow();
    }
    private void OpenWindow() { Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; Activate(); }
    private void AddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files to convert", Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) == true) AddPending(dialog.FileNames);
    }
    private void FilesDragged(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void FilesDropped(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPending(files); }
    private void AddPending(IEnumerable<string> files)
    {
        var rejected = 0;
        foreach (var file in files)
        {
            if (!File.Exists(file) && !Directory.Exists(file)) { rejected++; continue; }
            if (pending.Count >= 100) { rejected++; continue; }
            var full = Path.GetFullPath(file);
            if (!pending.Contains(full, StringComparer.OrdinalIgnoreCase)) pending.Add(full);
        }
        ShowQueue(this, new RoutedEventArgs()); UpdateSelection();
        if (rejected > 0) StatusMessage.Text = $"Skipped {rejected} unsupported files/folders or files beyond the 100-file batch limit.";
    }
    private void ClearFiles(object sender, RoutedEventArgs e) { pending.Clear(); UpdateSelection(); }
    private void UpdateSelection()
    {
        SelectionTitle.Text = pending.Count == 0 ? "Drop your files here" : $"{pending.Count} file{(pending.Count == 1 ? "" : "s")} ready to convert";
        SelectionDetail.Text = pending.Count == 0 ? "Documents, images, audio, video, and more" : string.Join(" · ", pending.Select(Path.GetFileName));
        var previous = (TargetFormat.SelectedItem as TargetChoice)?.Target;
        var routes = pending.Select(p => Directory.Exists(p) ? Formats.AvailableRoutes("folder", engines) : Formats.AvailableRoutes(p, engines)).ToArray();
        var targets = routes.Length == 0 ? [] : routes[0].Select(r => r.Target).Where(t => routes.All(rs => rs.Any(r => r.Target == t))).ToArray();
        var choices = targets.Select(t => new TargetChoice(t, routes.All(rs => rs.Any(r => r.Target == t && engines.Available(r.Engine))))).ToArray();
        TargetFormat.ItemsSource = choices;
        TargetFormat.SelectedItem = choices.FirstOrDefault(c => c.Target == previous) ?? choices.FirstOrDefault(c => c.Available && c.Target == "pdf")
            ?? choices.FirstOrDefault(c => c.Available) ?? choices.FirstOrDefault();
        UpdateTarget();
    }
    private void TargetChanged(object sender, SelectionChangedEventArgs e) { if (ConvertButton != null) UpdateTarget(); }
    private void UpdateTarget()
    {
        var choice = TargetFormat.SelectedItem as TargetChoice;
        ConvertButton.IsEnabled = choice?.Available == true && pending.Count > 0;
        if (pending.Count == 0) RouteHint.Text = "Original files are preserved. Existing outputs get a numbered filename.";
        else if (choice == null) RouteHint.Text = "These files have no common target. Convert them in separate batches by file type.";
        else
        {
            var routes = pending.Select(p => Formats.AvailableRoutes(Directory.Exists(p) ? "folder" : p, engines).First(r => r.Target == choice.Target)).ToArray();
            RouteHint.Text = choice.Available ? string.Join(" · ", routes.Select(r => r.Description).Distinct()) : "Install or configure: " + string.Join(", ", routes.Where(r => !engines.Available(r.Engine)).Select(r => r.Engine).Distinct());
        }
    }
    private void BrowseOutput(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose output folder" };
        if (dialog.ShowDialog(this) == true) OutputFolder.Text = dialog.FolderName;
    }
    private void ConvertFiles(object sender, RoutedEventArgs e)
    {
        if (TargetFormat.SelectedItem is not TargetChoice choice || !choice.Available) return;
        var failed = new List<string>(); var accepted = new List<string>();
        if (choice.Target == "zip")
        {
            try { queue.Enqueue(pending[0], "zip", string.IsNullOrWhiteSpace(OutputFolder.Text) ? null : OutputFolder.Text.Trim(), pending.ToArray()); pending.Clear(); UpdateSelection(); RefreshJobs(); StatusMessage.Text = "Queued one ZIP archive containing the selected items."; }
            catch (Exception error) { StatusMessage.Text = error.Message; }
            return;
        }
        foreach (var file in pending)
        {
            try { queue.Enqueue(file, choice.Target, string.IsNullOrWhiteSpace(OutputFolder.Text) ? null : OutputFolder.Text.Trim()); accepted.Add(file); }
            catch (Exception error) { failed.Add(error.Message); }
        }
        pending.RemoveAll(accepted.Contains); UpdateSelection(); RefreshJobs();
        StatusMessage.Text = failed.Count > 0 ? string.Join("; ", failed.Distinct()) : $"Queued {accepted.Count} conversion(s). Originals are preserved.";
    }
    private void RefreshJobs()
    {
        queue.RetryPersistence();
        var snapshots = queue.Snapshot();
        if (!snapshots.SequenceEqual(last))
        {
            var selected = (JobsGrid.SelectedItem as JobSnapshot)?.Id;
            JobsGrid.ItemsSource = snapshots;
            JobsGrid.SelectedItem = snapshots.FirstOrDefault(j => j.Id == selected);
            foreach (var job in snapshots.Where(j => j.State is JobState.Completed or JobState.Failed))
            {
                if (last.Any(j => j.Id == job.Id && j.IsActive))
                    tray.ShowBalloonTip(3000, job.State == JobState.Completed ? "Conversion complete" : "Conversion failed", job.Name + " → " + job.Target.ToUpperInvariant(), job.State == JobState.Completed ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning);
            }
            last = snapshots;
        }
        var active = snapshots.Count(j => j.IsActive); var done = snapshots.Count(j => j.State == JobState.Completed);
        Counts.Text = $"{active} active  ·  {done} completed";
        tray.Text = active > 0 ? $"FileShift — {active} conversion(s) active" : "FileShift — ready";
        EmptyQueue.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (queue.PersistenceError != null) StatusMessage.Text = "Could not save history: " + queue.PersistenceError;
        JobSelected(this, null!);
    }
    private void JobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CancelButton == null) return;
        var job = JobsGrid.SelectedItem as JobSnapshot;
        CancelButton.IsEnabled = job?.IsActive == true; RetryButton.IsEnabled = job?.CanRetry == true;
        RevealButton.IsEnabled = job?.Output != null;
        if (job?.Error != null) StatusMessage.Text = job.Error;
        else if (job?.Output != null) StatusMessage.Text = job.Output;
    }
    private void CancelJob(object sender, RoutedEventArgs e) { if (JobsGrid.SelectedItem is JobSnapshot job) queue.Cancel(job.Id); RefreshJobs(); }
    private void RetryJob(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is not JobSnapshot job) return;
        try { queue.Enqueue(job.Source, job.Target, job.OutputDirectory, job.Sources); RefreshJobs(); }
        catch (Exception error) { StatusMessage.Text = error.Message; }
    }
    private void RevealOutput(object sender, RoutedEventArgs e)
    {
        if (JobsGrid.SelectedItem is JobSnapshot job && job.Output != null)
        {
            if (!File.Exists(job.Output)) { StatusMessage.Text = "The output file was moved or deleted."; return; }
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false }; info.ArgumentList.Add("/select,"); info.ArgumentList.Add(job.Output); Process.Start(info);
        }
    }
    private void ClearHistory(object sender, RoutedEventArgs e) { queue.ClearFinished(); RefreshJobs(); }
    private void ShowQueue(object sender, RoutedEventArgs e) { QueuePage.Visibility = Visibility.Visible; SettingsPage.Visibility = Visibility.Collapsed; PageTitle.Text = "Your conversion workspace"; PageSubtitle.Text = "Drop files, choose a format, and let FileShift handle the rest."; }
    private void ShowSettings(object sender, RoutedEventArgs e) { QueuePage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible; PageTitle.Text = "Engines & setup"; PageSubtitle.Text = "Choose your tools. Keep your workflow local."; engines = new EnginePaths(settings); if (ShellIntegration.IsRegistered(exe)) ShellIntegration.RefreshPolicy(exe, engines); RefreshEngines(); }
    private void RefreshEngines()
    {
        EngineSummary.Text = "✓  Built-in images & ZIP\n" + string.Join("\n", EnginePaths.ExternalEngines.Select(e => (engines.Available(e) ? "✓  " : "○  ") + e));
        EngineRows.Children.Clear(); pathBoxes.Clear();
        foreach (var engine in EnginePaths.ExternalEngines)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            row.Children.Add(new TextBlock { Text = engine + (engines.Available(engine) ? "  ·  Available" : "  ·  Not installed"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5) });
            var path = new TextBox { Text = settings.EngineOverrides.GetValueOrDefault(engine) ?? "", ToolTip = engines[engine] ?? "Automatic detection; enter an executable path to override", Tag = engine };
            pathBoxes.Add(engine, path); row.Children.Add(path);
            row.Children.Add(new TextBlock { Text = engines[engine] ?? "Use the installer to obtain this engine.", Foreground = System.Windows.Media.Brushes.SlateGray, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            EngineRows.Children.Add(row);
        }
        adobeBoxes.Clear();
        EngineRows.Children.Add(new TextBlock { Text = "Premiere / AME presets (experimental)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
        EngineRows.Children.Add(new TextBlock { Text = "AME 22.5+; one root sequence per project. Close AME before each job. Cancellation stops monitoring; stop the render in AME. Adobe apps require your own licensed installation.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var target in new[] { "mp4", "mov", "mp3", "wav" })
        {
            EngineRows.Children.Add(new TextBlock { Text = target.ToUpperInvariant() + " · exported .epr preset", FontSize = 11 });
            var box = new TextBox { Text = settings.AdobePresets.GetValueOrDefault(target) ?? "", Margin = new Thickness(0, 4, 0, 10) };
            adobeBoxes.Add(target, box); EngineRows.Children.Add(box);
        }
        ShellStatus.Text = ShellIntegration.IsRegistered(exe) ? "Explorer command is enabled. Windows 11: look under ‘Show more options’." : "Explorer command is not enabled for this copy of FileShift.";
        StartupCheck.IsChecked = ShellIntegration.StartupEnabled;
    }
    private void SaveEngines(object sender, RoutedEventArgs e)
    {
        try
        {
            settings.EngineOverrides = pathBoxes.ToDictionary(p => p.Key, p => p.Value.Text.Trim().Trim('"'));
            settings.AdobePresets = adobeBoxes.ToDictionary(p => p.Key, p => p.Value.Text.Trim().Trim('"'));
            settings.Save(); engines = new EnginePaths(settings); if (ShellIntegration.IsRegistered(exe)) ShellIntegration.RefreshPolicy(exe, engines); RefreshEngines(); UpdateSelection(); StatusMessage.Text = "Engine paths saved and Explorer formats refreshed.";
        }
        catch (Exception error) { StatusMessage.Text = error.Message; }
    }
    private void RunInstaller(object sender, RoutedEventArgs e)
    {
        var installer = Path.Combine(Settings.DataDirectory, "FileShiftSetup.exe");
        if (!File.Exists(installer)) { StatusMessage.Text = "Run FileShiftSetup.exe from the release folder to install additional engines."; return; }
        Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true });
    }
    private void EnableShell(object sender, RoutedEventArgs e) { TrySetting(() => ShellIntegration.Register(exe)); }
    private void DisableShell(object sender, RoutedEventArgs e) { TrySetting(() => ShellIntegration.Unregister(exe)); }
    private void StartupChanged(object sender, RoutedEventArgs e) { TrySetting(() => ShellIntegration.SetStartup(exe, StartupCheck.IsChecked == true)); }
    private void TrySetting(Action action) { try { action(); RefreshEngines(); } catch (Exception error) { StatusMessage.Text = error.Message; } }
    private void WindowClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; Hide(); } }
    private async Task Quit()
    {
        if (quitting) return; quitting = true; timer.Stop(); Hide(); tray.Visible = false;
        await queue.DisposeAsync(); tray.Dispose(); trayIcon.Dispose(); Application.Current.Shutdown();
    }
}
