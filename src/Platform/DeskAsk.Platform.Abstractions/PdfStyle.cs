using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskAsk.Platform;

/// <summary>
/// The look of every PDF (invoices, reports, asset exports): the colour of table header bands and headings, the letters in the header, borders and text,
/// a line printed at the foot of each page, and the closing note of an invoice. Kept per customer (setting pdf.style); invoices use the vendor's own.
/// Colours are #rrggbb; anything else falls back to the standard (header #207178 with white letters, black borders and text).
/// </summary>
public sealed record PdfStyle(string HeaderColor = "#207178", string HeaderText = "#FFFFFF", string Border = "#000000", string Text = "#000000", string Accent = "#207178", string Footer = "", string Note = "")
{
    public const string Key = "pdf.style";
    public const string DefaultNote = "Paid in full. This is a computer-generated invoice and needs no signature.";
    public static readonly PdfStyle Default = new();
    static readonly Regex Hex = new("^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static bool IsHex(string? s) => s != null && Hex.IsMatch(s);

    /// <summary>The same style with every colour valid and every text trimmed to its limit.</summary>
    public PdfStyle Clean()
    {
        string C(string v, string d) => IsHex(v) ? v.ToUpperInvariant() : d;
        string T(string? v, int max) { v = (v ?? "").Replace("\r", "").Trim(); return v.Length > max ? v[..max] : v; }
        return new PdfStyle(C(HeaderColor, Default.HeaderColor), C(HeaderText, Default.HeaderText), C(Border, Default.Border), C(Text, Default.Text), C(Accent, Default.Accent), T(Footer, 120).Replace("\n", " "), T(Note, 600));
    }

    public static PdfStyle Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return (JsonSerializer.Deserialize<PdfStyle>(json, Web) ?? Default).Clean(); } catch (JsonException) { return Default; }
    }

    public static async Task<PdfStyle> LoadAsync(ISettings s, CancellationToken ct) => Parse(await s.GetAsync(Key, ct));

    public string ToJson() => JsonSerializer.Serialize(Clean(), Web);

    /// <summary>#rrggbb as PDF colour components (0 to 1).</summary>
    public static (double r, double g, double b) Rgb(string hex)
    {
        var h = IsHex(hex) ? hex : "#000000";
        return (Convert.ToInt32(h[1..3], 16) / 255.0, Convert.ToInt32(h[3..5], 16) / 255.0, Convert.ToInt32(h[5..7], 16) / 255.0);
    }
}
