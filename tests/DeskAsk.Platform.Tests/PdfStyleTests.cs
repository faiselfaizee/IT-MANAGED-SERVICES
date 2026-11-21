using System.Text;
using DeskAsk.Platform;
using Xunit;

/// <summary>Tables in every PDF: teal (#207178) header band with white letters, black letters and black borders.</summary>
public class PdfStyleTests
{
    [Fact]
    public void ATableHasATealHeaderWithWhiteLettersAndBlackBordersAndText()
    {
        var d = new PdfDoc();
        d.Heading("Report", 15, rule: true);
        d.Table(new[] { "Name", "Count" }, new[] { 3.0, 1 }, new[] { new[] { "Printers", "4" }, new[] { "Total", "4" } }, 9, boldLast: true);
        var text = Encoding.Latin1.GetString(d.ToBytes());
        Assert.Contains("0.125 0.443 0.471 rg", text);   // #207178: the header band
        Assert.Contains("1 1 1 rg", text);               // white header letters
        Assert.Contains("0 0 0 RG 0.6 w", text);         // black cell borders
        Assert.Contains("0 0 0 rg", text);               // black body letters
        Assert.DoesNotContain("0.4 0.45 0.52 rg", text); // the old grey letters
    }
}
