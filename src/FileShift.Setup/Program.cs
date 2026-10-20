namespace FileShift.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--install-app-only"))
        {
            try { InstallService.Install(!args.Contains("--no-explorer"), false, _ => { }).GetAwaiter().GetResult(); return 0; }
            catch (Exception e) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "FileShift-setup-error.log"), e.ToString()); return 1; }
        }
        Application.Run(new SetupForm(args.Contains("--uninstall")));
        return 0;
    }
}
