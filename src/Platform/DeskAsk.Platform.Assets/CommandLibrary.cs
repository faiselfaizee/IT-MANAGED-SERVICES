using System.Reflection;
using System.Text.Json;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>The built-in troubleshooting commands (embedded library.json). A new version adds commands that are missing; a command an administrator edited or switched off is left alone.</summary>
public static class CommandLibrary
{
    public static async Task SeedAsync(string connectionString, CancellationToken ct)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("library.json");
        if (s == null) return;
        var lib = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        await using var db = new NpgsqlConnection(connectionString); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        foreach (var c in lib.RootElement.EnumerateArray())
        {
            string G(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            int I(string n, int d) => c.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : d;
            var ps = c.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Array ? p.GetRawText() : "[]";
            await using var cmd = new NpgsqlCommand(@"insert into assets.commands (title, description, category, os, shell, body, risk, params, timeout_s, builtin, enabled, pos)
                values (@t,@d,@c,@o,@s,@b,@r,@p,@to,true,true,@pos) on conflict do nothing", db, tx);
            cmd.Parameters.AddWithValue("t", G("title")); cmd.Parameters.AddWithValue("d", G("description")); cmd.Parameters.AddWithValue("c", G("category") == "" ? "General" : G("category"));
            cmd.Parameters.AddWithValue("o", G("os")); cmd.Parameters.AddWithValue("s", G("shell")); cmd.Parameters.AddWithValue("b", G("body")); cmd.Parameters.AddWithValue("r", G("risk") == "" ? "safe" : G("risk"));
            cmd.Parameters.AddWithValue("p", ps); cmd.Parameters.AddWithValue("to", Math.Clamp(I("timeout", 120), 10, 7200)); cmd.Parameters.AddWithValue("pos", I("pos", 0));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
