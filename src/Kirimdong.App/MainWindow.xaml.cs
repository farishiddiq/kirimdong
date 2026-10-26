using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Kirimdong.App.Interop;
using Kirimdong.App.Services;
using Kirimdong.App.ViewModels;
using Kirimdong.Core;
using Microsoft.Win32;

namespace Kirimdong.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly TransferServer _server;
    private readonly DiscoveryService _discovery;
    private readonly ObservableCollection<PeerItem> _peers = new();
    private readonly ObservableCollection<TransferItem> _slips = new();
    private readonly Queue<OfferPrompt> _offers = new();
    private readonly DispatcherTimer _timer;
    private readonly EdgeWatcher _edges;
    private OfferPrompt? _currentOffer;
    private ConnectionReport? _report;
    private bool _loaded;

    public MainWindow(AppSettings settings, int? port, int? discoveryPort = null)
    {
        _settings = settings;
        InitializeComponent();

        NameBox.Text = settings.DeviceName;
        SaveFolderText.Text = settings.SaveFolder;
        SaveFolderText.ToolTip = settings.SaveFolder;
        AutoAcceptBox.IsChecked = settings.AutoAccept;
        PortalBox.IsChecked = settings.EdgePortal;
        PeerList.ItemsSource = _peers;
        SlipList.ItemsSource = _slips;

        _server = new TransferServer(Identity, () => _settings.SaveFolder, DecideAsync);
        _server.TransferStarted += t => Dispatcher.BeginInvoke(() => AddSlip(t));
        _server.HelloReceived += (hello, address) => _discovery?.Learn(hello, address);
        try { _server.Start(port ?? Protocol.DefaultTcpPort); }
        catch (SocketException) { _server.Start(0); } // port taken: any free port works, discovery tells others which

        _discovery = new DiscoveryService(Identity, discoveryPort ?? Protocol.DiscoveryPort);
        _discovery.PeersChanged += () => Dispatcher.BeginInvoke(SyncPeers);
        _discovery.SweepStateChanged += () => Dispatcher.BeginInvoke(SyncPeers);
        _discovery.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        _edges = new EdgeWatcher(new PortalWindow(), PeerAtEdge) { Enabled = settings.EdgePortal };
        _edges.Dropped += OnPortalDrop;

        ShowAddresses();
        SyncPeers();
        Loaded += async (_, _) => await RefreshConnectionAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            foreach (var slip in _slips.Where(s => s.IsActive)) slip.Tick();
            if (_currentOffer is not null)
            {
                _currentOffer.Tick();
                OfferCountdown.Text = _currentOffer.Countdown;
            }
        };
        _timer.Start();
        _loaded = true;
    }

    private Hello Identity() => new(_settings.DeviceId, _settings.DeviceName, _server.Port);

    // ----- this PC -----

    private void ShowAddresses()
    {
        var addresses = DiscoveryService.LocalAddresses();
        var text = addresses.Count == 0 ? "Tidak tersambung ke jaringan" : string.Join("  ·  ", addresses);
        if (_server.Port != Protocol.DefaultTcpPort) text += $"  (port {_server.Port})";
        MyAddress.Text = text;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(async () =>
    {
        ShowAddresses();
        _discovery.AnnounceNow();
        await RefreshConnectionAsync();
    });

    private void CommitName()
    {
        var name = Protocol.CleanName(NameBox.Text);
        NameBox.Text = name;
        if (name == _settings.DeviceName) return;
        _settings.DeviceName = name;
        _settings.Save();
        _discovery.AnnounceNow();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Escape) NameBox.Text = _settings.DeviceName;
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(this, this);
            e.Handled = true;
        }
    }

    private void NameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitName();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Simpan paket masuk ke folder", InitialDirectory = ExistingOrNull(_settings.SaveFolder) };
        if (dialog.ShowDialog(this) != true) return;
        _settings.SaveFolder = dialog.FolderName;
        _settings.Save();
        SaveFolderText.Text = dialog.FolderName;
        SaveFolderText.ToolTip = dialog.FolderName;
    }

    private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_settings.SaveFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "kirimdong", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AutoAccept_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.AutoAccept = AutoAcceptBox.IsChecked == true;
        _settings.Save();
    }

    private void Portal_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.EdgePortal = PortalBox.IsChecked == true;
        _settings.Save();
        _edges.Enabled = _settings.EdgePortal;
    }

    // ----- other PCs -----

    private void SyncPeers()
    {
        var current = _discovery.Peers;
        foreach (var gone in _peers.Where(p => current.All(c => c.Id != p.Id)).ToList()) _peers.Remove(gone);
        foreach (var peer in current)
        {
            var item = _peers.FirstOrDefault(p => p.Id == peer.Id);
            if (item is null)
            {
                // The first PC ever found gets the right-hand edge, so dragging past the edge works out of the box.
                if (_settings.PeerEdges.Count == 0)
                {
                    _settings.PeerEdges[peer.Id] = "right";
                    _settings.Save();
                }
                item = new PeerItem(peer, _settings.PeerEdges.GetValueOrDefault(peer.Id), _settings.TrustedPeers.Contains(peer.Id));
                item.TrustChanged += OnTrustChanged;
                _peers.Add(item);
            }
            else item.Update(peer);
        }
        EmptyState.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeerCount.Text = _peers.Count switch
        {
            0 when _discovery.IsSweeping => "Memindai jaringan…",
            0 => _discovery.ListenError is null ? "Mencari…" : "Pencarian otomatis mati, pakai IP",
            1 => "1 PC ditemukan",
            var n => $"{n} PC ditemukan",
        };
    }

    private void OnTrustChanged(PeerItem peer)
    {
        _settings.TrustedPeers.Remove(peer.Id);
        if (peer.IsTrusted) _settings.TrustedPeers.Add(peer.Id);
        _settings.Save();
    }

    /// <summary>One edge leads to one PC: choosing an edge takes it from any other PC; choosing it again clears it.</summary>
    private void Edge_Click(object sender, RoutedEventArgs e)
    {
        if (PeerOf(sender) is not { } peer || (sender as FrameworkElement)?.Tag is not string edge) return;
        var clear = peer.Edge == edge;
        foreach (var other in _peers.Where(p => p.Edge == edge)) other.Edge = null;
        foreach (var key in _settings.PeerEdges.Where(kv => kv.Value == edge || kv.Key == peer.Id).Select(kv => kv.Key).ToList())
            _settings.PeerEdges.Remove(key);
        if (!clear)
        {
            peer.Edge = edge;
            _settings.PeerEdges[peer.Id] = edge;
        }
        _settings.Save();
    }

    private (string Id, string Name)? PeerAtEdge(string edge)
    {
        var peer = _peers.FirstOrDefault(p => p.Edge == edge);
        return peer is null ? null : (peer.Id, peer.Name);
    }

    private void OnPortalDrop(string peerId, string edge, string[] paths)
    {
        if (_peers.FirstOrDefault(p => p.Id == peerId) is { } peer) Send(peer, paths, edge);
    }

    private async void AddByIp_Click(object sender, RoutedEventArgs e)
    {
        var text = IpBox.Text.Trim();
        if (text.Length == 0) { IpBox.Focus(); return; }
        var port = Protocol.DefaultTcpPort;
        var host = text;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            host = text[..colon];
            port = p;
        }

        IpStatus.Text = $"Menghubungi {text}…";
        try
        {
            var address = IPAddress.TryParse(host, out var ip)
                ? ip
                : (await Dns.GetHostAddressesAsync(host)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            // A hello (not just a ping) so the other PC learns about this one too.
            var hello = await TransferClient.HelloAsync(address, port, Identity(), TimeSpan.FromSeconds(5));
            if (hello.Id == _settings.DeviceId)
            {
                IpStatus.Text = "Itu alamat PC ini sendiri.";
                return;
            }
            _discovery.AddManual(hello, address);
            IpStatus.Text = $"Ketemu: {hello.Name}";
            IpBox.Clear();
        }
        catch (Exception)
        {
            IpStatus.Text = $"Tidak ada kirimdong di {text}. Pastikan app terbuka di PC itu dan firewall mengizinkannya.";
        }
    }

    private void IpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddByIp_Click(sender, e);
    }

    private static PeerItem? PeerOf(object sender) => (sender as FrameworkElement)?.DataContext as PeerItem;

    private void Peer_DragEnter(object sender, DragEventArgs e) => Peer_DragOver(sender, e);

    private void Peer_DragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        if (PeerOf(sender) is { } peer) peer.IsDropTarget = hasFiles;
        e.Handled = true;
    }

    private void Peer_DragLeave(object sender, DragEventArgs e)
    {
        if (PeerOf(sender) is { } peer) peer.IsDropTarget = false;
    }

    private void Peer_Drop(object sender, DragEventArgs e)
    {
        if (PeerOf(sender) is not { } peer) return;
        peer.IsDropTarget = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) Send(peer, paths);
        e.Handled = true;
    }

    private void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        if (PeerOf(sender) is not { } peer) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = $"Pilih file untuk {peer.Name}" };
        if (dialog.ShowDialog(this) == true) Send(peer, dialog.FileNames);
    }

    private void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (PeerOf(sender) is not { } peer) return;
        var dialog = new OpenFolderDialog { Multiselect = true, Title = $"Pilih folder untuk {peer.Name}" };
        if (dialog.ShowDialog(this) == true) Send(peer, dialog.FolderNames);
    }

    private async void Send(PeerItem peer, IReadOnlyList<string> paths, string? edge = null)
    {
        OutgoingBatch batch;
        try { batch = await Task.Run(() => FileCollector.Collect(paths)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (edge is not null) BringToFront(); // dropped on the portal: the window may be behind others
            MessageBox.Show(this, ex.Message, "kirimdong", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (batch.IsEmpty)
        {
            if (edge is not null) BringToFront();
            MessageBox.Show(this, "Tidak ada file yang bisa dikirim dari pilihan itu.", "kirimdong", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var transfer = TransferClient.CreateTransfer(peer.Name, batch);
        var item = AddSlip(transfer);
        // Through the portal the main window may be hidden: follow the parcel at the edge it left by.
        if (edge is not null) new ToastWindow(item, edge);
        var target = peer.EndPoint;
        var me = Identity();
        _ = Task.Run(() => TransferClient.SendAsync(target, me, batch, transfer, edge));
    }

    // ----- slips -----

    private TransferItem AddSlip(Transfer transfer)
    {
        var item = new TransferItem(transfer);
        var toasted = false;
        transfer.Changed += _ => Dispatcher.BeginInvoke(() =>
        {
            item.RefreshAll();
            if (transfer.Direction != TransferDirection.Receive) return;
            // An accepted parcel arrives at the edge facing the PC it came from (or the right edge),
            // unless kirimdong is already in front where its slip is visible.
            if (transfer.State == TransferState.Running && !toasted && (transfer.Edge is not null || !IsActive))
            {
                toasted = true;
                new ToastWindow(item, Opposite(transfer.Edge) ?? "right");
            }
            if (transfer.State == TransferState.Done && !IsActive) Flash();
        });
        _slips.Insert(0, item);
        UpdateSlipArea();
        return item;
    }

    private static string? Opposite(string? edge) => edge switch
    {
        "left" => "right",
        "right" => "left",
        "top" => "bottom",
        "bottom" => "top",
        _ => null,
    };

    private void UpdateSlipArea()
    {
        NoSlips.Visibility = _slips.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static TransferItem? SlipOf(object sender) => (sender as FrameworkElement)?.DataContext as TransferItem;

    private void Cancel_Click(object sender, RoutedEventArgs e) => SlipOf(sender)?.Transfer.Cancel();

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (SlipOf(sender) is { } slip) _slips.Remove(slip);
        UpdateSlipArea();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (SlipOf(sender)?.Transfer is not { } t) return;
        try
        {
            var saved = t.SavedPaths.Select(p => File.Exists(p) || Directory.Exists(p) ? p : null).OfType<string>().ToList();
            var args = saved.Count == 1 ? $"/select,\"{saved[0]}\"" : $"\"{t.SaveFolder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, "kirimdong", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ----- incoming offers -----

    private async Task<bool> DecideAsync(IncomingOffer offer, CancellationToken ct)
    {
        if (_settings.AutoAccept || _settings.TrustedPeers.Contains(offer.Offer.SenderId)) return true;
        var prompt = await Dispatcher.InvokeAsync(() =>
        {
            var p = new OfferPrompt(offer, _settings.SaveFolder, ct);
            _offers.Enqueue(p);
            p.Answer.ContinueWith(_ => Dispatcher.BeginInvoke(() => OfferClosed(p)), TaskScheduler.Default);
            ShowNextOffer();
            return p;
        });
        return await prompt.Answer.ConfigureAwait(false);
    }

    private void ShowNextOffer()
    {
        if (_currentOffer is not null) return;
        while (_offers.Count > 0)
        {
            var next = _offers.Dequeue();
            if (!next.IsOpen) continue;
            _currentOffer = next;
            OfferSender.Text = next.SenderName;
            OfferSummary.Text = next.Summary;
            OfferSize.Text = $"{next.Size}  ·  dari {next.From}";
            OfferFiles.ItemsSource = next.Preview;
            OfferMore.Text = next.More;
            OfferMore.Visibility = next.More.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OfferFolder.Text = next.SaveFolder;
            OfferCountdown.Text = next.Countdown;
            OfferTrust.IsChecked = false;
            OfferTrustText.Text = $"Lain kali langsung terima dari {next.SenderName}";
            OfferLayer.Visibility = Visibility.Visible;
            AcceptButton.Focus();
            Flash();
            SystemSounds.Asterisk.Play();
            return;
        }
        OfferLayer.Visibility = Visibility.Collapsed;
    }

    private void OfferClosed(OfferPrompt prompt)
    {
        if (_currentOffer != prompt) return;
        _currentOffer = null;
        ShowNextOffer();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_currentOffer is not { } offer) return;
        if (OfferTrust.IsChecked == true)
        {
            var id = offer.Offer.Offer.SenderId;
            if (!_settings.TrustedPeers.Contains(id)) _settings.TrustedPeers.Add(id);
            _settings.Save();
            _peers.FirstOrDefault(p => p.Id == id)?.SetTrustedQuietly(true);
        }
        offer.Respond(true);
    }

    // ----- connection check -----

    /// <summary>Re-reads network type and firewall rules; shows the banner when other PCs are being kept out.</summary>
    private async Task RefreshConnectionAsync()
    {
        _report = await ConnectionCheck.RunAsync();
        var network = _report.Networks.FirstOrDefault();
        var where = network is null ? "jaringan ini" : $"jaringan {network.Name} ({network.Category})";
        switch (_report.Firewall)
        {
            case FirewallState.NotAllowed:
                BannerText.Text = $"Firewall Windows menahan kiriman masuk di {where}, jadi PC lain tidak bisa menemukan atau mengirim ke PC ini. Tekan Izinkan, lalu pilih Ya di jendela izin Windows.";
                break;
            case FirewallState.Blocked:
                BannerText.Text = $"Izin firewall untuk kirimdong pernah ditolak di PC ini, jadi PC lain tidak bisa menemukan atau mengirim ke sini. Tekan Izinkan untuk membukanya lagi.";
                break;
        }
        var needsFix = _report.Firewall is FirewallState.NotAllowed or FirewallState.Blocked;
        FirewallBanner.Visibility = needsFix ? Visibility.Visible : Visibility.Collapsed;
        BannerButton.IsEnabled = true;
        BannerButton.Content = "Izinkan";
        if (CheckLayer.Visibility == Visibility.Visible) FillCheck();
    }

    private void FillCheck()
    {
        if (_report is null) return;
        var network = _report.Networks.FirstOrDefault();
        CheckNetwork.Text = network is null
            ? "Tidak tersambung ke jaringan."
            : $"{network.Name} · {network.Category}" + (network.Category == "Public"
                ? ". Windows memperlakukan Wi-Fi ini sebagai jaringan umum, jadi koneksi masuk diblokir kecuali app diizinkan."
                : ".");
        if (network?.LinkMbps is { } mbps && mbps > 0)
        {
            // Between two laptops on one access point every byte crosses the air twice; real throughput is roughly half of a third of the link rate.
            var estimate = Math.Max(1, mbps / 8.0 * 0.55 / 2);
            CheckSpeed.Text = $"Link {network.Adapter} {mbps} Mbps. Antar dua laptop di Wi-Fi yang sama, perkiraan sekitar {estimate:0} MB/dtk. " +
                              "Lebih cepat lewat 5 GHz dekat router, atau kabel LAN.";
        }
        else CheckSpeed.Text = "Kecepatan link tidak diketahui.";
        CheckFirewall.Text = _report.Firewall switch
        {
            FirewallState.Allowed => "kirimdong diizinkan menerima koneksi di jaringan ini.",
            FirewallState.Off => "Firewall Windows mati di jaringan ini; tidak ada yang menghalangi.",
            FirewallState.Blocked => "Diblokir. Izin untuk kirimdong pernah ditolak.",
            FirewallState.NotAllowed => $"Belum diizinkan di jaringan {_report.CurrentProfiles}. PC lain tidak bisa mengirim ke sini.",
            _ => "Tidak bisa dibaca.",
        };
        CheckFixButton.Visibility = _report.Firewall is FirewallState.NotAllowed or FirewallState.Blocked or FirewallState.Unknown
            ? Visibility.Visible : Visibility.Collapsed;
        CheckDiscovery.Text = (_discovery.ListenError is null
            ? "Siaran otomatis aktif. "
            : "Siaran otomatis tidak bisa didengar di PC ini; pencarian memakai pindai jaringan. ")
            + (_peers.Count == 0 ? "Belum ada PC lain yang ditemukan." : $"{_peers.Count} PC ditemukan.");
    }

    private async void CheckConnection_Click(object sender, RoutedEventArgs e)
    {
        CheckLayer.Visibility = Visibility.Visible;
        FillCheck();
        await RefreshConnectionAsync();
    }

    private void CloseCheck_Click(object sender, RoutedEventArgs e) => CheckLayer.Visibility = Visibility.Collapsed;

    private async void FixFirewall_Click(object sender, RoutedEventArgs e)
    {
        BannerButton.IsEnabled = false;
        BannerButton.Content = "Menunggu izin…";
        CheckFixButton.IsEnabled = false;
        var ok = await ConnectionCheck.RequestFixAsync();
        CheckFixButton.IsEnabled = true;
        await RefreshConnectionAsync();
        if (ok && _report?.Firewall is FirewallState.Allowed)
        {
            _discovery.AnnounceNow();
            _ = _discovery.SweepAsync();
        }
        else if (!ok)
        {
            BannerText.Text = "Belum diizinkan: jendela izin Windows ditutup atau ditolak. Tekan Izinkan lagi dan pilih Ya.";
        }
    }

    private async void Sweep_Click(object sender, RoutedEventArgs e)
    {
        SweepButton.IsEnabled = false;
        CheckDiscovery.Text = "Menanyai setiap alamat di jaringan ini…";
        var found = await _discovery.SweepAsync();
        SweepButton.IsEnabled = true;
        FillCheck();
        CheckDiscovery.Text += found > 0 ? $" Pindai menjangkau {found} PC."
            : _peers.Count > 0 ? " Pindai tidak menemukan PC tambahan."
            : " Pindai tidak menemukan kirimdong lain. Pastikan app terbuka di PC itu dan firewall-nya sudah diizinkan.";
    }

    private void Decline_Click(object sender, RoutedEventArgs e) => _currentOffer?.Respond(false);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_currentOffer is not null && e.Key == Key.Escape)
        {
            _currentOffer.Respond(false);
            e.Handled = true;
        }
        base.OnPreviewKeyDown(e);
    }

    // ----- window -----

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void Flash() => Native.Flash(new WindowInteropHelper(this).Handle);

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        // A maximised chrome-less window hangs over the screen edge by the resize border; pull the content back in.
        Frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var active = _slips.Count(s => s.Transfer.State == TransferState.Running);
        if (active > 0 && MessageBox.Show(this, "Masih ada paket yang sedang dikirim atau diterima. Tutup dan batalkan?",
                "kirimdong", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CommitName();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _timer.Stop();
        foreach (var slip in _slips) slip.Transfer.Cancel();
        while (_offers.TryDequeue(out var p)) p.Respond(false);
        _currentOffer?.Respond(false);
        _edges.Dispose();
        foreach (var toast in Application.Current.Windows.OfType<ToastWindow>().ToList()) toast.Close();
        _discovery.Dispose();
        _server.Dispose();
        base.OnClosed(e);
    }

    private static string? ExistingOrNull(string path) => Directory.Exists(path) ? path : null;
}
