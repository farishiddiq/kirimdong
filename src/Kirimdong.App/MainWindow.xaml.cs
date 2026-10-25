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
    private OfferPrompt? _currentOffer;
    private bool _loaded;

    public MainWindow(AppSettings settings, int? port)
    {
        _settings = settings;
        InitializeComponent();

        NameBox.Text = settings.DeviceName;
        MyBarcode.Code = settings.DeviceId;
        SaveFolderText.Text = settings.SaveFolder;
        SaveFolderText.ToolTip = settings.SaveFolder;
        AutoAcceptBox.IsChecked = settings.AutoAccept;
        PeerList.ItemsSource = _peers;
        SlipList.ItemsSource = _slips;

        _server = new TransferServer(Identity, () => _settings.SaveFolder, DecideAsync);
        _server.TransferStarted += t => Dispatcher.BeginInvoke(() => AddSlip(t));
        try { _server.Start(port ?? Protocol.DefaultTcpPort); }
        catch (SocketException) { _server.Start(0); } // port taken: any free port works, discovery tells others which

        _discovery = new DiscoveryService(Identity);
        _discovery.PeersChanged += () => Dispatcher.BeginInvoke(SyncPeers);
        _discovery.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        ShowAddresses();
        SyncPeers();

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

    private void OnNetworkChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        ShowAddresses();
        _discovery.AnnounceNow();
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

    // ----- other PCs -----

    private void SyncPeers()
    {
        var current = _discovery.Peers;
        foreach (var gone in _peers.Where(p => current.All(c => c.Id != p.Id)).ToList()) _peers.Remove(gone);
        foreach (var peer in current)
        {
            var item = _peers.FirstOrDefault(p => p.Id == peer.Id);
            if (item is null) _peers.Add(new PeerItem(peer));
            else item.Update(peer);
        }
        EmptyState.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PeerCount.Text = _peers.Count switch
        {
            0 => _discovery.ListenError is null ? "Mencari…" : "Pencarian otomatis mati, pakai IP",
            1 => "1 PC ditemukan",
            var n => $"{n} PC ditemukan",
        };
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
            var hello = await TransferClient.PingAsync(address, port);
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

    private async void Send(PeerItem peer, IReadOnlyList<string> paths)
    {
        OutgoingBatch batch;
        try { batch = await Task.Run(() => FileCollector.Collect(paths)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "kirimdong", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (batch.IsEmpty)
        {
            MessageBox.Show(this, "Tidak ada file yang bisa dikirim dari pilihan itu.", "kirimdong", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var transfer = TransferClient.CreateTransfer(peer.Name, batch);
        AddSlip(transfer);
        var target = peer.EndPoint;
        var me = Identity();
        _ = Task.Run(() => TransferClient.SendAsync(target, me, batch, transfer));
    }

    // ----- slips -----

    private void AddSlip(Transfer transfer)
    {
        var item = new TransferItem(transfer);
        transfer.Changed += _ => Dispatcher.BeginInvoke(() =>
        {
            item.RefreshAll();
            if (transfer.State == TransferState.Done && transfer.Direction == TransferDirection.Receive && !IsActive)
                Flash();
        });
        _slips.Insert(0, item);
        UpdateSlipArea();
    }

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
        if (_settings.AutoAccept) return true;
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
            OfferBarcode.Code = next.Offer.Offer.SenderId;
            OfferSummary.Text = next.Summary;
            OfferSize.Text = $"{next.Size}  ·  dari {next.From}";
            OfferFiles.ItemsSource = next.Preview;
            OfferMore.Text = next.More;
            OfferMore.Visibility = next.More.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            OfferFolder.Text = $"Disimpan ke {next.SaveFolder}";
            OfferCountdown.Text = next.Countdown;
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

    private void Accept_Click(object sender, RoutedEventArgs e) => _currentOffer?.Respond(true);

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

    private void Flash()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            uCount = 0,
        };
        FlashWindowEx(ref info);
    }

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
        _discovery.Dispose();
        _server.Dispose();
        base.OnClosed(e);
    }

    private static string? ExistingOrNull(string path) => Directory.Exists(path) ? path : null;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);
}
