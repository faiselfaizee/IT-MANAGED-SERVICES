using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace DeskAsk.Platform.Assets;

/// <summary>Seals small secrets (enrolment tokens shown again, remote-control passwords, discovery credentials) with AES-GCM under a key made from the server secret and the customer id, so a copy of one customer's database alone is not enough.</summary>
public static class SecretBox
{
    static byte[] Key(IConfiguration cfg, string tenantId)
    {
        var secret = cfg["Assets:SecretKey"] ?? cfg["Identity:EncryptionSecret"] ?? "development-only-secret";
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("assets-secrets:" + tenantId));
    }
    public static string Seal(IConfiguration cfg, string tenantId, string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var data = Encoding.UTF8.GetBytes(plain);
        var ct = new byte[data.Length]; var tag = new byte[16];
        using var g = new AesGcm(Key(cfg, tenantId), 16); g.Encrypt(nonce, data, ct, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }
    public static string Open(IConfiguration cfg, string tenantId, string sealedText)
    {
        var all = Convert.FromBase64String(sealedText);
        var pt = new byte[all.Length - 28];
        using var g = new AesGcm(Key(cfg, tenantId), 16); g.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), pt);
        return Encoding.UTF8.GetString(pt);
    }
}
