using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RacingHelper.Web;

/// <summary>
/// Opening the dashboard from a phone / tablet: which address to type, whether Windows Firewall lets it through, and a
/// one-click (UAC) fix. PowerShell is used instead of netsh because its output isn't translated on non-English Windows.
/// </summary>
public static class LanAccess
{
    public const string RuleName = "Racing Helper dashboard";

    public sealed record Address(string Ip, string Url, string Adapter, bool HasGateway, bool Likely);

    static readonly string[] VirtualWords =
        { "virtual", "vmware", "virtualbox", "hyper-v", "vethernet", "wsl", "vpn", "radmin", "hamachi", "zerotier", "tailscale", "wireguard", "npcap", "bluetooth", "loopback", "tap-", "tun" };

    /// <summary>IPv4 addresses of this PC, the real home-network one (has a router/gateway, 192.168.x.x) first.</summary>
    public static List<Address> Addresses(int port)
    {
        var list = new List<(Address a, int rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = ni.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                string name = (ni.Name + " " + ni.Description).ToLowerInvariant();
                bool isVirtual = VirtualWords.Any(name.Contains);
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue;          // no address from the router
                    bool priv = b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
                    int rank = (isVirtual ? 100 : 0) + (gateway ? 0 : 10) + (b[0] == 192 && b[1] == 168 ? 0 : priv ? 1 : 5);
                    var ip = ua.Address.ToString();
                    list.Add((new Address(ip, $"http://{ip}:{port}/", ni.Name, gateway, false), rank));
                }
            }
        }
        catch { }
        var sorted = list.OrderBy(x => x.rank).ToList();
        return sorted.Select((x, i) => x.a with { Likely = i == 0 && x.rank < 100 }).GroupBy(a => a.Ip).Select(g => g.First()).ToList();
    }

    /// <summary>Network category of the active connections (Private / Public / DomainAuthenticated), Windows only.</summary>
    public static List<string> NetworkCategories()
    {
        var json = PowerShell("Get-NetConnectionProfile | Select-Object Name, @{n='Category';e={[string]$_.NetworkCategory}} | ConvertTo-Json -Compress");
        var res = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return res;
            using var doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : new[] { doc.RootElement };
            foreach (var e in items) res.Add($"{e.GetProperty("Name").GetString()}: {e.GetProperty("Category").GetString()}");
        }
        catch { }
        return res;
    }

    /// <summary>Inbound firewall rules for this program and our port rule: (blocking rules, our allow rule present).</summary>
    public static (int blocks, bool allowRule) Firewall(string exe)
    {
        if (!OperatingSystem.IsWindows()) return (0, true);
        string q = exe.Replace("'", "''");
        var output = PowerShell(
            "$b = @(Get-NetFirewallApplicationFilter -Program '" + q + "' -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' -and $_.Enabled -eq 'True' }).Count; " +
            "$a = @(Get-NetFirewallRule -DisplayName '" + RuleName + "' -ErrorAction SilentlyContinue | Where-Object { $_.Enabled -eq 'True' }).Count; " +
            "\"$b $a\"");
        var parts = (output ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int blocks) && int.TryParse(parts[1], out int allow)) return (blocks, allow > 0);
        return (0, false);
    }

    /// <summary>
    /// Removes inbound "block" rules for this program (Windows adds one when its firewall popup is cancelled) and allows
    /// the dashboard port from your local network only, on any network profile. Asks for admin rights (UAC).
    /// </summary>
    public static (bool ok, string message) FixFirewall(string exe, int port)
    {
        if (!OperatingSystem.IsWindows()) return (false, "Only on Windows.");
        string q = exe.Replace("'", "''");
        string script =
            "$ErrorActionPreference = 'Stop'; " +
            "Get-NetFirewallApplicationFilter -Program '" + q + "' -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue | " +
            "Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Block' } | Remove-NetFirewallRule; " +
            "Remove-NetFirewallRule -DisplayName '" + RuleName + "' -ErrorAction SilentlyContinue; " +
            "New-NetFirewallRule -DisplayName '" + RuleName + "' -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + port + " -RemoteAddress LocalSubnet -Profile Any | Out-Null";
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Encode(script))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return (false, "Couldn't start PowerShell.");
            if (!p.WaitForExit(30000)) return (false, "Windows Firewall didn't answer in time.");
            return p.ExitCode == 0 ? (true, "Done: Racing Helper is allowed through Windows Firewall for devices on your home network.")
                                   : (false, "Windows Firewall refused the change (code " + p.ExitCode + ").");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (false, "Cancelled. Windows needs your OK (the admin prompt) to change the firewall.");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    // -EncodedCommand: base64 UTF-16, so paths and quotes in the script can't break the command line
    static string Encode(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    static string? PowerShell(string command)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Encode(command))
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return output;
        }
        catch { return null; }
    }
}
