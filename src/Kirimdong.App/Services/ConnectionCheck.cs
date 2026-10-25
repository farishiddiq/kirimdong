using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace Kirimdong.App.Services;

public enum FirewallState
{
    /// <summary>Inbound connections to kirimdong are allowed on every network type this PC is on now.</summary>
    Allowed,
    /// <summary>A rule blocks kirimdong (left behind when the Windows firewall question was cancelled).</summary>
    Blocked,
    /// <summary>No rule allows kirimdong on the current network type, so Windows drops what comes in.</summary>
    NotAllowed,
    Off,
    Unknown,
}

public sealed record NetworkInfo(string Name, string Category, string? Adapter, long? LinkMbps);

public sealed record ConnectionReport(IReadOnlyList<NetworkInfo> Networks, FirewallState Firewall, string CurrentProfiles)
{
    public bool OnPublicNetwork => Networks.Any(n => n.Category == "Public");
}

/// <summary>
/// Reads what decides whether other PCs can reach this one: the network type (Public/Private) and the Windows
/// firewall rules for this exe. Uses the Windows COM APIs, which work without administrator rights.
/// </summary>
public static class ConnectionCheck
{
    private const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;
    private const int ProtocolTcp = 6, ProtocolUdp = 17, ProtocolAny = 256;
    private const int DirectionIn = 1, ActionBlock = 0, ActionAllow = 1;
    public const string RuleName = "kirimdong";

    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    public static Task<ConnectionReport> RunAsync() => Task.Run(Run);

    public static ConnectionReport Run()
    {
        var networks = ReadNetworks();
        var firewall = ReadFirewall(out var profiles);
        return new ConnectionReport(networks, firewall, profiles);
    }

    private static IReadOnlyList<NetworkInfo> ReadNetworks()
    {
        var list = new List<NetworkInfo>();
        var wifi = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.GetIPProperties().GatewayAddresses.Count > 0)
            .ToList();
        try
        {
            // INetworkListManager: the same "Public / Private" the Settings app shows.
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
            foreach (dynamic network in (IEnumerable)manager.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */))
            {
                string name = network.GetName();
                int category = network.GetCategory();
                var nic = wifi.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) ?? wifi.FirstOrDefault();
                list.Add(new NetworkInfo(name, category switch { 0 => "Public", 1 => "Private", _ => "Domain" },
                    nic?.Name, nic is null ? null : nic.Speed / 1_000_000));
            }
        }
        catch (Exception)
        {
            foreach (var nic in wifi)
                list.Add(new NetworkInfo(nic.Name, "?", nic.Name, nic.Speed / 1_000_000));
        }
        return list;
    }

    private static FirewallState ReadFirewall(out string profileText)
    {
        profileText = "";
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            int current = policy.CurrentProfileTypes;
            profileText = string.Join(", ", Names(current));
            var exe = Path.GetFullPath(ExePath);

            var enabledProfiles = 0;
            var defaultAllows = 0;
            foreach (var p in new[] { ProfileDomain, ProfilePrivate, ProfilePublic }.Where(p => (current & p) != 0))
            {
                if ((bool)policy.FirewallEnabled[p]) enabledProfiles |= p;
                if ((int)policy.DefaultInboundAction[p] == ActionAllow) defaultAllows |= p;
            }
            if (enabledProfiles == 0) return FirewallState.Off;

            int tcpAllowed = 0, udpAllowed = 0, blocked = 0;
            foreach (dynamic rule in (IEnumerable)policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app is null || !(bool)rule.Enabled || (int)rule.Direction != DirectionIn) continue;
                if (!string.Equals(SafeFullPath(app), exe, StringComparison.OrdinalIgnoreCase)) continue;
                int profiles = rule.Profiles;
                int protocol = rule.Protocol;
                int action = rule.Action;
                if (action == ActionBlock) blocked |= profiles;
                else if (action == ActionAllow)
                {
                    if (protocol is ProtocolTcp or ProtocolAny) tcpAllowed |= profiles;
                    if (protocol is ProtocolUdp or ProtocolAny) udpAllowed |= profiles;
                }
            }
            if ((blocked & enabledProfiles) != 0) return FirewallState.Blocked;
            var allowed = (tcpAllowed & udpAllowed) | defaultAllows;
            return (allowed & enabledProfiles) == enabledProfiles ? FirewallState.Allowed : FirewallState.NotAllowed;
        }
        catch (Exception)
        {
            return FirewallState.Unknown;
        }
    }

    /// <summary>
    /// Runs in a copy of kirimdong started with administrator rights (--fix-firewall): switches off rules that
    /// block this exe and adds inbound TCP and UDP rules that allow it on every network type.
    /// </summary>
    public static int Fix()
    {
        try
        {
            var exe = Path.GetFullPath(ExePath);
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2")!)!;
            dynamic rules = policy.Rules;
            var haveTcp = false;
            var haveUdp = false;
            foreach (dynamic rule in (IEnumerable)rules)
            {
                string? app = rule.ApplicationName;
                if (app is null || (int)rule.Direction != DirectionIn) continue;
                if (!string.Equals(SafeFullPath(app), exe, StringComparison.OrdinalIgnoreCase)) continue;
                if ((int)rule.Action == ActionBlock && (bool)rule.Enabled) rule.Enabled = false; // reversible, unlike deleting
                if ((string)rule.Name == RuleName && (int)rule.Action == ActionAllow)
                {
                    rule.Enabled = true;
                    rule.Profiles = ProfileDomain | ProfilePrivate | ProfilePublic;
                    if ((int)rule.Protocol == ProtocolTcp) haveTcp = true;
                    if ((int)rule.Protocol == ProtocolUdp) haveUdp = true;
                }
            }
            if (!haveTcp) rules.Add(NewRule(exe, ProtocolTcp));
            if (!haveUdp) rules.Add(NewRule(exe, ProtocolUdp));
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>Starts the elevated fixer (Windows asks for permission). True when it reported success.</summary>
    public static async Task<bool> RequestFixAsync()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ExePath, "--fix-firewall") { UseShellExecute = true, Verb = "runas" });
            if (p is null) return false;
            await p.WaitForExitAsync().ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the permission prompt was declined
        }
    }

    private static object NewRule(string exe, int protocol)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule")!)!;
        rule.Name = RuleName;
        rule.Description = "Menerima paket dari PC lain di jaringan lokal (dibuat oleh kirimdong).";
        rule.Grouping = "kirimdong";
        rule.ApplicationName = exe;
        rule.Protocol = protocol;
        rule.Direction = DirectionIn;
        rule.Action = ActionAllow;
        rule.Profiles = ProfileDomain | ProfilePrivate | ProfilePublic;
        rule.Enabled = true;
        return rule;
    }

    private static IEnumerable<string> Names(int profiles)
    {
        if ((profiles & ProfileDomain) != 0) yield return "Domain";
        if ((profiles & ProfilePrivate) != 0) yield return "Private";
        if ((profiles & ProfilePublic) != 0) yield return "Public";
    }

    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch (Exception) { return path; }
    }
}
