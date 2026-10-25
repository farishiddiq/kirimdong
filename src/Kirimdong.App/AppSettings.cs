using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kirimdong.App;

/// <summary>Stored in %AppData%\kirimdong\settings.json (one file per --profile, for running two copies while testing).</summary>
public sealed class AppSettings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string DeviceName { get; set; } = Environment.MachineName;
    public string SaveFolder { get; set; } = DefaultSaveFolder();
    public bool AutoAccept { get; set; }
    /// <summary>Device ids whose parcels are accepted without asking (ticked on their label or in the offer card).</summary>
    public List<string> TrustedPeers { get; set; } = new();
    /// <summary>Which edge of this PC's screen leads to which peer (device id → "left" / "right" / "top" / "bottom").</summary>
    public Dictionary<string, string> PeerEdges { get; set; } = new();
    /// <summary>Show the drop portal when a drag reaches a screen edge that leads to a peer.</summary>
    public bool EdgePortal { get; set; } = true;

    private string _path = "";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "kirimdong");

    public static AppSettings Load(string? profile)
    {
        var path = Path.Combine(Folder, string.IsNullOrEmpty(profile) ? "settings.json" : $"settings-{profile}.json");
        AppSettings settings;
        try
        {
            settings = File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new AppSettings();
        }
        settings._path = path;
        if (string.IsNullOrWhiteSpace(settings.DeviceId)) settings.DeviceId = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(settings.DeviceName)) settings.DeviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(settings.SaveFolder)) settings.SaveFolder = DefaultSaveFolder();
        settings.TrustedPeers ??= new();
        settings.PeerEdges ??= new();
        if (!File.Exists(path)) settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; the app keeps working with what is in memory.
        }
    }

    private static string DefaultSaveFolder() => Path.Combine(DownloadsFolder(), "kirimdong");

    private static string DownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr)!; }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
