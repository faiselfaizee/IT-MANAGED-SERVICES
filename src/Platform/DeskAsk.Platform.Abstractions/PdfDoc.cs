using System.Globalization;
using System.Text;

namespace DeskAsk.Platform;

/// <summary>
/// A very small PDF writer for the monthly report: A4 pages, Helvetica and Helvetica-Bold (built into every PDF reader, so nothing is embedded),
/// wrapped text and simple tables. Latin letters only: anything else is shown as a question mark. No outside library, no native files.
/// </summary>
public sealed class PdfDoc
{
    const double W = 595.28, H = 841.89, Margin = 48;
    readonly List<StringBuilder> pages = new(); StringBuilder cur = null!; double y;
    public PdfDoc() => NewPage();

    void NewPage() { cur = new StringBuilder(); pages.Add(cur); y = H - Margin; }
    void Room(double need) { if (y - need < Margin) NewPage(); }

    // Helvetica widths (per 1000 em) for ASCII 32..126; good enough to wrap and align text.
    static readonly int[] Wd = { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584 };
    public static double Width(string s, double size, bool bold) { double w = 0; foreach (var c in s) w += (c >= 32 && c <= 126 ? Wd[c - 32] * (bold && c != ' ' ? 1.06 : 1) : 556); return w * size / 1000; }

    /// <summary>Latin-1 letters keep their shape in the output; anything beyond becomes a question mark.</summary>
    static string Clean(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Replace("\r", "").Replace('\t', ' '))
            sb.Append(c switch { '\n' => ' ', '–' or '—' => '-', '‘' or '’' => '\'', '“' or '”' => '"', '…' => '.', '·' => '-', >= ' ' and <= '~' => c, >= ' ' and <= 'ÿ' => c, _ => '?' });
        return sb.ToString();
    }
    static string Esc(string s)
    {   // WinAnsi: Latin-1 range is the same code points; escape the PDF string delimiters
        var sb = new StringBuilder();
        foreach (var c in s) { if (c is '(' or ')' or '\\') sb.Append('\\'); sb.Append(c); }
        return sb.ToString();
    }

    void Put(string text, double x, double size, bool bold, (double r, double g, double b)? color = null)
    {
        var c = color ?? (0.1, 0.12, 0.16);
        cur.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {c.Item1:0.##} {c.Item2:0.##} {c.Item3:0.##} rg {x:0.##} {y:0.##} Td ({Esc(Clean(text))}) Tj ET\n");
    }

    public static IEnumerable<string> Wrap(string text, double max, double size, bool bold)
    {
        text = Clean(text);
        foreach (var para in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var w = word;
                while (Width(w, size, bold) > max && w.Length > 1) { var cut = w.Length; while (cut > 1 && Width(w[..cut], size, bold) > max) cut--; if (line.Length > 0) { yield return line.ToString(); line.Clear(); } yield return w[..cut]; w = w[cut..]; }
                if (line.Length > 0 && Width(line + " " + w, size, bold) > max) { yield return line.ToString(); line.Clear(); }
                if (line.Length > 0) line.Append(' '); line.Append(w);
            }
            yield return line.ToString();
        }
    }

    public void Heading(string text, double size = 15, bool rule = false)
    {
        Room(size * 2.4); y -= size * 0.5; y -= size; Put(text, Margin, size, true, (0.04, 0.35, 0.33)); y -= size * 0.35;
        if (rule) { cur.Append(CultureInfo.InvariantCulture, $"0.8 0.83 0.86 RG 0.6 w {Margin:0.##} {y:0.##} m {W - Margin:0.##} {y:0.##} l S\n"); y -= 6; }
    }
    public void Paragraph(string text, double size = 10, bool bold = false)
    {
        foreach (var l in Wrap(text, W - 2 * Margin, size, bold)) { Room(size * 1.5); y -= size * 1.25; Put(l, Margin, size, bold); y -= size * 0.25; }
    }
    public void Gap(double h) => y -= h;

    /// <summary>Label and value pairs in two columns, like a row of figures.</summary>
    public void Facts(IReadOnlyList<(string label, string value)> items)
    {
        var colW = (W - 2 * Margin) / 2;
        for (var i = 0; i < items.Count; i += 2)
        {
            Room(36); y -= 22;
            for (var k = 0; k < 2 && i + k < items.Count; k++)
            {
                var x = Margin + k * colW; var (label, value) = items[i + k];
                Put(value, x, 17, true, (0.1, 0.12, 0.16)); y -= 12; Put(label, x, 8.5, false, (0.4, 0.45, 0.52)); y += 12;
            }
            y -= 14;
        }
    }

    /// <summary>A table: widths are shares of the page width; long cells wrap onto more lines; the header repeats on a new page.</summary>
    public void Table(string[] head, double[] shares, IEnumerable<string[]> rows, double size = 8.5)
    {
        var total = W - 2 * Margin; var xs = new double[head.Length]; var ws = new double[head.Length]; var acc = Margin;
        for (var i = 0; i < head.Length; i++) { ws[i] = total * shares[i] / shares.Sum(); xs[i] = acc; acc += ws[i]; }
        void Header()
        {
            Room(size * 3); y -= size * 1.4;
            for (var i = 0; i < head.Length; i++) Put(head[i], xs[i] + 2, size, true, (0.4, 0.45, 0.52));
            y -= 4; cur.Append(CultureInfo.InvariantCulture, $"0.75 0.78 0.82 RG 0.6 w {Margin:0.##} {y:0.##} m {W - Margin:0.##} {y:0.##} l S\n");
        }
        Header(); var any = false;
        foreach (var row in rows)
        {
            any = true;
            var cells = row.Select((c, i) => Wrap(c, ws[i] - 6, size, false).ToList()).ToList(); var lines = Math.Max(1, cells.Max(c => c.Count));
            if (y - (lines * size * 1.3 + 6) < Margin) { NewPage(); Header(); }
            y -= size * 1.3;
            for (var li = 0; li < lines; li++)
            {
                for (var i = 0; i < cells.Count; i++) if (li < cells[i].Count) Put(cells[i][li], xs[i] + 2, size, false);
                if (li < lines - 1) y -= size * 1.3;
            }
            y -= 3; cur.Append(CultureInfo.InvariantCulture, $"0.9 0.92 0.94 RG 0.4 w {Margin:0.##} {y:0.##} m {W - Margin:0.##} {y:0.##} l S\n");
        }
        if (!any) { y -= size * 1.4; Put("None.", Margin + 2, size, false, (0.4, 0.45, 0.52)); y -= 4; }
    }

    public byte[] ToBytes()
    {
        var objs = new List<string>(); // object bodies, 1-based by index + 1
        objs.Add("<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", pages.Select((_, i) => (5 + i * 2) + " 0 R"));
        objs.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            var footer = $"BT /F1 8 Tf 0.5 0.55 0.6 rg {Margin:0.##} 28 Td (Page {i + 1} of {pages.Count}) Tj ET\n";
            var content = pages[i] + footer;
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {W.ToString("0.##", CultureInfo.InvariantCulture)} {H.ToString("0.##", CultureInfo.InvariantCulture)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {6 + i * 2} 0 R >>");
            objs.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        using var ms = new MemoryStream(); var offsets = new List<long>();
        void Write(string s) { var b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        for (var i = 0; i < objs.Count; i++) { offsets.Add(ms.Position); Write($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = ms.Position;
        Write($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) Write(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
