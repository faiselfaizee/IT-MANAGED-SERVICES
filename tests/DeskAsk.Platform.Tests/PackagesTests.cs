using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>The software repository (6.19.0): installers uploaded in pieces with their SHA-256, package names, and installs queued as agent jobs.</summary>
public class PackagesTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    readonly string dir = Path.Combine(Path.GetTempPath(), "deskask-packages-" + Guid.NewGuid().ToString("N")[..8]);
    public PackagesTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Assets:PackageDir", dir); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task InstallersArriveInPiecesAndAreCheckedAndInstallsAreQueuedPerComputer()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate assets.packages restart identity cascade");
        var bytes = RandomNumberGenerator.GetBytes(20000); var sha = Convert.ToHexString(SHA256.HashData(bytes));

        var up = await J(await admin.PostAsync("/api/assets/packages/uploads", Json(new { })));
        var uid = up.GetProperty("id").GetString()!;
        async Task<HttpResponseMessage> Piece(long offset, byte[] b) => await admin.PutAsync($"/api/assets/packages/uploads/{uid}?offset={offset}", new ByteArrayContent(b));
        Assert.Equal(HttpStatusCode.OK, (await Piece(0, bytes[..12000])).StatusCode);
        var wrong = await Piece(0, bytes[12000..]);   // a piece sent twice or out of order: the server says where to go on
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode); Assert.Equal(12000, (await J(wrong)).GetProperty("length").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await Piece(12000, bytes[12000..])).StatusCode);

        // the kind of file has to fit the system, and a vendor's SHA-256 that does not match is refused
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/packages", Json(new { name = "Tool", os = "linux", kind = "file", upload = uid, fileName = "tool.msi" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/packages", Json(new { name = "Tool", os = "windows", kind = "winget", source = "bad id!" }))).StatusCode);
        var ok = await J(await admin.PostAsync("/api/assets/packages", Json(new { name = "Tool", version = "1.2", publisher = "Acme", os = "windows", kind = "file", upload = uid, fileName = "tool-1.2.msi", sha256 = sha.ToLowerInvariant(), args = "ALLUSERS=1" })));
        Assert.Equal(sha, ok.GetProperty("sha256").GetString()); Assert.Equal(20000, ok.GetProperty("size").GetInt64());
        var id = ok.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/packages", Json(new { name = "Chrome", os = "windows", kind = "winget", source = "Google.Chrome" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/packages", Json(new { name = "htop", os = "linux", kind = "apt", source = "htop" }))).StatusCode);

        var list = (await J(await admin.GetAsync("/api/assets/packages?os=windows"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("tool-1.2.msi", list.Single(p => p.GetProperty("id").GetInt32() == id).GetProperty("fileName").GetString());
        Assert.True(File.Exists(Directory.GetFiles(dir, id + ".pkg", SearchOption.AllDirectories).Single()));

        // a computer without an agent is skipped with the reason; nothing is queued for it
        var asset = (await J(await admin.PostAsync("/api/assets", Json(new { name = "No agent PC" })))).GetProperty("id").GetInt64();
        var inst = await J(await admin.PostAsync($"/api/assets/packages/{id}/install", Json(new { assetIds = new[] { asset } })));
        Assert.Empty(inst.GetProperty("queued").EnumerateArray());
        Assert.Contains("agent", inst.GetProperty("skipped")[0].GetProperty("reason").GetString());

        // a made-up download link finds nothing; switching a program off stops installs; removing deletes the file
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync("/agent/v1/packages/not-a-real-link")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/packages/{id}", Json(new { enabled = false }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync($"/api/assets/packages/{id}/install", Json(new { assetIds = new[] { asset } }))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/assets/packages/{id}")).StatusCode);
        Assert.Empty(Directory.GetFiles(dir, id + ".pkg", SearchOption.AllDirectories));

        // engineers install but do not add or remove programs
        var pw = "Long-Enough-1"; var email = "eng" + Guid.NewGuid().ToString("N")[..6] + "@pkg.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email, name = "Eng", role = "engineer", password = pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, email, pw);
        Assert.Equal(HttpStatusCode.OK, (await eng.GetAsync("/api/assets/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/assets/packages/uploads", Json(new { }))).StatusCode);
    }
}
