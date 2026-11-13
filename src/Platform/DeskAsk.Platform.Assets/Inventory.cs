using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskAsk.Platform.Assets;

/// <summary>The inventory of one computer, normalised from the document an agent, a WinRM or an SSH collector sends. One function serves every source so the data looks the same.</summary>
public sealed class NormInv
{
    public string? OsFamily, OsName, OsVersion, Hostname, DnsName, Make, Model, Serial, CpuMake, CpuModel, CpuSocket, DomainWorkgroup, BiosVersion, LastPatch;
    public string DomainType = "workgroup", AntivirusStatus = "Not detected", Kind = "endpoint";
    public long? TotalCpu, CpuCores, CoresPerCpu, Logical, Ram, TotalStorage, UptimeS;
    public string Disks = "[]", Nics = "[]", Apps = "[]", Antivirus = "[]", Macs = "[]", Ips = "[]";
    public int NicTotal, NicConnected;
    /// <summary>Extra hardware tables (sensors, power supplies, GPUs, datastores, virtual machines, hosts) as JSON; null when the source sends none.</summary>
    public string? Extra;
}

public static class Inventory
{
    static readonly Regex MacRx = new(@"^([0-9A-F]{2}:){5}[0-9A-F]{2}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex BadSerialRx = new(@"^(to be filled.*|default string|system serial number|none|n/a|0+|not specified|123456789)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static JsonElement Prop(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
    static string? S(JsonElement e, string k)
    {
        var v = Prop(e, k);
        var s = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        s = s?.Trim(); return string.IsNullOrEmpty(s) ? null : s;
    }
    static long? N(JsonElement e, string k)
    {
        var v = Prop(e, k);
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d)) return (long)Math.Round(d, MidpointRounding.AwayFromZero);
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && !double.IsInfinity(x)) return (long)Math.Round(x, MidpointRounding.AwayFromZero);
        return null;
    }
    static bool B(JsonElement e, string k) { var v = Prop(e, k); return v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"); }
    static IEnumerable<JsonElement> Arr(JsonElement e, string k)
    {
        var v = Prop(e, k);
        if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray();
        if (v.ValueKind == JsonValueKind.Object) return new[] { v };
        return Array.Empty<JsonElement>();
    }
    static string Json(object o) => JsonSerializer.Serialize(o);

    static string? Vendor(string? v)
    {
        if (v == null) return null;
        if (Regex.IsMatch(v, "GenuineIntel|Intel", RegexOptions.IgnoreCase)) return "Intel";
        if (Regex.IsMatch(v, "AuthenticAMD|AMD", RegexOptions.IgnoreCase)) return "AMD";
        return v;
    }

    public static NormInv Normalize(JsonElement inv)
    {
        var n = new NormInv();
        if (inv.ValueKind != JsonValueKind.Object) return n;
        var cpu = Prop(inv, "cpu");

        var disks = Arr(inv, "disks").Select(d => new Dictionary<string, object?>
        {
            ["name"] = S(d, "name"), ["model"] = S(d, "model"), ["type"] = S(d, "type"), ["size_bytes"] = N(d, "size_bytes"),
            ["partitions"] = Arr(d, "partitions").Select(p => new Dictionary<string, object?>
            { ["name"] = S(p, "name"), ["size_bytes"] = N(p, "size_bytes"), ["fs"] = S(p, "fs"), ["mount"] = S(p, "mount"), ["label"] = S(p, "label"), ["free_bytes"] = N(p, "free_bytes"), ["type"] = S(p, "type") }).ToList()
        }).ToList();

        var nics = Arr(inv, "nics").Select(x => new
        {
            name = S(x, "name"), description = S(x, "description"), mac = S(x, "mac")?.ToUpperInvariant().Replace('-', ':'),
            connected = B(x, "connected"), speed_mbps = N(x, "speed_mbps"),
            ips = Arr(x, "ips").Select(i => i.ValueKind == JsonValueKind.String ? i.GetString()!.Trim() : "").Where(i => i != "").ToList()
        }).Where(x => x.name != null || x.mac != null).ToList();
        var connected = nics.Where(x => x.connected).ToList();

        var apps = Arr(inv, "installed_apps").Select(a => new { name = S(a, "name"), version = S(a, "version"), publisher = S(a, "publisher"), installed_on = S(a, "installed_on") })
            .Where(a => a.name != null).ToList();
        var seen = new HashSet<string>();
        var uniq = apps.Where(a => seen.Add(a.name + "|" + a.version)).OrderBy(a => a.name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var av = Arr(inv, "antivirus").Select(a =>
        {
            var up = Prop(a, "up_to_date");
            object? upv = up.ValueKind is JsonValueKind.True or JsonValueKind.False ? up.ValueKind == JsonValueKind.True : null;
            return new { name = S(a, "name"), enabled = B(a, "enabled"), up_to_date = upv, state = S(a, "state"), definitions = S(a, "definitions") };
        }).Where(a => a.name != null).ToList();
        string avs = av.Count == 0 ? "Not detected"
            : !av.Any(a => a.enabled) ? "Disabled"
            : av.Any(a => a.up_to_date is bool b && !b) ? "Enabled - out of date" : "Protected";

        var ips = connected.SelectMany(x => x.ips).Where(i => !Regex.IsMatch(i, @"^(127\.|169\.254\.|::1|fe80)", RegexOptions.IgnoreCase)).Distinct().ToList();
        var total = N(cpu, "count"); if (total == 0) total = null;
        var cores = N(cpu, "total_cores");
        var cpp = N(cpu, "cores_per_cpu"); if (cpp == 0) cpp = null;
        if (cpp == null && total != null && cores is > 0) cpp = (long)Math.Round((double)cores.Value / total.Value, MidpointRounding.AwayFromZero);
        var storage = N(inv, "total_storage_bytes") ?? disks.Sum(d => (long)(d["size_bytes"] ?? 0L));
        var patch = S(inv, "last_patch_date");
        if (patch != null && !Regex.IsMatch(patch, @"^\d{4}-\d{2}-\d{2}$"))
            patch = DateTime.TryParse(patch, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var pd) ? pd.ToString("yyyy-MM-dd") : null;
        var serial = S(inv, "serial_number"); if (serial != null && BadSerialRx.IsMatch(serial)) serial = null;
        var fam = S(inv, "os_family");

        n.OsFamily = fam is "linux" or "windows" ? fam : null;
        n.OsName = S(inv, "os_name"); n.OsVersion = S(inv, "os_version");
        n.Hostname = S(inv, "hostname"); n.DnsName = S(inv, "dns_name");
        n.Make = S(inv, "make"); n.Model = S(inv, "model"); n.Serial = serial;
        n.CpuMake = Vendor(S(cpu, "manufacturer")); n.CpuModel = S(cpu, "model"); n.CpuSocket = S(cpu, "socket");
        n.TotalCpu = total; n.CpuCores = cores; n.CoresPerCpu = cpp; n.Logical = N(cpu, "logical");
        n.Ram = N(inv, "ram_bytes"); n.TotalStorage = storage == 0 ? null : storage; n.UptimeS = N(inv, "uptime_s");
        n.Disks = Json(disks); n.Nics = Json(nics); n.Apps = Json(uniq); n.Antivirus = Json(av);
        n.Macs = Json(nics.Select(x => x.mac ?? "").Where(m => MacRx.IsMatch(m)).Distinct().ToList());
        n.Ips = Json(ips); n.NicTotal = nics.Count; n.NicConnected = connected.Count;
        n.DomainType = S(inv, "domain_type") == "domain" ? "domain" : "workgroup"; n.DomainWorkgroup = S(inv, "domain");
        n.AntivirusStatus = avs; n.BiosVersion = S(inv, "bios_version"); n.LastPatch = patch;
        n.Kind = B(inv, "is_server") ? "server" : "endpoint";
        var extra = Prop(inv, "hardware_extra");
        if (extra.ValueKind == JsonValueKind.Object) { var raw = extra.GetRawText(); if (raw.Length <= 4_000_000) n.Extra = raw; }
        return n;
    }
}
