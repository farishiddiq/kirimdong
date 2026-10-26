using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kirimdong.App.Interop;
using Kirimdong.App.ViewModels;
using Kirimdong.Core;

namespace Kirimdong.App;

/// <summary>
/// A small parcel card at a screen edge that follows one transfer: it slides in from that edge, shows the
/// progress, then offers Buka / Folder for what arrived. It never takes focus.
/// </summary>
public partial class ToastWindow : Window
{
    private static readonly TimeSpan StayAfterDone = TimeSpan.FromSeconds(7);

    private readonly TransferItem _item;
    private readonly DispatcherTimer _timer;
    private DateTime? _finishedAt;
    private bool _hovered;

    public ToastWindow(TransferItem item, string edge)
    {
        InitializeComponent();
        _item = item;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.MakeNoActivate(hwnd);
        Native.RoundCorners(hwnd);
        if (item.IsSend) ParcelIcon.Source = (System.Windows.Media.ImageSource)Application.Current.Resources["ImgPackage"];
        Heading.Text = item.IsSend ? "Mengirim ke" : "Paket masuk dari";
        Peer.Text = item.PeerName;
        Summary.Text = item.Summary;
        Place(edge);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Place(string edge)
    {
        Native.GetCursorPos(out var p);
        var m = Native.MonitorRect(p);
        Show();
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        int x, y;
        switch (edge)
        {
            case "left": x = m.Left + 12; y = m.Top + (m.Height - h) / 2; break;
            case "top": x = m.Left + (m.Width - w) / 2; y = m.Top + 12; break;
            case "bottom": x = m.Left + (m.Width - w) / 2; y = m.Bottom - h - 48; break; // above the taskbar
            default: x = m.Right - w - 12; y = m.Top + (m.Height - h) / 2; break;
        }
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        // Slide in from the edge itself, as if it came through it.
        var (dx, dy) = edge switch { "left" => (-Width, 0.0), "top" => (0.0, -Height), "bottom" => (0.0, Height), _ => (Width, 0.0) };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void Refresh()
    {
        Meter.Value = _item.Progress;
        Status.Text = _item.Status;
        var t = _item.Transfer;
        Meter.IsDone = _item.IsDone;
        Meter.IsProblem = _item.IsProblem;
        if (t.IsActive) return;

        _finishedAt ??= DateTime.UtcNow;
        Actions.Visibility = _item.CanOpen ? Visibility.Visible : Visibility.Collapsed;
        if (!_hovered && DateTime.UtcNow - _finishedAt > StayAfterDone) Close();
    }

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _hovered = true;

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovered = false;
        if (_finishedAt is not null) _finishedAt = DateTime.UtcNow; // give a few more seconds after looking at it
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var saved = _item.Transfer.SavedPaths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
        if (saved is null) return;
        try { Process.Start(new ProcessStartInfo(saved) { UseShellExecute = true }); }
        catch (Win32Exception) { }
        Close();
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        var t = _item.Transfer;
        var saved = t.SavedPaths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        var args = saved.Count == 1 ? $"/select,\"{saved[0]}\"" : $"\"{t.SaveFolder}\"";
        try { Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true }); }
        catch (Win32Exception) { }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
