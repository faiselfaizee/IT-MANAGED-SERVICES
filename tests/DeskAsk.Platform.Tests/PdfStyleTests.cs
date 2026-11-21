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

    [Fact]
    public void ACustomDesignChangesTheColoursAndAddsTheFootLine()
    {
        var style = new PdfStyle(HeaderColor: "#FF0000", HeaderText: "#00FF00", Border: "#0000FF", Footer: "DamazeTech");
        var d = new PdfDoc(style);
        d.Table(new[] { "A" }, new[] { 1.0 }, new[] { new[] { "x" } }, 9);
        var text = Encoding.Latin1.GetString(d.ToBytes());
        Assert.Contains("1 0 0 rg", text);
        Assert.Contains("0 1 0 rg", text);
        Assert.Contains("0 0 1 RG", text);
        Assert.Contains("DamazeTech", text);
    }

    [Fact]
    public void BadSettingsFallBackToTheStandardDesign()
    {
        Assert.Equal(PdfStyle.Default, PdfStyle.Parse(null));
        Assert.Equal(PdfStyle.Default, PdfStyle.Parse("not json"));
        var c = new PdfStyle(HeaderColor: "teal", Border: "#12", Footer: new string('x', 500), Note: new string('n', 900)).Clean();
        Assert.Equal("#207178", c.HeaderColor);
        Assert.Equal("#000000", c.Border);
        Assert.Equal(120, c.Footer.Length);
        Assert.Equal(600, c.Note.Length);
        var rt = PdfStyle.Parse(new PdfStyle(HeaderColor: "#112233").ToJson());
        Assert.Equal("#112233", rt.HeaderColor);
        var (r, g, b) = PdfStyle.Rgb("#FF8000");
        Assert.Equal(1.0, r, 3); Assert.Equal(0.502, g, 3); Assert.Equal(0.0, b, 3);
    }
}
