using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Kirimdong.App.Interop;

namespace Kirimdong.App;

/// <summary>
/// The drop target that appears at a screen edge while something is being dragged there. It never takes
/// focus, so the drag (from Explorer, the desktop, a browser) carries on into it.
/// </summary>
public partial class PortalWindow : Window
{
    public PortalWindow()
    {
        InitializeComponent();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.MakeNoActivate(hwnd);
        Native.RoundCorners(hwnd);
    }

    /// <summary>Raised with the dropped paths; the portal hides itself first.</summary>
    public event Action<string[]>? FilesDropped;

    public bool IsDragInside { get; private set; }

    /// <summary>Shows the portal flush against <paramref name="edge"/> of the monitor rectangle (physical pixels).</summary>
    internal void ShowAt(string edge, string peerName, Native.RECT monitor, Native.POINT cursor)
    {
        PeerName.Text = peerName;
        Arrow.Text = edge switch { "left" => "", "top" => "", "bottom" => "", _ => "" };
        SetHot(false);

        // Size in physical pixels from the DIP size at this monitor's scale.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w, h, x, y;
        if (edge is "left" or "right")
        {
            w = (int)(260 * scale);
            h = Math.Min(monitor.Height - 40, (int)(420 * scale));
            x = edge == "left" ? monitor.Left : monitor.Right - w;
            y = Math.Clamp(cursor.Y - h / 2, monitor.Top + 20, monitor.Bottom - 20 - h);
        }
        else
        {
            w = Math.Min(monitor.Width - 40, (int)(460 * scale));
            h = (int)(240 * scale);
            x = Math.Clamp(cursor.X - w / 2, monitor.Left + 20, monitor.Right - 20 - w);
            y = edge == "top" ? monitor.Top : monitor.Bottom - h;
        }
        if (!IsVisible) Show();
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, Native.HWND_TOPMOST, x, y, w, h,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    public new void Hide()
    {
        IsDragInside = false;
        base.Hide();
    }

    private void SetHot(bool hot)
    {
        var res = Application.Current.Resources;
        Card.Background = (Brush)res[hot ? "Accent" : "Dark"];
        Background = Card.Background;
        Arrow.Foreground = (Brush)res[hot ? "OnInk" : "Accent"];
        Prompt.Foreground = (Brush)res[hot ? "OnInk" : "DarkInk2"];
        Prompt.Text = hot ? "Lepas sekarang, terkirim ke" : "Lepas di sini untuk kirim ke";
        Hint.Visibility = hot ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnDragEnter(object sender, DragEventArgs e) => OnDragOver(sender, e);

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        IsDragInside = files;
        SetHot(files);
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        IsDragInside = false;
        SetHot(false);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Handled = true;
        Hide();
        if (paths is { Length: > 0 }) FilesDropped?.Invoke(paths);
    }
}
