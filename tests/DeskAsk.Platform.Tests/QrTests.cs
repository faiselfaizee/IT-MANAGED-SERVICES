using DeskAsk.Platform;
using Xunit;

public class QrTests
{
    [Fact]
    public void AnAuthenticatorLinkBecomesAScannableImage()
    {
        var qr = Qr.Encode("otpauth://totp/DeskAsk:me@example.test?secret=JBSWY3DPEHPK3PXP&issuer=DeskAsk&digits=6&period=30", 'M');
        Assert.True(qr.Size >= 29);
        var svg = Qr.Svg(qr, 4, 55);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("width=\"55mm\"", svg);
    }
}
