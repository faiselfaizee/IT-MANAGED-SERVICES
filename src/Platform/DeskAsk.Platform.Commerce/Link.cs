using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// Release 6.4: the link between the company website (www.deskask.in) and this platform (docs/website-link.md).
/// <list type="bullet">
/// <item>The website calls /api/link/v1/... server to server: the same price list, quotes, checkout, order status, invoices and trials as the
/// platform's own store, signed with the shared secret. The browser never talks to the platform; Razorpay keys stay here.</item>
/// <item>The platform tells the website how website orders went (order.fulfilled, order.failed), signed the same way, with retries.</item>
/// <item>Customers renew from Administration › Subscription with a one-time renewal link that opens the website checkout filled in.</item>
/// </list>
/// A signed request carries X-DeskAsk-Timestamp (Unix seconds), X-DeskAsk-Nonce (once only) and X-DeskAsk-Signature =
/// "v1=" + hex(HMAC-SHA256(secret, timestamp + "\n" + nonce + "\n" + METHOD + "\n" + path and query + "\n" + body)). It is refused when it is more
/// than 5 minutes old or new, or when its nonce was seen before.
/// </summary>
public static class Link
{
    public const string HeaderTs = "X-DeskAsk-Timestamp", HeaderNonce = "X-DeskAsk-Nonce", HeaderSig = "X-DeskAsk-Signature", HeaderClientIp = "X-DeskAsk-Client-IP", HeaderEvent = "X-DeskAsk-Event";
    internal const string ItemKey = "deskask.link";
    const string Purpose = "commerce.link", RenewPurpose = "commerce.renew";
    static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public sealed record Settings(string WebsiteUrl = "", string SecretSealed = "", string SecretHint = "", DateTime? SecretAt = null, bool HidePlatformStore = false)
    {
        public bool Ready => WebsiteUrl != "" && SecretSealed != "";
    }

    public static async Task<Settings> LoadAsync(CommerceOptions o, CancellationToken ct)
    {
        var raw = await CommerceDb.GetSettingAsync(o, "link", ct);
        if (raw == null) return new Settings();
        try { return JsonSerializer.Deserialize<Settings>(raw, Licences.Json) ?? new Settings(); } catch (JsonException) { return new Settings(); }
    }

    static Task SaveAsync(CommerceOptions o, Settings s, CancellationToken ct) => CommerceDb.SetSettingAsync(o, "link", JsonSerializer.Serialize(s, Licences.Json), ct);

    public static string? Secret(IDataProtectionProvider dp, Settings s)
    {
        if (s.SecretSealed == "") return null;
        try { return dp.CreateProtector(Purpose).Unprotect(s.SecretSealed); } catch (CryptographicException) { return null; }
    }

    /// <summary>The signature of a request or event. Both sides compute exactly this.</summary>
    public static string Sign(string secret, string timestamp, string nonce, string method, string pathAndQuery, string body)
    {
        var data = Encoding.UTF8.GetBytes(timestamp + "\n" + nonce + "\n" + method.ToUpperInvariant() + "\n" + pathAndQuery + "\n" + body);
        return "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data)).ToLowerInvariant();
    }

