using System.Diagnostics;
using System.Reflection;
using FileShift.Core;

namespace FileShift.Setup;

public sealed class SetupForm : Form
{
    private readonly Dictionary<EnginePackage, CheckBox> choices = new();
    private readonly CheckBox explorer = new() { Text = "Add the ‘Convert to’ submenu to the Explorer context menu", Checked = true, AutoSize = true };
    private readonly CheckBox startup = new() { Text = "Start in the system tray when I sign in", AutoSize = true };
    private readonly CheckBox launch = new() { Text = "Open FileShift when setup finishes", Checked = true, AutoSize = true };
    private readonly CheckBox agreements = new() { Text = "I accept the selected engine licenses and winget source agreements (where applicable).", AutoSize = true };
    private readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(245, 248, 250), BorderStyle = BorderStyle.FixedSingle };
    private readonly Button install = new() { Text = "Install FileShift", AutoSize = true, Padding = new Padding(18, 9, 18, 9), BackColor = Color.FromArgb(8, 127, 140), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly Label status = new() { Text = "The app and .NET runtime are bundled. Optional engine downloads require internet.", AutoSize = true };
    private readonly bool uninstall;
    private bool busy;
    private bool finished;

    public SetupForm(bool uninstall)
    {
        this.uninstall = uninstall;
        Text = uninstall ? "Uninstall FileShift" : "FileShift Setup";
        ClientSize = new Size(860, 790); MinimumSize = new Size(800, 740); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Color.White;
        using var iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileShift.Branding.Icon.ico")!;
        using var resourceIcon = new Icon(iconStream);
        Icon = (Icon)resourceIcon.Clone();
        using var logoStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FileShift.Branding.Logo.png")!;
        using var resourceLogo = Image.FromStream(logoStream);
        var logo = new Bitmap(resourceLogo);
        Disposed += (_, _) => { Icon?.Dispose(); logo.Dispose(); };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 6 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(grid);
        var header = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 0, 0, 16) };
        header.Controls.Add(new PictureBox { Image = logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(64, 64), Margin = Padding.Empty, AccessibleName = "FileShift logo" });
        var title = new Label { Text = uninstall ? "Remove FileShift" : "FileShift  /  Setup", Font = new Font("Segoe UI", 25, FontStyle.Bold), ForeColor = Color.FromArgb(18, 34, 52), AutoSize = true, Margin = new Padding(14, 9, 0, 0) };
        header.Controls.Add(title);
        grid.Controls.Add(header, 0, 0);
        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
        grid.Controls.Add(options, 0, 1);
        if (!uninstall)
        {
            options.Controls.Add(new Label { Text = "Choose conversion engines", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) });
            var detected = new EnginePaths(Settings.Load());
            foreach (var package in EnginePackages.All)
            {
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 5) };
                var exists = detected.Available(package.Name);
                var check = new CheckBox { Text = $"{package.Name} — {package.Purpose}" + (exists ? "  [detected]" : ""), Checked = !exists, AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
                choices.Add(package, check); row.Controls.Add(check);
                var link = new LinkLabel { Text = "License", AutoSize = true, Margin = new Padding(4, 5, 4, 0) };
                link.LinkClicked += (_, _) => OpenUrl(package.LicenseUrl); row.Controls.Add(link);
                var download = new LinkLabel { Text = "Download", AutoSize = true, Margin = new Padding(4, 5, 0, 0) };
                download.LinkClicked += (_, _) => OpenUrl(package.DownloadUrl); row.Controls.Add(download); options.Controls.Add(row);
            }
            options.Controls.Add(new Label { Text = "Built in: PNG/JPEG/BMP/GIF/TIFF and ZIP. Adobe apps are obtained separately through Creative Cloud.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 12) });
            options.Controls.Add(explorer); options.Controls.Add(startup); options.Controls.Add(launch);
            options.Controls.Add(new Label { Text = "Windows 11: this menu entry appears under ‘Show more options’.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 8, 0, 10) });
            options.Controls.Add(agreements);
            var terms = new LinkLabel { Text = "Windows Package Manager source agreements", AutoSize = true };
            terms.LinkClicked += (_, _) => OpenUrl("https://aka.ms/winget-source-agreements"); options.Controls.Add(terms);
        }
        else
        {
            options.Controls.Add(new Label { Text = "Removes the app, Explorer commands, sign-in entry, and Start menu shortcut.\nYour output files, settings, history, and shared conversion engines will be kept.", AutoSize = true, Margin = new Padding(0, 4, 0, 14) });
            install.Text = "Uninstall FileShift";
        }
        grid.Controls.Add(new Label { Text = "Installation progress", AutoSize = true, Margin = new Padding(0, 0, 0, 8) }, 0, 2);
        grid.Controls.Add(logBox, 0, 3);
        status.MaximumSize = new Size(780, 0); status.Margin = new Padding(0, 12, 0, 12); grid.Controls.Add(status, 0, 4);
        var footer = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
        footer.Controls.Add(install); grid.Controls.Add(footer, 0, 5);
        install.Click += async (_, _) => await Run();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Wait for the current installer operation to finish. Package installers may show an administrator prompt."; } };
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private void Log(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(text)); return; }
        logBox.AppendText(text.Trim() + Environment.NewLine);
    }
    private async Task Run()
    {
        if (finished) { Close(); return; }
        var selected = choices.Where(p => p.Value.Checked).Select(p => p.Key).ToArray();
        if (selected.Length > 0 && !agreements.Checked) { status.Text = "Review the linked package licenses and accept the agreements, or uncheck the engines to install just the app."; return; }
        busy = true; install.Enabled = false;
        foreach (var choice in choices.Values) choice.Enabled = false;
        explorer.Enabled = startup.Enabled = launch.Enabled = agreements.Enabled = false;
        try
        {
            if (uninstall)
            {
                await InstallService.Uninstall(Log); status.Text = "FileShift has been removed.";
            }
            else
            {
                var shellChoice = explorer.Checked; var startupChoice = startup.Checked;
                // ZIP extraction and shell registration need no administrator rights.
                await InstallService.Install(shellChoice, startupChoice, Log);
                var failed = new List<string>();
                foreach (var package in selected)
                {
                    status.Text = "Installing " + package.Name + "… Check for an administrator prompt if progress pauses.";
                    try { if (await EnginePackages.Install(package, Log) == null) failed.Add(package.Name); }
                    catch (Exception e) { Log(package.Name + ": " + e.Message); failed.Add(package.Name); }
                }
                if (shellChoice) ShellIntegration.RefreshPolicy(InstallService.Executable, new EnginePaths(Settings.Load()));
                status.Text = failed.Count == 0 ? "Setup complete. FileShift is ready." : "App installed. Engines needing attention: " + string.Join(", ", failed) + ". See the log and official download links.";
                Directory.CreateDirectory(InstallService.Root);
                File.WriteAllText(Path.Combine(InstallService.Root, "setup.log"), logBox.Text);
                if (launch.Checked) Process.Start(new ProcessStartInfo(InstallService.Executable) { UseShellExecute = true });
            }
            install.Text = "Close"; finished = true; install.Enabled = true;
        }
        catch (Exception error) { Log(error.ToString()); status.Text = "Setup failed: " + error.Message; install.Enabled = true; foreach (var choice in choices.Values) choice.Enabled = true; explorer.Enabled = startup.Enabled = launch.Enabled = agreements.Enabled = true; }
        finally { busy = false; }
    }
}
