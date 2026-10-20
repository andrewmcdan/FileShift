using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using FileShift.Core;
using Microsoft.Win32;

namespace FileShift.Setup;

public static class InstallService
{
    public static string Root => Settings.DataDirectory;
    public static string AppDirectory => Path.Combine(Root, "App");
    public static string Executable => Path.Combine(AppDirectory, "FileShift.exe");
    public static string InstalledSetup => Path.Combine(Root, "FileShiftSetup.exe");
    private static string Receipt => Path.Combine(Root, "installation.json");
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FileShift";
    private static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "FileShift.lnk");

    public static async Task Install(bool explorer, bool startup, Action<string> log)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileShift.Payload.zip")
            ?? throw new InvalidOperationException("Installer payload is missing. Build this installer using scripts/Build.ps1.");
        Directory.CreateDirectory(Root);
        foreach (var obsolete in Directory.EnumerateDirectories(Root, "App.backup-*").Concat(Directory.EnumerateDirectories(Root, "App.removed-*")))
            if (File.Exists(Path.Combine(obsolete, ".fileshift-owned"))) TryDeleteOldApp(obsolete, log);
        var stage = Path.Combine(Root, "App.next-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(Root, "App.backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var swapped = false; var backedUp = false;
        try
        {
            log("Extracting the bundled app and .NET runtime…");
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries)
                {
                    var file = Path.GetFullPath(Path.Combine(stage, entry.FullName));
                    if (!file.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid payload path.");
                    if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(file); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    entry.ExtractToFile(file);
                }
            }
            if (!File.Exists(Path.Combine(stage, "FileShift.exe")) || !File.Exists(Path.Combine(stage, "FileShift.dll")) || !File.Exists(Path.Combine(stage, "FileShift.Shell.dll"))) throw new InvalidDataException("Incomplete app payload.");
            File.WriteAllText(Path.Combine(stage, ".fileshift-owned"), "FileShift 0.1");
            await StopApp();
            if (Directory.Exists(AppDirectory))
            {
                if (!File.Exists(Path.Combine(AppDirectory, ".fileshift-owned"))) throw new IOException("The app directory contains an unrecognized installation; it has been preserved.");
                Directory.Move(AppDirectory, backup); backedUp = true;
            }
            Directory.Move(stage, AppDirectory); swapped = true;
            if (!string.Equals(Environment.ProcessPath, InstalledSetup, StringComparison.OrdinalIgnoreCase))
                File.Copy(Environment.ProcessPath!, InstalledSetup, overwrite: true);
            if (explorer) ShellIntegration.Register(Executable); else ShellIntegration.Unregister(Executable);
            ShellIntegration.SetStartup(Executable, startup);
            CreateShortcut();
            using (var uninstall = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                uninstall.SetValue("DisplayName", "FileShift"); uninstall.SetValue("DisplayVersion", "0.1.0");
                uninstall.SetValue("Publisher", "FileShift"); uninstall.SetValue("InstallLocation", AppDirectory);
                uninstall.SetValue("DisplayIcon", Executable + ",0");
                uninstall.SetValue("UninstallString", $"\"{InstalledSetup}\" --uninstall");
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord); uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            File.WriteAllText(Receipt, JsonSerializer.Serialize(new { AppDirectory, Version = "0.1.0", InstalledAt = DateTimeOffset.Now }));
            if (backedUp)
            {
                TryDeleteOldApp(backup, log);
            }
            log("FileShift installed. Start menu shortcut and uninstall entry created.");
        }
        catch
        {
            if (swapped) DeleteOwned(AppDirectory);
            if (backedUp && Directory.Exists(backup)) Directory.Move(backup, AppDirectory);
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
    public static async Task Uninstall(Action<string> log)
    {
        if (!File.Exists(Receipt) || !File.Exists(Path.Combine(AppDirectory, ".fileshift-owned"))) throw new IOException("No recognized FileShift installation was found.");
        await StopApp();
        ShellIntegration.Unregister(Executable);
        using (var run = Registry.CurrentUser.OpenSubKey(ShellIntegration.RunPath, writable: true))
            if (string.Equals(run?.GetValue("FileShift") as string, $"\"{Executable}\" --background", StringComparison.OrdinalIgnoreCase)) run?.DeleteValue("FileShift", false);
        var removed = Path.Combine(Root, "App.removed-" + Guid.NewGuid().ToString("N"));
        Directory.Move(AppDirectory, removed);
        TryDeleteOldApp(removed, log);
        if (File.Exists(Shortcut)) File.Delete(Shortcut);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        File.Delete(Receipt);
        log("FileShift removed. Conversion engines, settings, conversion history, and the maintenance installer are retained.");
    }
    private static void DeleteOwned(string directory)
    {
        var full = Path.GetFullPath(directory);
        if (!full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(full, ".fileshift-owned"))) throw new IOException("Refusing to remove an unrecognized directory.");
        Directory.Delete(full, true);
    }
    private static void TryDeleteOldApp(string directory, Action<string> log)
    {
        try { DeleteOwned(directory); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A loaded shell DLL may remain mapped in Explorer until sign-out. Keep ownership
            // metadata even when recursive deletion already removed other files.
            if (Directory.Exists(directory))
            {
                try { File.WriteAllText(Path.Combine(directory, ".fileshift-owned"), "FileShift 0.1"); }
                catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { }
            }
            log("Windows is still using an older Explorer component. Its backup was retained; rerun setup after signing out to clean it up.");
        }
    }
    private static async Task StopApp()
    {
        if (!File.Exists(Executable)) return;
        var running = Process.GetProcessesByName("FileShift").Where(IsInstalledProcess).ToArray();
        if (running.Length == 0) return;
        using var quit = Process.Start(new ProcessStartInfo(Executable, "--quit") { UseShellExecute = false, CreateNoWindow = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { foreach (var process in running) { using (process) await process.WaitForExitAsync(timeout.Token); } }
        catch (OperationCanceledException) { throw new IOException("Close FileShift in all Windows sessions before installing or removing it."); }
    }
    private static bool IsInstalledProcess(Process process)
    {
        try { return string.Equals(process.MainModule?.FileName, Executable, StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
    private static void CreateShortcut()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(Shortcut);
        try { shortcut.TargetPath = Executable; shortcut.WorkingDirectory = AppDirectory; shortcut.IconLocation = Executable + ",0"; shortcut.Description = "FileShift — local file converter"; shortcut.Save(); }
        finally { Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
    }
}
