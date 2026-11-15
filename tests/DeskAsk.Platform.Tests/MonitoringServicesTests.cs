using Xunit;

/// <summary>Automatic services of servers, ignoring and choosing services (no database).</summary>
public class MonitoringServicesTests
{
    static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    static DeviceState Dev(string kind, ServiceInfo[] inv, Dictionary<string, string>? modes = null, WatchedItem[]? watched = null, int invAgeMin = 2) =>
        new(1, "AST-1", "SQL01", 1, "Acme", kind, "10.0.0.5", "", true, "3.4.0", Now.AddSeconds(-30), true, null, 1, Now, Now.AddMinutes(-1), 5, 40, 0, 0, Array.Empty<DiskUse>(), Array.Empty<CheckState>(), Array.Empty<PartFault>(),
            null, null, watched, watched == null ? null : Now.AddMinutes(-1), inv, Now.AddMinutes(-invAgeMin), modes);
    static Rule AutoRule() => new(1, "Automatic service stopped", true, "autoservice", 0, 0, "warning", Array.Empty<string>(), null, true, false, "high", true);
    static List<Condition> Run(DeviceState d) => Evaluator.Conditions(new[] { d }, new[] { AutoRule() }, _ => new Dictionary<long, Sustained>(), new HashSet<long>(), Now);

    static readonly ServiceInfo[] Inv =
    {
        new("MSSQLSERVER", "SQL Server", "stopped", "auto"), new("Spooler", "Print Spooler", "running", "auto"), new("W32Time", "Windows Time", "stopped", "manual"),
        new("gupdate", "Google Update", "stopped", "auto"), new("CDPUserSvc_3a7b2", "per user", "stopped", "auto"), new("OldAgent", "Old agent", "stopped", "disabled"),
        new("nginx", "nginx", "failed", "auto"),
    };

    [Fact]
    public void ServersWatchTheirAutomaticServicesExceptTheOnesThatStopByThemselves()
    {
        var c = Run(Dev("server", Inv));
        Assert.Equal(new[] { "service:mssqlserver", "service:nginx" }, c.Select(x => x.Subject).OrderBy(x => x));
        Assert.Equal("Service SQL Server (MSSQLSERVER) is stopped.", c.First(x => x.Subject == "service:mssqlserver").Text);
        Assert.Empty(Run(Dev("computer", Inv)));     // a computer is switched off and updated all the time
        Assert.Empty(Run(Dev("server", Inv, invAgeMin: 40))); // an old list proves nothing
    }

    [Fact]
    public void AServiceCanBeIgnoredOrChosen()
    {
        var modes = new Dictionary<string, string> { ["mssqlserver"] = "ignore", ["w32time"] = "watch", ["gupdate"] = "watch" };
        var c = Run(Dev("server", Inv, modes));
        Assert.Equal(new[] { "service:gupdate", "service:nginx", "service:w32time" }, c.Select(x => x.Subject).OrderBy(x => x));
        // a computer only watches what was chosen
        Assert.Equal(new[] { "service:gupdate", "service:w32time" }, Run(Dev("computer", Inv, modes)).Select(x => x.Subject).OrderBy(x => x));
    }

    [Fact]
    public void AChosenServiceThatIsNotOnTheListIsReportedEveryMinute()
    {
        var modes = new Dictionary<string, string> { ["myapp"] = "watch" };
        var d = Dev("server", Array.Empty<ServiceInfo>(), modes, new[] { new WatchedItem("MyApp", "service", false, "stopped", 0) });
        Assert.Equal("Service MyApp is stopped.", Run(d).Single().Text);
        var missing = Dev("server", Array.Empty<ServiceInfo>(), modes, new[] { new WatchedItem("MyApp", "service", false, "missing", 0) });
        Assert.Equal("Service MyApp is not installed on this device.", Run(missing).Single().Text);
        Assert.Empty(Run(Dev("server", Array.Empty<ServiceInfo>(), modes, new[] { new WatchedItem("MyApp", "service", true, "running", 1) })));
    }
}
