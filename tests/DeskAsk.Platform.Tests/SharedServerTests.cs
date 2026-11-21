using DeskAsk.Platform;
using DeskAsk.Platform.Tenancy;
using DeskAsk.Platform.Updates;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>On the shared SaaS server only the vendor's own customer may update the program; on a customer's own installation the administrator may.</summary>
public class SharedServerTests
{
    static IConfiguration Cfg(params (string, string)[] v) => new ConfigurationBuilder().AddInMemoryCollection(v.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).Build();
    static TenantContext As(string id) => new() { Current = new TenantInfo(id, id, id + ".test", "") };

    [Fact]
    public void AnInstallationWithoutACatalogLetsItsAdministratorUpdate() =>
        Assert.True(UpdatesModule.Allowed(Cfg(), As("acme")));

    [Fact]
    public void OnTheSharedServerOnlyTheVendorCustomerMayUpdate()
    {
        var cfg = Cfg(("Platform:CatalogConnection", "Host=x"));
        Assert.True(UpdatesModule.Allowed(cfg, As("platform")));
        Assert.False(UpdatesModule.Allowed(cfg, As("svjlasertech")));
        Assert.True(UpdatesModule.Allowed(Cfg(("Platform:CatalogConnection", "Host=x"), ("Commerce:VendorTenant", "damazetech")), As("damazetech")));
        Assert.False(UpdatesModule.Allowed(Cfg(("Platform:CatalogConnection", "Host=x"), ("Commerce:VendorTenant", "damazetech")), As("platform")));
    }
}
