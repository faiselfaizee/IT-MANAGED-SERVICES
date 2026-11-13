using DeskAsk.Platform.Assets;
using Xunit;

public class WinRmErrorTests
{
    [Fact]
    public void AWrappedConsoleErrorBecomesOneSentenceWithTheTrustedHostsFix()
    {
        var err = "[192.168.31.226] Connecting to remote server 192.168.31.226 failed with the following error message : The WinRM client\n" +
                  "cannot process the request. If the authentication scheme is different from Kerberos, or if the client computer is not\n" +
                  "joined to a domain, then HTTPS transport must be used or the destination machine must be added to the TrustedHosts\n" +
                  "configuration setting. For more information, see the about_Remote_Troubleshooting Help topic.\n" +
                  "    + CategoryInfo          : OpenError: (192.168.31.226:String) [], PSRemotingTransportException\n";
        var clean = DiscoveryApi.CleanError(err);
        Assert.StartsWith("The WinRM client cannot process the request.", clean);
        Assert.DoesNotContain("CategoryInfo", clean);
        Assert.DoesNotContain("about_Remote", clean);
        Assert.Contains("TrustedHosts -Value", DiscoveryApi.WinHint(clean, "the DeskAsk server"));
    }

    [Fact]
    public void ClixmlIsDecoded()
    {
        var err = "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">[h] Connecting to remote server h failed with the following error message : Access is denied. _x000D__x000A_</S></Objs>";
        var clean = DiscoveryApi.CleanError(err);
        Assert.Equal("Access is denied.", clean);
        Assert.Contains("Administrators group", DiscoveryApi.WinHint(clean, "x"));
    }
}
