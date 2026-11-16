using System.Text.RegularExpressions;

namespace DeskAsk.Platform.Backup;

/// <summary>What a syslog line or a report mail says about a backup run. Device is the machine the mail names, when it names one.</summary>
public sealed record ParsedRun(string Tool, string Job, string Result, string Message, string Device = "");

/// <summary>
/// Reads backup results out of text: syslog lines from Synology (Hyper Backup, Active Backup), QNAP (Hybrid Backup Sync) and Veeam,
/// and the subject of report mails (Veeam, Acronis, Synology, QNAP, Azure Backup, Macrium and other tools that write the result in the subject).
/// Only lines that clearly report a finished job are read; anything else gives null.
/// </summary>
public static class BackupParse
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly TimeSpan T = TimeSpan.FromMilliseconds(200);

    static string Quotes(string s) => s.Replace('\u201c', '"').Replace('\u201d', '"').Replace('\u201e', '"').Replace('\u2018', '\'').Replace('\u2019', '\'');
    static string Clean(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

    public static ParsedRun? Syslog(string source, string text)
    {
        var t = Clean(Quotes(text ?? "")); if (t.Length == 0) return null;
        // Veeam Backup & Replication syslog: JobName="..." JobResult="0|1|2" (sent for job-finished events)
        var vr = Regex.Match(t, @"JobResult=""(\d)""", I, T);
        var vn = Regex.Match(t, @"JobName=""([^""]+)""", I, T);
        if (vr.Success && vn.Success)
        {
            var d = Regex.Match(t, @"Description=""([^""]*)""", I, T);
            return new("veeam", vn.Groups[1].Value, vr.Groups[1].Value switch { "0" => "success", "1" => "warning", _ => "failed" }, d.Success ? d.Groups[1].Value : t);
        }
        // QNAP Hybrid Backup Sync
        if (t.Contains("Hybrid Backup Sync", StringComparison.OrdinalIgnoreCase) || t.Contains("[HBS", StringComparison.OrdinalIgnoreCase))
        {
            if (Regex.IsMatch(t, @"\brestore\b", I, T) && !Regex.IsMatch(t, @"\bbackup job\b", I, T)) return null;
            var name = Regex.Match(t, @"job:?\s*""([^""]+)""", I, T); if (!name.Success) return null;
            var job = name.Groups[1].Value;
            if (Regex.IsMatch(t, @"failed to complete|job failed|\bfailed\b", I, T)) return new("qnap", job, "failed", t);
            if (Regex.IsMatch(t, @"with errors|with warnings?|completed with warning|partially", I, T)) return new("qnap", job, "warning", t);
            if (Regex.IsMatch(t, @"\b(completed|finished)\b", I, T)) return new("qnap", job, "success", t);
            return null;
        }
        // Synology Hyper Backup: "[Destination][Task name] Backup task finished successfully."
        var syn = Regex.Match(t, @"\[([^\]]*)\]\s*\[([^\]]+)\]\s*(.+)$", I, T);
        if (syn.Success && Regex.IsMatch(syn.Groups[3].Value, @"backup task", I, T))
        {
            var body = syn.Groups[3].Value; var job = syn.Groups[2].Value;
            if (Regex.IsMatch(body, @"partially completed|partially", I, T)) return new("synology", job, "warning", t);
            if (Regex.IsMatch(body, @"failed|exception|not completed|was cancel+ed|aborted", I, T)) return new("synology", job, "failed", t);
            if (Regex.IsMatch(body, @"finished successfully|completed successfully|backup task (has )?(finished|completed)", I, T)) return new("synology", job, "success", t);
            return null;
        }
        // Synology Active Backup for Business: "... backup task [Name] ... completed/failed/partially completed"
        if (t.Contains("Active Backup", StringComparison.OrdinalIgnoreCase))
        {
            var n = Regex.Match(t, @"task\s*\[([^\]]+)\]", I, T); if (!n.Success) n = Regex.Match(t, @"task\s*""([^""]+)""", I, T);
            if (!n.Success) return null;
            var job = n.Groups[1].Value;
            if (Regex.IsMatch(t, @"partially", I, T)) return new("synology", job, "warning", t);
            if (Regex.IsMatch(t, @"failed|error", I, T)) return new("synology", job, "failed", t);
            if (Regex.IsMatch(t, @"completed|succeeded|finished", I, T)) return new("synology", job, "success", t);
        }
        return null;
    }

    /// <summary>The tool a mail comes from, by sender, subject and body.</summary>
    public static string MailTool(string from, string subject, string body, string fallback)
    {
        var all = (from + " " + subject + " " + (body.Length > 2000 ? body[..2000] : body)).ToLowerInvariant();
        if (all.Contains("veeam")) return "veeam";
        if (all.Contains("acronis")) return "acronis";
        if (all.Contains("hyper backup") || all.Contains("active backup") || all.Contains("synology")) return "synology";
        if (all.Contains("hybrid backup sync") || all.Contains("qnap")) return "qnap";
        if (all.Contains("macrium") || all.Contains("reflect")) return "macrium";
        if (all.Contains("azure backup") || all.Contains("recovery services") || all.Contains("azure-noreply")) return "azure";
        if (all.Contains("windows server backup")) return "wsb";
        return BackupModule.Tools.Contains(fallback) && fallback != "" ? fallback : "other";
    }

    /// <summary>A report mail: the result and job from the subject (and the body when the subject only says "Backup report").</summary>
    public static ParsedRun? Mail(string from, string subject, string body, string fallbackTool)
    {
        var s = Clean(Quotes(subject ?? ""));
        s = Regex.Replace(s, @"^((re|fw|fwd|aw|wg)\s*:\s*)+", "", I, T);
        if (s.Length == 0) return null;
        var tool = MailTool(from ?? "", s, body ?? "", fallbackTool);
        // Veeam: "[Success] Job name (3 machines)" / "[Warning] ..." / "[Failed] ..."
        var v = Regex.Match(s, @"^\[(Success|Warning|Failed|Error)\]\s*(.+?)(\s*\((\d+)\s+[^)]*\).*)?$", I, T);
        if (v.Success)
        {
            var res = v.Groups[1].Value.ToLowerInvariant() switch { "success" => "success", "warning" => "warning", _ => "failed" };
            var job = v.Groups[2].Value;
            var dev = Regex.Match(job, @"^(.+?)\s+on\s+([A-Za-z0-9._-]+)$", I, T);
            return dev.Success ? new(tool, dev.Groups[1].Value.Trim(), res, s, dev.Groups[2].Value) : new(tool, job.Trim(), res, s);
        }
        var result = Result(s) ?? (Regex.IsMatch(s, @"\b(backup|job|task|plan)\b", I, T) ? Result(Clean(body ?? "").Length > 1500 ? Clean(body!)[..1500] : Clean(body ?? "")) : null);
        if (result == null) return null;
        // a name in quotes is the job (Acronis: "Plan 'Daily' ... machine 'SRV01'"; QNAP: job "X"); otherwise the subject without the result words
        var quoted = Regex.Matches(s, @"[""']([^""']{2,120})[""']", I, T).Select(m => m.Groups[1].Value).ToList();
        var device = "";
        var onMachine = Regex.Match(s, @"\b(?:machine|computer|device|server|host|on)\s+[""']?([A-Za-z0-9][A-Za-z0-9._-]{1,62})[""']?", I, T);
        string name;
        if (quoted.Count > 0)
        {
            name = quoted[0];
            if (quoted.Count > 1) device = quoted[1];
            else if (onMachine.Success && !string.Equals(onMachine.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) device = onMachine.Groups[1].Value;
        }
        else
        {
            name = Regex.Replace(s, @"\[[^\]]*\]", " ", I, T);
            name = Regex.Replace(name, @"\b(has\s+)?(completed|finished|ended)\s+(successfully|with (errors?|warnings?))\b|\b(succeeded|successful(ly)?|success|failed|failure|failing|error(s)?|warning(s)?|unsuccessful|partially completed|not completed|completed|finished|aborted)\b", " ", I, T);
            name = Regex.Replace(name, @"\(\d+[^)]*\)|\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}|\d{1,2}:\d{2}(:\d{2})?(\s*(am|pm))?", " ", I, T);
            name = Regex.Replace(name, @"[:\-–|,]+\s*$|^\s*[:\-–|,]+", "", I, T);
            name = Clean(Regex.Replace(name, @"\s{2,}", " "));
            name = Regex.Replace(name, @"[\s:\-–|,.]+$", "");
            if (name.Length < 2) name = "Backup";
        }
        return new(tool, name.Length > 200 ? name[..200] : name, result, s, device);
    }

    /// <summary>success, warning or failed from words like "completed with errors", "failed", "succeeded"; null when the text says none of them.</summary>
    public static string? Result(string text)
    {
        if (Regex.IsMatch(text, @"with errors?|with warnings?|partially|\bwarning\b|completed with issues", I, T)) return "warning";
        if (Regex.IsMatch(text, @"\b(fail(ed|ure|ing)?|error|unsuccessful|not completed|aborted|did not (run|complete)|missed)\b", I, T)) return "failed";
        if (Regex.IsMatch(text, @"\b(success(ful(ly)?)?|succeeded|completed|finished|\bok\b)\b", I, T)) return "success";
        return null;
    }
}
