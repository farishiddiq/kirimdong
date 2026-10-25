using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Kirimdong.Core;

namespace Kirimdong.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Units
{
    private static readonly CultureInfo Id = CultureInfo.GetCultureInfo("id-ID");

    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : v.ToString(v >= 100 ? "0" : "0.0", Id) + " " + units[u];
    }

    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{Math.Max(1, (int)Math.Ceiling(t.TotalSeconds))} dtk";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} mnt {t.Seconds} dtk";
        return $"{(int)t.TotalHours} jam {t.Minutes} mnt";
    }
}

public sealed class PeerItem : Observable
{
    private string _name;
    private string _address;
    private bool _isDropTarget;

    public PeerItem(Peer peer)
    {
        Id = peer.Id;
        _name = peer.Name;
        _address = peer.Address.ToString();
        EndPoint = peer.EndPoint;
    }

    public string Id { get; }
    public IPEndPoint EndPoint { get; private set; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Address { get => _address; private set => Set(ref _address, value); }
    public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value); }

    public void Update(Peer peer)
    {
        Name = peer.Name;
        Address = peer.Address.ToString();
        EndPoint = peer.EndPoint;
    }
}

public sealed class TransferItem : Observable
{
    private long _lastBytes;
    private DateTime _lastSample = DateTime.UtcNow;
    private double _speed;

    public TransferItem(Transfer transfer)
    {
        Transfer = transfer;
    }

    public Transfer Transfer { get; }
    public bool IsSend => Transfer.Direction == TransferDirection.Send;
    public string Direction => IsSend ? "Ke" : "Dari";
    public string PeerName => Transfer.PeerName;
    public string Summary => $"{Transfer.Summary} · {Units.Bytes(Transfer.TotalBytes)}";
    public double Progress => Transfer.TotalBytes == 0
        ? (Transfer.State == TransferState.Done ? 1 : 0)
        : Math.Clamp((double)Transfer.TransferredBytes / Transfer.TotalBytes, 0, 1);
    public bool IsActive => Transfer.IsActive;
    public bool IsDone => Transfer.State == TransferState.Done;
    public bool IsProblem => Transfer.State is TransferState.Failed or TransferState.Declined or TransferState.Canceled;
    public bool CanOpen => !IsSend && IsDone;
    public bool CanDismiss => !IsActive;

    public string Status => Transfer.State switch
    {
        TransferState.Waiting => IsSend ? "Menunggu penerima menekan Terima…" : "Menunggu jawabanmu…",
        TransferState.Running => RunningStatus(),
        TransferState.Done => IsSend ? "Terkirim" : $"Tersimpan di {Transfer.SaveFolder}",
        TransferState.Declined => IsSend ? "Ditolak penerima" : "Kamu tolak",
        TransferState.Canceled => "Dibatalkan",
        TransferState.Failed => "Gagal: " + Transfer.Error,
        _ => "",
    };

    private string RunningStatus()
    {
        var done = Transfer.TransferredBytes;
        var text = $"{Units.Bytes(done)} dari {Units.Bytes(Transfer.TotalBytes)}";
        if (_speed > 1)
        {
            text += $" · {Units.Bytes((long)_speed)}/dtk";
            var left = Transfer.TotalBytes - done;
            if (left > 0) text += $" · sisa {Units.Duration(TimeSpan.FromSeconds(left / _speed))}";
        }
        return text;
    }

    /// <summary>Called by the UI timer; smooths speed over recent samples.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        var bytes = Transfer.TransferredBytes;
        var seconds = (now - _lastSample).TotalSeconds;
        if (seconds >= 0.4)
        {
            var instant = (bytes - _lastBytes) / seconds;
            _speed = _speed <= 0 ? instant : _speed * 0.6 + instant * 0.4;
            _lastBytes = bytes;
            _lastSample = now;
        }
        RefreshAll();
    }

    public void RefreshAll()
    {
        Raise(nameof(Progress));
        Raise(nameof(Status));
        Raise(nameof(IsActive));
        Raise(nameof(IsDone));
        Raise(nameof(IsProblem));
        Raise(nameof(CanOpen));
        Raise(nameof(CanDismiss));
    }
}

/// <summary>An offer waiting for Terima / Tolak.</summary>
public sealed class OfferPrompt : Observable
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTime _deadline = DateTime.UtcNow + TransferServer.DecisionTimeout;

    public OfferPrompt(IncomingOffer offer, string saveFolder, CancellationToken ct)
    {
        Offer = offer;
        SaveFolder = saveFolder;
        ct.Register(() => _answer.TrySetResult(false));
        var names = offer.Offer.Files.Select(f => f.Path).ToList();
        Preview = names.Take(5).ToList();
        More = names.Count > 5 ? $"dan {names.Count - 5} file lainnya" : "";
    }

    public IncomingOffer Offer { get; }
    public string SenderName => Offer.Transfer.PeerName;
    public string Summary => Offer.Transfer.Summary;
    public string Size => $"{Offer.Offer.Files.Count} file · {Units.Bytes(Offer.Offer.TotalBytes)}";
    public IReadOnlyList<string> Preview { get; }
    public string More { get; }
    public string SaveFolder { get; }
    public string From => Offer.From.ToString();
    public Task<bool> Answer => _answer.Task;
    public bool IsOpen => !_answer.Task.IsCompleted;

    public string Countdown
    {
        get
        {
            var left = _deadline - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"Otomatis ditolak dalam {(int)left.TotalMinutes}:{left.Seconds:00}";
        }
    }

    public void Respond(bool accept) => _answer.TrySetResult(accept);
    public void Tick() => Raise(nameof(Countdown));
}
