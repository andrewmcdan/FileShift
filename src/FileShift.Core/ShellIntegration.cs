using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FileShift.Core;

public static class ShellIntegration
{
    public const string VerbPath = @"Software\Classes\*\shell\FileShift.Convert";
    public const string FolderVerbPath = @"Software\Classes\Directory\shell\FileShift.Convert";
    public const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string PolicyPath = @"Software\FileShift\Shell";
    public const string HandlerId = "{B623BEF3-493E-49A1-AF1E-27C63770624B}";
    private const string ClassPath = @"Software\Classes\CLSID\" + HandlerId;
    public static void Register(string executable)
    {
        executable = Path.GetFullPath(executable);
        var library = Path.Combine(Path.GetDirectoryName(executable)!, "FileShift.Shell.dll");
        if (!File.Exists(executable) || executable.Contains('"')) throw new ArgumentException("Invalid executable path.");
        if (!File.Exists(library)) throw new FileNotFoundException("The Explorer submenu component is missing. Build with scripts/Build.ps1 or reinstall FileShift.", library);
        RefreshPolicy(executable, new EnginePaths(Settings.Load()));
        using (var server = Registry.CurrentUser.CreateSubKey(ClassPath + @"\InprocServer32"))
        {
            server.SetValue("", library); server.SetValue("ThreadingModel", "Apartment");
        }
        foreach (var path in new[] { VerbPath, FolderVerbPath })
        {
            using var verb = Registry.CurrentUser.CreateSubKey(path);
            verb.DeleteSubKeyTree("command", false);
            verb.DeleteValue("", false);
            verb.SetValue("MUIVerb", "Convert to");
            verb.SetValue("MultiSelectModel", "Player");
            verb.SetValue("ExplorerCommandHandler", HandlerId);
            verb.SetValue("FileShift.Executable", executable);
        }
        NotifyChanged();
    }
    public static void RefreshPolicy(string executable, EnginePaths engines)
    {
        var snapshot = ShellMenu.Snapshot(executable, engines);
        using var policy = Registry.CurrentUser.CreateSubKey(PolicyPath);
        if (policy.GetValue("MenuSnapshot") is string[] old && old.SequenceEqual(snapshot)) return;
        policy.SetValue("MenuSnapshot", snapshot, RegistryValueKind.MultiString);
        NotifyChanged();
    }
    public static bool IsRegistered(string executable) => Owned(VerbPath, executable);
    private static bool Owned(string path, string executable)
    {
        using var verb = Registry.CurrentUser.OpenSubKey(path);
        if (string.Equals(verb?.GetValue("FileShift.Executable") as string, executable, StringComparison.OrdinalIgnoreCase)) return true;
        using var command = verb?.OpenSubKey("command");
        return string.Equals(command?.GetValue("") as string, $"\"{executable}\" --convert \"%1\"", StringComparison.OrdinalIgnoreCase);
    }
    public static void Unregister(string executable)
    {
        foreach (var path in new[] { VerbPath, FolderVerbPath })
            if (Owned(path, executable)) Registry.CurrentUser.DeleteSubKeyTree(path, false);
        var library = Path.Combine(Path.GetDirectoryName(executable)!, "FileShift.Shell.dll");
        using (var server = Registry.CurrentUser.OpenSubKey(ClassPath + @"\InprocServer32"))
        {
            if (!string.Equals(server?.GetValue("") as string, library, StringComparison.OrdinalIgnoreCase)) { NotifyChanged(); return; }
        }
        Registry.CurrentUser.DeleteSubKeyTree(ClassPath, false);
        using (var policy = Registry.CurrentUser.OpenSubKey(PolicyPath))
        {
            var owner = (policy?.GetValue("MenuSnapshot") as string[])?.FirstOrDefault(s => s.StartsWith("X|", StringComparison.Ordinal));
            if (!string.Equals(owner, "X|" + executable, StringComparison.OrdinalIgnoreCase)) { NotifyChanged(); return; }
        }
        Registry.CurrentUser.DeleteSubKeyTree(PolicyPath, false);
        NotifyChanged();
    }
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    private static void NotifyChanged() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
    public static bool StartupEnabled
    {
        get { using var run = Registry.CurrentUser.OpenSubKey(RunPath); return run?.GetValue("FileShift") is string; }
    }
    public static void SetStartup(string executable, bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunPath);
        if (enabled) run.SetValue("FileShift", $"\"{executable}\" --background");
        else run.DeleteValue("FileShift", false);
    }
}
