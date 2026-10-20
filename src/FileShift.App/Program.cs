using System;
using System.IO;
using System.Linq;
using System.Windows;
using FileShift.Core;

namespace FileShift.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            if (args.Contains("--register-shell")) { ShellIntegration.Register(exe); return 0; }
            if (args.Contains("--unregister-shell")) { ShellIntegration.Unregister(exe); return 0; }
            using var instance = new SingleInstance();
            if (!instance.IsPrimary) return instance.Send(args).GetAwaiter().GetResult() ? 0 : 1;
            if (args.Contains("--quit")) return 0;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow();
            app.MainWindow = window;
            _ = instance.Listen(incoming => app.Dispatcher.InvokeAsync(() => window.Receive(incoming)));
            window.Receive(args);
            app.Run();
            return 0;
        }
        catch (Exception e)
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            File.WriteAllText(Path.Combine(Settings.DataDirectory, "last-error.log"), e.ToString());
            MessageBox.Show(e.Message, "FileShift", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
