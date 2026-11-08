using System.Security.Cryptography;
using DeskAsk.Platform;
using DeskAsk.Platform.Licensing;

// deskask-license keygen --out <folder>
// deskask-license issue --key <vendor-private.pem> --tenant <id|*> --plan <name> --modules servicedesk:agents=10,assets,vault --days 365 [--grace 14] [--out file]
// The private key never leaves the vendor. The public key goes into every installation as Licensing:VendorPublicKeyPem.
static string? Arg(string[] a, string name) { var i = Array.IndexOf(a, "--" + name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

var cmd = args.FirstOrDefault();
try
{
    if (cmd == "keygen")
    {
        var dir = Arg(args, "out") ?? ".";
        Directory.CreateDirectory(dir);
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(dir, "vendor-private.pem"), k.ExportPkcs8PrivateKeyPem());
        File.WriteAllText(Path.Combine(dir, "vendor-public.pem"), k.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine($"Written vendor-private.pem (keep it secret) and vendor-public.pem in {Path.GetFullPath(dir)}. Key id {LicenseCodec.KeyId(k)}.");
        return 0;
    }
    if (cmd == "issue")
    {
        var keyFile = Arg(args, "key") ?? throw new ArgumentException("--key is needed");
        var tenant = Arg(args, "tenant") ?? throw new ArgumentException("--tenant is needed");
        var plan = Arg(args, "plan") ?? "standard";
        var days = int.Parse(Arg(args, "days") ?? "365");
        var grace = int.Parse(Arg(args, "grace") ?? "14");
        var modules = new Dictionary<string, ModuleGrant>();
        foreach (var part in (Arg(args, "modules") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(':', 2);
            if (!Modules.All.Contains(bits[0])) throw new ArgumentException($"Unknown module '{bits[0]}'. Known: {string.Join(", ", Modules.All)}");
            var limits = new Dictionary<string, int>();
            if (bits.Length == 2)
                foreach (var l in bits[1].Split('+', StringSplitOptions.RemoveEmptyEntries)) { var kv = l.Split('=', 2); limits[kv[0]] = int.Parse(kv[1]); }
            modules[bits[0]] = new ModuleGrant(limits.Count == 0 ? null : limits);
        }
        if (modules.Count == 0) throw new ArgumentException("--modules is needed, for example servicedesk:agents=10,assets");
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(keyFile));
        var now = DateTimeOffset.UtcNow;
        var token = LicenseCodec.Issue(key, new LicensePayload(1, tenant, plan, now.ToUnixTimeSeconds(), now.ToUnixTimeSeconds(), now.AddDays(days).ToUnixTimeSeconds(), grace, modules));
        var o = Arg(args, "out");
        if (o != null) { File.WriteAllText(o, token); Console.WriteLine($"Licence for '{tenant}' written to {o} (valid {days} days, modules {string.Join(",", modules.Keys)})."); }
        else Console.WriteLine(token);
        return 0;
    }
    Console.WriteLine("Commands: keygen --out <folder> | issue --key <pem> --tenant <id> --plan <name> --modules <list> --days <n> [--grace <n>] [--out <file>]\nA module limit is written module:limit=value, several limits joined with +, for example servicedesk:agents=10+customers=50");
    return 2;
}
catch (Exception e) { Console.Error.WriteLine("Error: " + e.Message); return 1; }
