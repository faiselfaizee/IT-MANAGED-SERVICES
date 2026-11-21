using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DeskAsk.Platform.Tenancy;
using DnsClient;
using DnsClient.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace DeskAsk.Platform.Identity;

/// <summary>Reads DNS from the machine's own resolver.</summary>
public sealed class SystemDnsLookup : IDnsLookup
{
    readonly LookupClient dns = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseCache = false });
    public async Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default)
    {
        try { var r = await dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct); return r.Answers.TxtRecords().SelectMany(t => t.Text).Select(x => x.Trim()).ToList(); }
        catch (DnsResponseException) { return Array.Empty<string>(); }
    }
    public async Task<IReadOnlyList<string>> CnameAsync(string name, CancellationToken ct = default)
    {
        try { var r = await dns.QueryAsync(name, QueryType.CNAME, cancellationToken: ct); return r.Answers.CnameRecords().Select(c => c.CanonicalName.Value.TrimEnd('.').ToLowerInvariant()).ToList(); }
        catch (DnsResponseException) { return Array.Empty<string>(); }
    }
}

/// <summary>
/// A customer's own web address. The administrator adds it, puts two DNS records in place (a TXT record proving the address is theirs and a CNAME to us),
/// and presses Check; from then on the address opens their installation and the proxy gets its certificate by itself.
/// </summary>
public static class DomainsApi
{
    public const int MaxPerCustomer = 5;
    public const string TxtPrefix = "_deskask-verify.";
    static readonly Regex Name = new(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.Compiled);

    /// <summary>Why the address cannot be used, or null.</summary>
    public static string? Problem(string host, IConfiguration cfg)
    {
        if (!Name.IsMatch(host) || host.Contains("..")) return "That is not a web address like desk.example.com.";
        if (IPAddress.TryParse(host, out _)) return "Use a name, not an IP address.";
        foreach (var reserved in (cfg["Platform:ReservedDomains"] ?? "deskask.in").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (host == reserved || host.EndsWith("." + reserved)) return "Addresses under " + reserved + " are ours. Use your own domain.";
        return null;
    }

    static object View(DomainRef d, string target) => new
    {
        host = d.Host, verified = d.Verified, primary = d.Primary,
        txtName = TxtPrefix + d.Host, txtValue = d.Token, cname = target,
    };

    public static void Map(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/domains").RequireAuthorization(p => p.RequireRole("admin"));

        async Task<string> Target(ITenantContext t, ITenantDomains d, IConfiguration cfg, CancellationToken ct) =>
            cfg["Platform:CustomDomainTarget"] is { Length: > 0 } c ? c : await d.BuiltInHostAsync(t.Require().Id, ct);

        g.MapGet("", async (ITenantContext t, ITenantDomains d, IConfiguration cfg, CancellationToken ct) =>
        {
            var target = await Target(t, d, cfg, ct);
            return Results.Ok(new { target, builtIn = await d.BuiltInHostAsync(t.Require().Id, ct), items = (await d.ListAsync(t.Require().Id, ct)).Select(x => View(x, target)) });
        });

        g.MapPost("", async (DomainBody b, ITenantContext t, ITenantDomains d, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var host = HostName.Normalize(b.Host ?? "");
            if (Problem(host, cfg) is { } why) return Results.BadRequest(new { error = "bad_domain", detail = why });
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            if (await d.AddAsync(t.Require().Id, host, token, MaxPerCustomer, ct) is { } no) return Results.Conflict(new { error = "domain_unavailable", detail = no });
            await audit.WriteAsync("domain.added", detail: "host=" + host, ct: ct);
            var target = await Target(t, d, cfg, ct);
            return Results.Ok(View((await d.ListAsync(t.Require().Id, ct)).First(x => x.Host == host), target));
        });

        g.MapPost("/{host}/verify", async (string host, ITenantContext t, ITenantDomains d, IDnsLookup dns, IConfiguration cfg, IServiceProvider sp, IAuditLog audit, CancellationToken ct) =>
        {
            host = HostName.Normalize(host);
            var mine = (await d.ListAsync(t.Require().Id, ct)).FirstOrDefault(x => x.Host == host);
            if (mine == null) return Results.NotFound();
            var target = await Target(t, d, cfg, ct);
            var txt = await dns.TxtAsync(TxtPrefix + host, ct);
            var cname = await dns.CnameAsync(host, ct);
            var owned = txt.Any(x => string.Equals(x, mine.Token, StringComparison.OrdinalIgnoreCase));
            var pointed = cname.Contains(HostName.Normalize(target));
            if (!owned)
                return Results.Ok(new { verified = false, txtFound = txt.Count > 0, cnameOk = pointed, detail = "We could not find the TXT record " + TxtPrefix + host + " with the value shown. DNS changes can take a few minutes; try again shortly." });
            await d.MarkVerifiedAsync(t.Require().Id, host, ct);
            await IdentitySchema.EnsureWebClientAsync(sp.GetRequiredService<IOpenIddictApplicationManager>(), host, ct);
            await audit.WriteAsync("domain.verified", detail: "host=" + host, ct: ct);
            return Results.Ok(new { verified = true, cnameOk = pointed, detail = pointed ? "The address is verified." : "Verified. Point " + host + " to " + target + " (CNAME) so people can reach it." });
        });

        g.MapPost("/{host}/primary", async (string host, PrimaryBody b, ITenantContext t, ITenantDomains d, IAuditLog audit, CancellationToken ct) =>
        {
            if (!await d.SetPrimaryAsync(t.Require().Id, host, b.Primary, ct)) return Results.BadRequest(new { error = "not_verified", detail = "Check the address first; only a verified address can be the main one." });
            await audit.WriteAsync("domain.primary", detail: "host=" + HostName.Normalize(host) + "; primary=" + b.Primary, ct: ct);
            return Results.Ok(new { ok = true });
        });

        g.MapDelete("/{host}", async (string host, ITenantContext t, ITenantDomains d, IAuditLog audit, CancellationToken ct) =>
        {
            if (!await d.RemoveAsync(t.Require().Id, host, ct)) return Results.NotFound();
            await audit.WriteAsync("domain.removed", detail: "host=" + HostName.Normalize(host), ct: ct);
            return Results.NoContent();
        });
    }

    public sealed record DomainBody(string? Host);
    public sealed record PrimaryBody(bool Primary);
}