    static bool Same(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    /// <summary>Lets a request through only when the website signed it with the shared secret, recently, and only once.</summary>
    static RouteHandlerBuilder Signed(RouteHandlerBuilder b) => b.AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
    {
        var http = ctx.HttpContext; var sp = http.RequestServices;
        var o = sp.GetRequiredService<CommerceOptions>();
        if (!o.For(sp.GetRequiredService<ITenantContext>())) return Results.NotFound(new { error = "not_found" });
        var s = await LoadAsync(o, http.RequestAborted);
        var secret = Secret(sp.GetRequiredService<IDataProtectionProvider>(), s);
        IResult Refuse(string why) => Results.Json(new { error = "link_auth", detail = why }, statusCode: 401);
        if (secret == null) return Refuse("The website link is not set up on the platform (Vendor console › Website link).");
        var ts = http.Request.Headers[HeaderTs].ToString(); var nonce = http.Request.Headers[HeaderNonce].ToString(); var sig = http.Request.Headers[HeaderSig].ToString();
        if (!long.TryParse(ts, out var t) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - t) > Window.TotalSeconds) return Refuse("The request is too old or its clock is wrong.");
        if (nonce.Length is < 16 or > 64) return Refuse("The request has no valid nonce.");
        // the body was buffered by LinkBodyBuffer before binding read it; read it again from the start
        if (!http.Request.Body.CanSeek) return Refuse("The request body could not be checked.");
        http.Request.Body.Position = 0;
        string body;
        using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8, leaveOpen: true)) body = await reader.ReadToEndAsync(http.RequestAborted);
        http.Request.Body.Position = 0;
        if (!Same(sig, Sign(secret, ts, nonce, http.Request.Method, http.Request.Path + http.Request.QueryString, body))) return Refuse("The signature does not match: check that both sides have the same secret.");
        await using (var db = await CommerceDb.OpenAsync(o, http.RequestAborted))
        {
            await using (var old = new NpgsqlCommand("delete from commerce.link_nonces where at < now() - interval '15 minutes'", db)) await old.ExecuteNonQueryAsync(http.RequestAborted);
            await using var c = new NpgsqlCommand("insert into commerce.link_nonces (nonce) values (@n) on conflict do nothing returning 1", db);
            c.Parameters.AddWithValue("n", nonce);
            if (await c.ExecuteScalarAsync(http.RequestAborted) == null) return Refuse("This request was already received.");
        }
        http.Items[ItemKey] = true;
        return await next(ctx);
    });

    /// <summary>The visitor's address: the website passes it on signed requests; otherwise the caller's own address.</summary>
    public static string ClientIp(HttpContext h)
    {
        if (h.Items.ContainsKey(ItemKey) && IPAddress.TryParse(h.Request.Headers[HeaderClientIp].ToString(), out var ip)) return ip.ToString();
        return h.Connection.RemoteIpAddress?.ToString() ?? "";
    }

    public static bool FromWebsite(HttpContext h) => h.Items.ContainsKey(ItemKey);

    /// <summary>A renewal link for a customer, valid 3 days. The website passes it back with the checkout, so the customer's own address may be bought again.</summary>
    public static string RenewToken(IDataProtectionProvider dp, string tenantId) =>
        dp.CreateProtector(RenewPurpose).ToTimeLimitedDataProtector().Protect(tenantId, TimeSpan.FromDays(3));

    public static string? RenewTenant(IDataProtectionProvider dp, string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try { return dp.CreateProtector(RenewPurpose).ToTimeLimitedDataProtector().Unprotect(token); } catch (CryptographicException) { return null; }
    }

    static RouteHandlerBuilder Console(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    public static void Map(IEndpointRouteBuilder r)
    {
        // the store's routes, for the website (the same code as the platform's own store page)
        StoreFront.MapRoutes(r, "/api/link/v1", Signed, link: true);

        Signed(r.MapGet("/api/link/v1/ping", () => Results.Ok(new { ok = true, platform = "DeskAsk" })));

        // the website's admin lists the products and switches one on or off: the platform keeps the one list, so both sides always show the same
        Signed(r.MapGet("/api/link/v1/products", async (CommerceOptions o, CancellationToken ct) => Results.Ok(await Products.ListViewAsync(o, ct))));
        Signed(r.MapPost("/api/link/v1/products/{key}", async (string key, JsonElement b, IServiceProvider sp, CancellationToken ct) =>
        {
            bool? sale = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("onSale", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            return Products.Result(await Products.SetAsync(sp, key, sale, b.ValueKind == JsonValueKind.Object && b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                b.ValueKind == JsonValueKind.Object && b.TryGetProperty("tagline", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null, "the website", ct), new { saved = true });
        }));

        // what a renewal link stands for: the customer, their contact details and their current plan, so the website can fill in the checkout
        Signed(r.MapGet("/api/link/v1/renew", async (string? token, CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var tenant = RenewTenant(dp, token);
            if (tenant == null) return Results.Json(new { error = "renew_expired", detail = "This renewal link has expired. Open it again from Administration › Subscription." }, statusCode: 410);
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var c = new NpgsqlCommand(@"select s.kind, s.period, s.lines::text, s.ends_at, s.state, a.name, a.email, a.country, a.gstin, a.address, a.phone, a.state_code
                from commerce.subscriptions s join commerce.accounts a on a.id = s.account_id where s.tenant_id = @t and s.kind = 'saas' order by s.created_at desc limit 1", db);
            c.Parameters.AddWithValue("t", tenant);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.Ok(new { tenantId = tenant, kind = "saas" });
            var lines = JsonDocument.Parse(rd.GetString(2)).RootElement.Clone();
            var (kind, period, ends, state) = (rd.GetString(0), rd.GetString(1), rd.GetDateTime(3), rd.GetString(4));
            var contact = new { company = rd.GetString(5), email = rd.GetString(6), country = rd.GetString(7), gstin = rd.GetString(8), address = rd.GetString(9), phone = rd.GetString(10), stateCode = rd.GetString(11) };
            await rd.CloseAsync();
            // the plan id of each module's current size, when it is still on the price list
            var plans = await Store.PlansAsync(db, ct); var pick = new Dictionary<string, int>();
            if (lines.ValueKind == JsonValueKind.Object)
                foreach (var m in lines.EnumerateObject())
                {
                    var match = plans.Where(p => p.Module == m.Name).FirstOrDefault(p => m.Value.ValueKind == JsonValueKind.Object && p.Limits.All(l => m.Value.TryGetProperty(l.Key, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == l.Value))
                                ?? plans.Where(p => p.Module == m.Name).OrderBy(p => p.Sort).FirstOrDefault();
                    if (match != null) pick[m.Name] = match.Id;
                }
            return Results.Ok(new { tenantId = tenant, kind, period = period is "month" ? "month" : "year", state, endsAt = ends, contact, plans = pick });
        }));

        // ---- Vendor console › Website link ----
        Console(r.MapGet("/api/commerce/link", async (CommerceOptions o, CancellationToken ct) =>
        {
            var s = await LoadAsync(o, ct);
            var recent = new List<object>();
            await using (var db = await CommerceDb.OpenAsync(o, ct))
            await using (var c = new NpgsqlCommand("select event_id, type, attempts, created_at, delivered_at, last_error, next_at from commerce.link_outbox order by id desc limit 30", db))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                    recent.Add(new { id = rd.GetString(0), type = rd.GetString(1), attempts = rd.GetInt32(2), created = rd.GetDateTime(3), delivered = rd.IsDBNull(4) ? (DateTime?)null : rd.GetDateTime(4), error = rd.GetString(5), next = rd.GetDateTime(6) });
            return Results.Ok(new { websiteUrl = s.WebsiteUrl, hasSecret = s.SecretSealed != "", secretHint = s.SecretHint, secretAt = s.SecretAt, hidePlatformStore = s.HidePlatformStore, hookPath = LinkOut.HookPath, recent });
        }));

        Console(r.MapPut("/api/commerce/link", async (JsonElement b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            var url = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("websiteUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()!.Trim().TrimEnd('/') : "";
            if (url != "" && (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != "https" || parsed.PathAndQuery != "/" || parsed.UserInfo != ""))
                return Results.BadRequest(new { error = "url", detail = "The website address is like https://www.deskask.in, without a path." });
            var hide = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("hidePlatformStore", out var h) && h.ValueKind == JsonValueKind.True;
            var s = await LoadAsync(o, ct);
            s = s with { WebsiteUrl = url, HidePlatformStore = hide && url != "" };
            await SaveAsync(o, s, ct);
            await audit.WriteAsync("commerce.link_changed", url, hide ? "platform store hidden" : "platform store shown", ct: ct);
            return Results.Ok(new { websiteUrl = s.WebsiteUrl, hidePlatformStore = s.HidePlatformStore });
        }));

        // a new shared secret: shown once, to paste into the website's admin (Store › Link)
        Console(r.MapPost("/api/commerce/link/secret", async (CommerceOptions o, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var secret = "dkl_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var s = await LoadAsync(o, ct);
            s = s with { SecretSealed = dp.CreateProtector(Purpose).Protect(secret), SecretHint = secret[^4..], SecretAt = DateTime.UtcNow };
            await SaveAsync(o, s, ct);
            await audit.WriteAsync("commerce.link_secret_made", "", "ends …" + s.SecretHint, ct: ct);
            return Results.Ok(new { secret, hint = s.SecretHint });
        }));

        // sends a signed ping event now and says what the website answered
        Console(r.MapPost("/api/commerce/link/test", async (HttpContext http, CancellationToken ct) =>
        {
            var (ok, detail) = await LinkOut.PingAsync(http.RequestServices, ct);
            return Results.Ok(new { ok, detail });
        }));
    }
}

/// <summary>Events for the website: written to commerce.link_outbox, sent at once and retried by the commerce.link job (1, 2, 4 … up to 60 minutes apart, for 3 days).</summary>
public static class LinkOut
{
    public const string HookPath = "/store/hooks";
    public static HttpMessageHandler? TestHandler { get; set; }
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(15) };
    static HttpClient Client() => TestHandler != null ? new HttpClient(TestHandler, false) { Timeout = TimeSpan.FromSeconds(15) } : Shared;

    public static async Task SendAsync(IServiceProvider sp, string type, object data, CancellationToken ct)
    {
        var o = sp.GetRequiredService<CommerceOptions>();
        var s = await Link.LoadAsync(o, ct);
        if (s.WebsiteUrl == "") return;
        var id = "ev_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var body = JsonSerializer.Serialize(new { id, type, created = DateTime.UtcNow, data }, Licences.Json);
        await using var db = await CommerceDb.OpenAsync(o, ct);
        await using (var c = new NpgsqlCommand("insert into commerce.link_outbox (event_id, type, body) values (@i, @t, @b)", db))
        { c.Parameters.AddWithValue("i", id); c.Parameters.AddWithValue("t", type); c.Parameters.AddWithValue("b", body); await c.ExecuteNonQueryAsync(ct); }
        await DeliverDueAsync(sp, db, s, ct);
    }

    static async Task<(bool Ok, string Detail)> PostAsync(IDataProtectionProvider dp, Link.Settings s, string type, string body, CancellationToken ct)
    {
        var secret = Link.Secret(dp, s);
        if (secret == null || s.WebsiteUrl == "") return (false, "The website address or the shared secret is not set.");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(); var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        using var req = new HttpRequestMessage(HttpMethod.Post, s.WebsiteUrl + HookPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add(Link.HeaderTs, ts); req.Headers.Add(Link.HeaderNonce, nonce); req.Headers.Add(Link.HeaderEvent, type);
        req.Headers.Add(Link.HeaderSig, Link.Sign(secret, ts, nonce, "POST", HookPath, body));
        try
        {
            using var res = await Client().SendAsync(req, ct);
            if (res.IsSuccessStatusCode) return (true, "The website answered " + (int)res.StatusCode + ".");
            var text = await res.Content.ReadAsStringAsync(ct);
            return (false, "The website answered " + (int)res.StatusCode + (text.Length > 0 ? ": " + (text.Length > 200 ? text[..200] : text) : "") + ".");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return (false, "The website could not be reached: " + e.Message); }
    }

    public static async Task<(bool, string)> PingAsync(IServiceProvider sp, CancellationToken ct)
    {
        var s = await Link.LoadAsync(sp.GetRequiredService<CommerceOptions>(), ct);
        var body = JsonSerializer.Serialize(new { id = "ping_" + Guid.NewGuid().ToString("N")[..12], type = "ping", created = DateTime.UtcNow, data = new { } }, Licences.Json);
        return await PostAsync(sp.GetRequiredService<IDataProtectionProvider>(), s, "ping", body, ct);
    }

    public static async Task DeliverDueAsync(IServiceProvider sp, NpgsqlConnection db, Link.Settings s, CancellationToken ct)
    {
        var due = new List<(long Id, string Type, string Body, int Attempts, DateTime Created)>();
        await using (var c = new NpgsqlCommand("select id, type, body, attempts, created_at from commerce.link_outbox where delivered_at is null and next_at <= now() and created_at > now() - interval '3 days' order by id limit 20", db))
        await using (var rd = await c.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) due.Add((rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetDateTime(4)));
        var dp = sp.GetRequiredService<IDataProtectionProvider>();
        foreach (var e in due)
        {
            var (ok, detail) = await PostAsync(dp, s, e.Type, e.Body, ct);
            var wait = TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, e.Attempts)));
            await using var u = new NpgsqlCommand(ok
                ? "update commerce.link_outbox set delivered_at = now(), attempts = attempts + 1, last_error = '' where id = @i"
                : "update commerce.link_outbox set attempts = attempts + 1, last_error = @e, next_at = now() + @w where id = @i", db);
            u.Parameters.AddWithValue("i", e.Id); u.Parameters.AddWithValue("e", detail); u.Parameters.AddWithValue("w", wait);
            await u.ExecuteNonQueryAsync(ct);
            if (!ok) sp.GetService<ILoggerFactory>()?.CreateLogger("DeskAsk.Store").LogWarning("Event {Type} for the website not delivered: {Detail}", e.Type, detail);
        }
    }
}

/// <summary>Retries events the website did not take yet.</summary>
public sealed class LinkJob : IJobHandler
{
    public const string JobType = "commerce.link";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var o = job.Services.GetRequiredService<CommerceOptions>();
        if (!o.ForId(job.Tenant.Id)) return;
        var s = await Link.LoadAsync(o, ct);
        if (!s.Ready) return;
        await using var db = await CommerceDb.OpenAsync(o, ct);
        await LinkOut.DeliverDueAsync(job.Services, db, s, ct);
    }
}

/// <summary>
/// Buffers the body of every /api/link/v1 request before the endpoint binds it, so the Signed filter (which runs after binding)
/// can read the exact bytes that were signed.
/// </summary>
public sealed class LinkBodyBuffer : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, go) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api/link/v1")) ctx.Request.EnableBuffering(256 * 1024);
            await go(ctx);
        });
        next(app);
    };
}
