using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// Microsoft and Google sign-in for one customer company (Devices setup › Customers › a company › Sign-in), next to the installation's own sign-in for its staff.
/// Each company keeps its own app details under its own settings key. The sign-in page asks for the email and sends the person to the company whose
/// email domains match. A company's sign-in lets in only that company's people, and people it creates are tied to it.
/// </summary>
public static class CompanySso
{
    /// <summary>The company of a company's admin page (/api/companies/{companyId}/sso/...); null on the installation's own pages.</summary>
    public static int? Of(HttpContext h) =>
        h.Request.RouteValues.TryGetValue("companyId", out var v) && int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var c) ? c : null;

    public static string KeyFor(string key, int? company) => company == null ? key : key + ".company." + company.Value.ToString(CultureInfo.InvariantCulture);

    public static bool RoleOk(int? company, string role) => company == null ? UsersEndpoints.Roles.Contains(role) && role != "admin" : role is "customer" or "cadmin";
    public static string RoleError(int? company) => company == null
        ? "New people can be created as engineer, viewer, customer administrator or customer."
        : "A company's people are employees (customer) or customer administrators (cadmin).";
    public static bool ImportRoleOk(int? company, string role) => company == null ? UsersEndpoints.Roles.Contains(role) : role is "customer" or "cadmin";

    public static async Task<bool> ExistsAsync(HttpContext h, int? company, CancellationToken ct) =>
        company is not int co || await h.RequestServices.GetRequiredService<ICompanyDirectory>().GetAsync(co, ct) != null;

    /// <summary>A company's sign-in lets in only people of that company. Null when the person may come in.</summary>
    public static async Task<string?> RefuseAsync(HttpContext h, int? company, UserAccount user, CancellationToken ct)
    {
        if (company is not int co) return null;
        if (user.Role is not ("customer" or "cadmin")) return "This account is not one of the company's users. Sign in with your email and password.";
        var ties = await h.RequestServices.GetRequiredService<IAccess>().UserCompaniesAsync(ct);
        return ties.TryGetValue(user.Id, out var c) && c == co ? null : "This account belongs to another company. Ask your IT team.";
    }

    /// <summary>Ties new people to the company whose sign-in made them (on the installation's own sign-in: the single-company rule).</summary>
    public static async Task TieAsync(IServiceProvider sp, int? company, IEnumerable<UserAccount> users, CancellationToken ct)
    {
        if (company is not int co) { await SingleCompanyLink.TryAsync(sp, users, ct); return; }
        var access = sp.GetRequiredService<IAccess>();
        foreach (var u in users) await access.SetCompanyAsync(u.Id, co, ct);
    }

    /// <summary>Only existing customer users, not tied to another company, are linked by a company's import.</summary>
    public static async Task<bool> MayLinkAsync(IServiceProvider sp, int? company, UserAccount user, CancellationToken ct)
    {
        if (company is not int co) return true;
        if (user.Role is not ("customer" or "cadmin")) return false;
        var ties = await sp.GetRequiredService<IAccess>().UserCompaniesAsync(ct);
        return !ties.TryGetValue(user.Id, out var c) || c == co;
    }

    static int? CompanyOfKey(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(key[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : null;

    static string[] DomainList(string domains) => domains.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('@').ToLowerInvariant()).Distinct().ToArray();

    /// <summary>Is any company's sign-in switched on? (The sign-in page then offers "Sign in with your company account".)</summary>
    public static async Task<bool> AnyAsync(HttpContext h)
    {
        var st = h.RequestServices.GetRequiredService<ISettings>(); var dp = h.RequestServices.GetRequiredService<IDataProtectionProvider>(); var ct = h.RequestAborted;
        foreach (var key in (await st.ListAsync(EntraConfig.Key + ".company.", ct)).Keys)
            if (CompanyOfKey(key, EntraConfig.Key + ".company.") is int c && (await EntraConfig.LoadAsync(st, dp, ct, c)).Ready) return true;
        foreach (var key in (await st.ListAsync(GoogleConfig.Key + ".company.", ct)).Keys)
            if (CompanyOfKey(key, GoogleConfig.Key + ".company.") is int c && (await GoogleConfig.LoadAsync(st, dp, ct, c)).Ready) return true;
        return false;
    }

    /// <summary>The company sign-in for this email's domain: Microsoft first, then Google.</summary>
    public static async Task<(string Provider, int Company)?> FindAsync(HttpContext h, string email)
    {
        var at = email.LastIndexOf('@'); if (at < 1 || at == email.Length - 1) return null;
        var domain = email[(at + 1)..].ToLowerInvariant();
        var st = h.RequestServices.GetRequiredService<ISettings>(); var dp = h.RequestServices.GetRequiredService<IDataProtectionProvider>(); var ct = h.RequestAborted;
        foreach (var key in (await st.ListAsync(EntraConfig.Key + ".company.", ct)).Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (CompanyOfKey(key, EntraConfig.Key + ".company.") is int c && await EntraConfig.LoadAsync(st, dp, ct, c) is { Ready: true } e && DomainList(e.Domains).Contains(domain)) return ("microsoft", c);
        foreach (var key in (await st.ListAsync(GoogleConfig.Key + ".company.", ct)).Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (CompanyOfKey(key, GoogleConfig.Key + ".company.") is int c && await GoogleConfig.LoadAsync(st, dp, ct, c) is { Ready: true } g && g.DomainList.Contains(domain)) return ("google", c);
        return null;
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        // "Sign in with your company account": the email decides which company's Microsoft or Google sign-in is used
        r.MapGet("/account/sso/company", async (HttpContext http, string? email, string? returnUrl) =>
        {
            var ret = IdentityModule.LocalOrRoot(returnUrl); var e = (email ?? "").Trim().ToLowerInvariant();
            string Form(string error) => "<form method=\"get\" action=\"/account/sso/company\"><div class=\"m\"><i></i>DeskAsk</div><h2>Sign in with your company account</h2>"
                + (error == "" ? "" : "<p class=\"e\" role=\"alert\">" + WebUtility.HtmlEncode(error) + "</p>")
                + "<input type=\"hidden\" name=\"returnUrl\" value=\"" + WebUtility.HtmlEncode(ret) + "\">"
                + "<input name=\"email\" type=\"email\" placeholder=\"Your work email\" autocomplete=\"username\" required value=\"" + WebUtility.HtmlEncode(e) + "\">"
                + "<button type=\"submit\">Continue</button><a href=\"/account/login?ReturnUrl=" + Uri.EscapeDataString(ret) + "\">Back to sign in</a></form>";
            if (e == "") return Results.Content(IdentityModule.Html(Form("")), "text/html; charset=utf-8", Encoding.UTF8);
            var found = await FindAsync(http, e);
            if (found is not { } f) return Results.Content(IdentityModule.Html(Form("No company sign-in is set up for this email. Sign in with your email and password.")), "text/html; charset=utf-8", Encoding.UTF8, StatusCodes.Status404NotFound);
            return Results.Redirect($"/account/sso/{f.Provider}?company={f.Company.ToString(CultureInfo.InvariantCulture)}&hint={Uri.EscapeDataString(e)}&returnUrl={Uri.EscapeDataString(ret)}");
        }).AllowAnonymous();
    }
}
