using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeskAsk.Platform.Licensing;

/// <summary>
/// A licence is a signed token, three base64url parts joined by dots: header, payload, signature (ES256 over "header.payload").
/// The vendor signs it with a private key it alone holds; every platform installation only holds the public key. The same token works
/// in SaaS (stored in the catalog) and on-prem (file or upload).
/// </summary>
public static class LicenseCodec
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static string B64u(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static byte[] FromB64u(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    public static string KeyId(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    public static string Issue(ECDsa privateKey, LicensePayload p)
    {
        var header = B64u(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { alg = "ES256", typ = "dk-license", kid = KeyId(privateKey) })));
        var body = B64u(JsonSerializer.SerializeToUtf8Bytes(p, Json));
        var sig = privateKey.SignData(Encoding.ASCII.GetBytes(header + "." + body), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + body + "." + B64u(sig);
    }

    /// <summary>Checks signature, customer and dates. Never throws: a bad token is an Invalid licence with a reason.</summary>
    public static License Verify(string? token, ECDsa publicKey, string tenantId, DateTimeOffset now)
    {
        try
        {
            var parts = (token ?? "").Trim().Split('.');
            if (parts.Length != 3) return Bad("Not a licence.");
            using (var h = JsonDocument.Parse(FromB64u(parts[0])))
            {
                var r = h.RootElement;
                if (r.GetProperty("alg").GetString() != "ES256" || r.GetProperty("typ").GetString() != "dk-license") return Bad("Unsupported licence format.");
            }
            if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64u(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return Bad("The signature is not valid.");
            var p = JsonSerializer.Deserialize<LicensePayload>(FromB64u(parts[1]), Json);
            if (p == null || p.V != 1 || p.Modules == null) return Bad("Unsupported licence content.");
            if (p.Tenant != tenantId && p.Tenant != "*") return new License(LicenseState.Invalid, p, "This licence was issued to another customer.");
            var t = now.ToUnixTimeSeconds();
            if (t < p.NotBefore) return new License(LicenseState.NotYetValid, p, "The licence is not valid yet.");
            if (t <= p.Expires) return new License(LicenseState.Valid, p, "");
            if (t <= p.Expires + p.GraceDays * 86400L) return new License(LicenseState.Grace, p, "The licence has expired; it still works during the grace period.");
            return new License(LicenseState.Expired, p, "The licence has expired.");
        }
        catch (Exception) { return Bad("The licence cannot be read."); }
        static License Bad(string why) => new(LicenseState.Invalid, null, why);
    }
}
