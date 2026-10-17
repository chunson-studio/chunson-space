using System.IO;
using System.Text.Json;
using System.Windows;

namespace ChunsonLogin;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--check-network")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var report = Path.GetFullPath(e.Args[1]);
            if (File.Exists(report)) { Shutdown(2); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            try
            {
                var result = await new EnvironmentCoordinator(new FlClashNetwork(), new EgressProbe(), new SystemTimezone(), new SystemRegion()).DetectAsync(CancellationToken.None);
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = true, result, timezoneWrites = 0, regionWrites = 0, browserLaunches = 0 }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed = false, error = ex.Message, timezoneWrites = 0, regionWrites = 0, browserLaunches = 0 }));
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var output = Path.GetFullPath(e.Args[1]);
            if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) { Shutdown(2); return; }
            Directory.CreateDirectory(output);
            try { await VerificationSuite.RunAsync(output); Shutdown(0); }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString());
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = false, error = ex.Message }));
                Shutdown(1);
            }
            return;
        }
        var preview = e.Args is ["--preview"];
        if (e.Args.Length > 0 && !preview) { Shutdown(2); return; }
        if (!preview)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!await Chunson.Setup.FlClashSetupWindow.EnsureAsync(async token => { await new FlClashNetwork().ResolveAsync(token); }))
            { Shutdown(); return; }
        }
        var model = preview ? LoginViewModel.Preview() : new LoginViewModel(
            new EnvironmentCoordinator(new FlClashNetwork(), new EgressProbe(), new SystemTimezone(), new SystemRegion()),
            new DedicatedBrowser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunsonLogin")),
            BrowserLocator.Find());
        MainWindow = new MainWindow(model);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MainWindow.Show();
        if (!preview) model.ShowClock();
    }
}
