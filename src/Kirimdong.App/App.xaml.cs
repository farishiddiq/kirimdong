using System;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Kirimdong.App.Controls;

namespace Kirimdong.App;

public partial class App : Application
{
    private Mutex? _single;
    private EventWaitHandle? _showSignal;

    /// <summary>
    /// Command line (for testing two copies on one PC):
    ///   --profile NAME   separate settings file and single-instance lock
    ///   --port N         TCP port to receive on (default 47801; falls back to any free port)
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // Helper modes: no window, no single-instance lock.
        if (e.Args.Length >= 1 && e.Args[0] == "--fix-firewall")
        {
            Shutdown(Services.ConnectionCheck.Fix());
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--diagnose")
        {
            var report = Services.ConnectionCheck.Run();
            System.IO.File.WriteAllText(e.Args[1], System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            Shutdown(0);
            return;
        }

        string? profile = null;
        int? port = null;
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--profile") profile = e.Args[i + 1];
            if (e.Args[i] == "--port" && int.TryParse(e.Args[i + 1], out var p)) port = p;
        }

        var key = "kirimdong-" + (profile ?? "default");
        _single = new Mutex(true, key + "-lock", out var first);
        if (!first)
        {
            // Already running: ask that copy to come to the front, then leave.
            try { EventWaitHandle.OpenExisting(key + "-show").Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, key + "-show");

        Resources["KraftBrush"] = Textures.Kraft((Color)Resources["KraftColor"], 11);
        Resources["KraftDarkBrush"] = Textures.Kraft((Color)Resources["KraftDarkColor"], 23);
        Resources["PaperBrush"] = Textures.Paper((Color)Resources["PaperColor"], 5);

        base.OnStartup(e);
        var window = new MainWindow(AppSettings.Load(profile), port);
        MainWindow = window;
        window.Show();

        var signal = _showSignal;
        var listener = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.BringToFront);
        }) { IsBackground = true, Name = "show-signal" };
        listener.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _single?.Dispose();
        base.OnExit(e);
    }
}
