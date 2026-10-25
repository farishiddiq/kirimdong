using System;
using System.Windows.Threading;
using Kirimdong.App.Interop;

namespace Kirimdong.App.Services;

/// <summary>
/// Watches for a drag that reaches an outer screen edge leading to a peer, and opens the portal there.
/// A drag counts when the primary button went down at least 60px away from that edge (so holding a scrollbar
/// at the edge never triggers it), the cursor has rested on the edge for a moment, and the foreground app is
/// not moving or resizing a window (Snap).
/// </summary>
public sealed class EdgeWatcher : IDisposable
{
    private const int StartAwayFromEdge = 60;
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(400);

    private readonly PortalWindow _portal;
    private readonly Func<string, (string Id, string Name)?> _peerAtEdge;
    private readonly DispatcherTimer _timer;
    private bool _wasDown;
    private Native.POINT _pressedAt;
    private DateTime _atEdgeSince = DateTime.MaxValue;
    private DateTime _releasedAt = DateTime.MaxValue;
    private string? _openEdge;
    private string? _openPeer;

    public EdgeWatcher(PortalWindow portal, Func<string, (string Id, string Name)?> peerAtEdge)
    {
        _portal = portal;
        _peerAtEdge = peerAtEdge;
        _portal.FilesDropped += paths =>
        {
            if (_openPeer is { } id && _openEdge is { } edge) Dropped?.Invoke(id, edge, paths);
            Close();
        };
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>(peer id, edge, dropped paths)</summary>
    public event Action<string, string, string[]>? Dropped;

    public bool Enabled
    {
        get => _timer.IsEnabled;
        set
        {
            if (value) _timer.Start();
            else { _timer.Stop(); Close(); }
        }
    }

    private void Tick()
    {
        Native.GetCursorPos(out var p);
        var down = Native.LeftButtonDown();
        if (down && !_wasDown) _pressedAt = p;
        _wasDown = down;

        if (_openEdge is not null)
        {
            // Open: stay while the drag is on its way or inside; close shortly after release or when the cursor leaves.
            if (down) _releasedAt = DateTime.MaxValue;
            else if (_releasedAt == DateTime.MaxValue) _releasedAt = DateTime.UtcNow;
            var gone = !down && DateTime.UtcNow - _releasedAt > Linger;
            var away = !_portal.IsDragInside && EdgeAt(p, 520) != _openEdge; // wider than the portal at 200% scale
            if (gone || away) Close();
            return;
        }

        var edge = down ? EdgeAt(p, 1) : null;
        if (edge is null || EdgeAt(_pressedAt, StartAwayFromEdge) == edge || Native.ForegroundBusyWithMouse())
        {
            _atEdgeSince = DateTime.MaxValue;
            return;
        }
        if (_atEdgeSince == DateTime.MaxValue) _atEdgeSince = DateTime.UtcNow;
        if (DateTime.UtcNow - _atEdgeSince < Dwell) return;
        if (_peerAtEdge(edge) is not { } peer) return;

        _openEdge = edge;
        _openPeer = peer.Id;
        _releasedAt = DateTime.MaxValue;
        _portal.ShowAt(edge, peer.Name, Native.MonitorRect(p), p);
    }

    private void Close()
    {
        _openEdge = null;
        _openPeer = null;
        _atEdgeSince = DateTime.MaxValue;
        if (_portal.IsVisible) _portal.Hide();
    }

    /// <summary>Which outer edge of the desktop the point is within <paramref name="margin"/> pixels of, if any.
    /// An edge between two monitors does not count.</summary>
    private static string? EdgeAt(Native.POINT p, int margin)
    {
        var m = Native.MonitorRect(p);
        if (p.X >= m.Right - margin && !Native.HasMonitorAt(m.Right + 2, p.Y)) return "right";
        if (p.X <= m.Left + margin - 1 && !Native.HasMonitorAt(m.Left - 2, p.Y)) return "left";
        if (p.Y <= m.Top + margin - 1 && !Native.HasMonitorAt(p.X, m.Top - 2)) return "top";
        if (p.Y >= m.Bottom - margin && !Native.HasMonitorAt(p.X, m.Bottom + 2)) return "bottom";
        return null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _portal.Close();
    }
}
