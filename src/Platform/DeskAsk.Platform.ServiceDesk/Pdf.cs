using System.Globalization;
using System.Text;

namespace DeskAsk.Platform.ServiceDesk;

internal static class ReportPdf
{
    static string Hours(double? h) => h == null ? "no data" : h < 1 ? Math.Round(h.Value * 60) + " min" : Math.Round(h.Value, 1).ToString(CultureInfo.InvariantCulture) + " h";
    static string Pct(double? p) => p == null ? "no data" : Math.Round(p.Value).ToString(CultureInfo.InvariantCulture) + "%";
    static readonly Dictionary<string, string> Status = new() { ["open"] = "Open", ["progress"] = "In progress", ["hold"] = "Awaiting customer", ["resolved"] = "Resolved", ["closed"] = "Closed" };

    public static byte[] Render(string company, DateTime monthStart, ReportsApi.ReportData d)
    {
        var p = new PdfDoc();
        p.Heading("Service report", 22);
        p.Paragraph(company + "  -  " + monthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture), 11);
        p.Gap(6);
        var facts = new List<(string, string)> {
            ("Tickets opened", d.Opened.ToString(CultureInfo.InvariantCulture)), ("Tickets solved", d.Resolved.ToString(CultureInfo.InvariantCulture)),
            ("Open at the start of the month", d.BacklogStart.ToString(CultureInfo.InvariantCulture)), ("Open at the end of the month", d.BacklogEnd.ToString(CultureInfo.InvariantCulture)),
            ("Average first reply", Hours(d.AvgFirst)), ("Average time to solve", Hours(d.AvgSolve)),
            ("Replied in time", Pct(d.RespondPct)), ("Solved in time", Pct(d.ResolvePct)) };
        if (d.Changes != null) { facts.Add(("Changes finished well", d.Changes.Completed.ToString(CultureInfo.InvariantCulture))); facts.Add(("Changes that failed or were rolled back", d.Changes.Failed.ToString(CultureInfo.InvariantCulture))); }
        p.Facts(facts);
        void Group(string name, List<ReportsApi.Counted> rows) { if (rows.Count == 0) return; p.Heading(name, 12, true); p.Table(new[] { "Name", "Tickets" }, new[] { 5.0, 1 }, rows.Select(r => new[] { r.Name, r.Count.ToString(CultureInfo.InvariantCulture) })); }
        Group("By priority", d.ByPriority); Group("By category", d.ByCategory); Group("By source", d.BySource); Group("People who raised the most tickets", d.ByRequester); Group("Devices with the most tickets", d.ByAsset); Group("By company", d.ByCompany);
        p.Heading("Tickets opened this month", 12, true);
        p.Table(new[] { "Ticket", "Subject", "Status", "Priority", "Opened" }, new[] { 1.3, 5, 1.8, 1.2, 1.4 },
            d.Tickets.Select(t => new[] { t.Number, t.Subject, Status.GetValueOrDefault(t.Status, t.Status), char.ToUpperInvariant(t.Priority[0]) + t.Priority[1..], t.CreatedAt.ToString("dd MMM", CultureInfo.InvariantCulture) }));
        if (d.Tickets.Count >= 300) p.Paragraph("Only the first 300 tickets are listed.", 8.5);
        return p.ToBytes();
    }
}
