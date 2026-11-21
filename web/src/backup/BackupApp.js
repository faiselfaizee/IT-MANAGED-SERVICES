import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useHash, useInterval, useLoad } from '../http';
import { ago, Act, Copy, Empty, Err, fmtBytes, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { CountUp, MiniRing, Ring } from '../Rings';
import { BarList } from '../monitoring/Dashboard';
import { Plans, PlanPage } from './Plans';
import Report from './Report';
const n = (v) => Number(v ?? 0);
export const TOOLS = {
    deskask: 'DeskAsk file backup', veeam: 'Veeam', wsb: 'Windows Server Backup', mars: 'Azure Recovery Services agent (MARS)', mabs: 'Azure Backup Server (MABS/DPM)',
    synology: 'Synology', qnap: 'QNAP', acronis: 'Acronis', azure: 'Microsoft Azure Backup', macrium: 'Macrium', other: 'Other',
};
const STATUS = {
    success: ['Success', 'ok'], warning: ['Warning', 'warn'], failed: ['Failed', 'bad'], missed: ['Missed', 'bad'], waiting: ['Waiting for a first result', ''], ignored: ['Not watched', ''],
};
const AGE = { day: 'Good backup within a day', week: 'Last good backup 1 to 7 days ago', older: 'Last good backup over a week ago', never: 'Never a good backup' };
const SOURCE = { agent: 'Agent', syslog: 'Syslog', mail: 'Report mail', api: 'Report address' };
const coKey = (m) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
export const jobsHref = (co, filter = '') => '#/backup/jobs' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
export function statusParts(m, co) {
    return [
        { key: 'success', label: 'Success', value: n(m.success), tone: 'ok', href: jobsHref(co, 'status=success') },
        { key: 'warning', label: 'Warning', value: n(m.warning), tone: 'warn', href: jobsHref(co, 'status=warning') },
        { key: 'failed', label: 'Failed', value: n(m.failed), tone: 'bad', href: jobsHref(co, 'status=failed') },
        { key: 'missed', label: 'Missed', value: n(m.missed), tone: 'bad', href: jobsHref(co, 'status=missed') },
        { key: 'waiting', label: 'Waiting', value: n(m.waiting), tone: 'none', href: jobsHref(co, 'status=waiting') },
    ];
}
export function StatusBadge({ s }) {
    const [w, t] = STATUS[s] ?? [s, ''];
    return _jsx("span", { className: `badge ${t}`, children: w });
}
const MENU = [
    { group: 'Overview', items: [{ key: '', label: 'Dashboard' }] },
    { group: 'Backups', items: [{ key: 'jobs', label: 'Backup jobs' }, { key: 'plans', label: 'File backups' }, { key: 'devices', label: 'Devices with backup software' }] },
    { group: 'Reports', items: [{ key: 'report', label: 'Backup status report' }] },
    { group: 'Settings', items: [{ key: 'settings', label: 'Alerts', admin: true }, { key: 'senders', label: 'Report mails', admin: true }, { key: 'keys', label: 'Report address', admin: true }, { key: 'connect', label: 'How to connect' }] },
];
/** The Backup module: watches the backup software customers already run. Routes inside #/backup. */
export default function BackupApp() {
    const [, a, b] = useHash();
    const me = useLoad('/api/platform/me');
    const role = String(me.data?.role ?? '');
    const admin = role === 'admin';
    const canEdit = admin || role === 'engineer';
    const section = a === 'customer' ? '' : a === 'job' ? 'jobs' : a === 'plan' ? 'plans' : a ?? '';
    let page;
    switch (a) {
        case 'customer':
            page = _jsx(CustomerDash, { id: b ?? 'all' }, b);
            break;
        case 'jobs':
            page = _jsx(Jobs, {}, location.hash);
            break;
        case 'job':
            page = _jsx(JobPage, { id: b ?? '', canEdit: canEdit }, b);
            break;
        case 'devices':
            page = _jsx(Devices, {});
            break;
        case 'plans':
            page = _jsx(Plans, { admin: admin });
            break;
        case 'plan':
            page = _jsx(PlanPage, { id: b ?? '', admin: admin, canRun: canEdit }, b);
            break;
        case 'settings':
            page = admin ? _jsx(Settings, {}) : _jsx(Dashboard, {});
            break;
        case 'senders':
            page = admin ? _jsx(Senders, {}) : _jsx(Dashboard, {});
            break;
        case 'keys':
            page = admin ? _jsx(Keys, {}) : _jsx(Dashboard, {});
            break;
        case 'connect':
            page = _jsx(Connect, {});
            break;
        case 'report':
            page = _jsx(Report, {});
            break;
        default: page = _jsx(Dashboard, {});
    }
    return _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": "Backup", children: [_jsx("div", { className: "modnav-head", children: _jsx("strong", { children: "Backup" }) }), MENU.map((g) => {
                        const items = g.items.filter((i) => !i.admin || admin);
                        return items.length === 0 ? null : _jsxs("div", { className: "modnav-group", children: [g.group && _jsx("h4", { children: g.group }), items.map((i) => _jsx("a", { href: '#/backup' + (i.key ? '/' + i.key : ''), className: section === i.key ? 'on' : '', children: i.label }, i.key))] }, g.group);
                    })] }), _jsx("main", { className: "modmain", children: page })] });
}
/** One tile per customer, worst first, with a small ring of its jobs' last results. */
function Dashboard() {
    const { data, error, loading, reload } = useLoad('/api/backup/dashboard');
    useInterval(reload, 60_000);
    const [q, setQ] = useState('');
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const tiles = (Array.isArray(data?.tiles) ? data.tiles : []);
    const all = (data?.all ?? {});
    if (data?.restricted && tiles.length === 1)
        return _jsx(CustomerDash, { id: coKey(tiles[0]) });
    const list = tiles.filter((t) => !q || String(t.company).toLowerCase().includes(q.toLowerCase()));
    return _jsxs(_Fragment, { children: [_jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: "Backup" }), _jsxs("p", { className: "muted", children: [tiles.length, " customers \u00B7 ", n(all.jobs), " jobs \u00B7 ", n(all.failed), " failed, ", n(all.missed), " missed, ", n(all.warning), " with a warning"] })] }), tiles.length === 0 ? _jsxs("div", { className: "card", children: [_jsx("h2", { style: { marginTop: 0 }, children: "No backup results yet" }), _jsx("p", { children: "Backup reads the results of the backup software your customers already use. Results come from the agent (Veeam, Windows Server Backup, Azure Backup), syslog from NAS devices, report mails and a report address for scripts." }), _jsxs("p", { children: [_jsx("a", { className: "button primary", href: "#/backup/connect", children: "How to connect each tool" }), " ", n(all.devicesWithTools) > 0 && _jsxs("span", { className: "muted", children: ["The agent has found backup software on ", n(all.devicesWithTools), " devices; their first results arrive within 15 minutes."] })] })] }) : _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Find a customer", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Find a customer" }), _jsx("a", { className: "button", href: jobsHref('all', 'status=alert'), children: "Jobs with an alert" })] }), _jsxs("div", { className: "co-tiles", children: [!q && _jsx(Tile, { m: all, index: 0 }), list.map((t, i) => _jsx(Tile, { m: t, index: i + 1 }, coKey(t)))] })] })] });
}
function Tile({ m, index }) {
    const co = coKey(m);
    const delay = Math.min(index, 12) * 70;
    const bad = n(m.failed) + n(m.missed);
    return _jsxs("a", { className: `co-tile${co === 'all' ? ' all' : ''}${bad > 0 ? ' attn' : n(m.warning) > 0 ? ' warn' : ''}`, href: `#/backup/customer/${co}`, children: [_jsxs("div", { className: "co-tile-head", children: [_jsx("b", { className: "co-name", children: m.company }), _jsxs("span", { className: "muted", children: [_jsx(CountUp, { value: n(m.jobs), delay: delay }), " jobs"] })] }), _jsxs("div", { className: "co-tile-body", children: [_jsx(MiniRing, { parts: statusParts(m, co), label: `Backup jobs of ${m.company}`, delay: delay }), _jsxs("ul", { className: "co-figs", children: [_jsxs("li", { className: n(m.failed) > 0 ? 'bad' : '', children: [_jsx("i", { className: "dot tone-bad", "aria-hidden": "true" }), _jsx("span", { children: "Failed" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.failed), delay: delay }) })] }), _jsxs("li", { className: n(m.missed) > 0 ? 'bad' : '', children: [_jsx("i", { className: "dot tone-bad", "aria-hidden": "true" }), _jsx("span", { children: "Missed" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.missed), delay: delay }) })] }), _jsxs("li", { className: n(m.warning) > 0 ? 'warn' : '', children: [_jsx("i", { className: "dot tone-warn", "aria-hidden": "true" }), _jsx("span", { children: "Warning" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.warning), delay: delay }) })] }), _jsxs("li", { children: [_jsx("i", { className: "dot tone-ok", "aria-hidden": "true" }), _jsx("span", { children: "Success" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.success), delay: delay }) })] }), _jsxs("li", { className: n(m.ageNever) + n(m.ageOlder) > 0 ? 'bad' : '', children: [_jsx("span", { children: "No good backup this week" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.ageNever) + n(m.ageOlder), delay: delay }) })] })] })] })] });
}
/** One customer's backup rings; every part opens the job list filtered to it. */
function CustomerDash({ id }) {
    const { data, error, loading, reload } = useLoad('/api/backup/dashboard');
    useInterval(reload, 60_000);
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const tiles = (Array.isArray(data?.tiles) ? data.tiles : []);
    const m = id === 'all' ? data?.all : tiles.find((t) => coKey(t) === id);
    if (!m)
        return _jsx(Empty, { children: "This customer has no backup jobs." });
    const co = id;
    const bt = (m.byTool ?? {});
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/backup", children: "Backup" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), _jsx("span", { "aria-current": "page", children: m.company })] }), _jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: m.company }), _jsxs("p", { className: "muted", children: [n(m.jobs), " jobs on ", n(m.devices), " devices \u00B7 ", n(m.alerts), " with an open alert"] }), _jsx("a", { className: "button primary", href: jobsHref(co), children: "View all jobs" })] }), _jsxs("div", { className: "rings dash-rings", children: [_jsx(Ring, { title: "Last result", unit: "jobs", delay: 0, empty: "No jobs yet.", parts: statusParts(m, co) }), _jsx(Ring, { title: "Last good backup", unit: "jobs", delay: 120, empty: "No watched jobs.", parts: [
                            { key: 'day', label: 'Within a day', value: n(m.ageDay), tone: 'ok', href: jobsHref(co, 'age=day') },
                            { key: 'week', label: '1 to 7 days ago', value: n(m.ageWeek), tone: 'warn', href: jobsHref(co, 'age=week') },
                            { key: 'older', label: 'Over a week ago', value: n(m.ageOlder), tone: 'bad', href: jobsHref(co, 'age=older') },
                            { key: 'never', label: 'Never', value: n(m.ageNever), tone: 'none', href: jobsHref(co, 'age=never') }
                        ] }), _jsx(BarList, { title: "Jobs by software", empty: "No jobs yet.", rows: Object.keys(TOOLS).filter((k) => n(bt[k]) > 0).map((k) => ({ key: k, label: TOOLS[k], value: n(bt[k]), href: jobsHref(co, 'tool=' + k) })) })] }), _jsx("p", { className: "muted tile-foot", children: "A job is missed when nothing has arrived for its interval plus the grace hours in Backup \u203A Alerts. Jobs that are not watched are left out of the rings." })] });
}
const FILTERS = {
    status: { ...Object.fromEntries(Object.entries(STATUS).map(([k, v]) => [k, v[0]])), alert: 'With an open alert' },
    age: AGE, tool: TOOLS,
};
function Jobs() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const company = qs.get('company') ?? '';
    const asset = qs.get('asset') ?? '';
    const filters = Object.keys(FILTERS).map((k) => [k, qs.get(k) ?? '']).filter(([k, v]) => v && FILTERS[k][v]);
    const [q, setQ] = useState('');
    const url = '/api/backup/jobs?q=' + encodeURIComponent(q) + (company ? '&company=' + encodeURIComponent(company) : '') + (asset ? '&asset=' + encodeURIComponent(asset) : '') + filters.map(([k, v]) => `&${k}=${v}`).join('');
    const { data, error, loading, reload } = useLoad(url);
    useInterval(reload, 60_000);
    const items = (Array.isArray(data?.items) ? data.items : []);
    const without = (key) => { const x = new URLSearchParams(qs); x.delete(key); const t = x.toString(); return '#/backup/jobs' + (t ? '?' + t : ''); };
    const coName = company === 'none' ? 'No customer' : company ? String(items[0]?.company ?? 'Customer') : '';
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/backup", children: "Backup" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), company && _jsxs(_Fragment, { children: [_jsx("a", { href: `#/backup/customer/${company}`, children: coName }), _jsx("span", { "aria-hidden": "true", children: "\u203A" })] }), _jsx("span", { "aria-current": "page", children: filters.length ? filters.map(([k, v]) => FILTERS[k][v]).join(', ') : 'All jobs' })] }), _jsx("h1", { children: "Backup jobs" }), (company || asset || filters.length > 0) && _jsxs("div", { className: "filter-chips", children: [company && _jsxs("a", { href: without('company'), title: "Show every customer", children: [coName, " \u2715"] }), asset && _jsxs("a", { href: without('asset'), title: "Show every device", children: [String(items[0]?.assetName ?? 'Device'), " \u2715"] }), filters.map(([k, v]) => _jsxs("a", { href: without(k), title: "Remove this filter", children: [FILTERS[k][v], " \u2715"] }, k))] }), _jsx("div", { className: "tools", children: _jsx("input", { placeholder: "Search job, device or customer", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Search jobs" }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No jobs match." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Status" }), _jsx("th", { children: "Job" }), _jsx("th", { children: "Software" }), _jsx("th", { children: "Device" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Last result" }), _jsx("th", { children: "Last good backup" }), _jsx("th", { children: "Expected" })] }) }), _jsx("tbody", { children: items.map((j) => _jsxs("tr", { children: [_jsxs("td", { children: [_jsx(StatusBadge, { s: j.status }), j.ticketNumber && _jsx("div", { className: "muted small", children: j.ticketNumber })] }), _jsxs("td", { children: [_jsx("a", { href: `#/backup/job/${j.id}`, children: j.name }), _jsx("div", { className: "muted small", children: SOURCE[j.source] ?? j.source })] }), _jsx("td", { children: j.toolName }), _jsx("td", { children: j.assetName || _jsx("span", { className: "muted", children: "\u2013" }) }), _jsx("td", { children: j.company || _jsx("span", { className: "muted", children: "\u2013" }) }), _jsx("td", { children: j.lastAt ? ago(j.lastAt) : _jsx("span", { className: "muted", children: "none yet" }) }), _jsx("td", { children: j.lastSuccessAt ? ago(j.lastSuccessAt) : _jsx("span", { className: "muted", children: "never" }) }), _jsx("td", { children: n(j.everyHours) > 0 ? `every ${j.everyHours} h` : _jsx("span", { className: "muted", children: "no schedule" }) })] }, j.id)) })] }) })), n(data?.total) > items.length && _jsxs("p", { className: "muted", children: ["Showing ", items.length, " of ", n(data?.total), "; narrow the search to see the rest."] })] });
}
function JobPage({ id, canEdit }) {
    const { data, error, loading, reload } = useLoad(`/api/backup/jobs/${encodeURIComponent(id)}`);
    const [edit, setEdit] = useState(false);
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error || !data)
        return _jsx(Loading, { loading: false, error: error || 'Not found' });
    const j = data.job;
    const runs = (Array.isArray(data.runs) ? data.runs : []);
    const mo = (data.month ?? {});
    const co = j.companyId == null ? 'none' : String(j.companyId);
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/backup", children: "Backup" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), _jsx("a", { href: `#/backup/customer/${co}`, children: j.company || 'No customer' }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), _jsx("span", { "aria-current": "page", children: j.name })] }), _jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: j.name }), _jsx(StatusBadge, { s: j.status }), canEdit && _jsxs(_Fragment, { children: [_jsx("button", { onClick: () => setEdit(true), children: "Change" }), _jsx(Act, { label: "Delete", danger: true, confirm: `Delete the job "${j.name}" and its history? If the software reports it again, it comes back as a new job.`, run: async () => { await api(`/api/backup/jobs/${j.id}`, { method: 'DELETE' }); location.hash = '#/backup/jobs'; } })] })] }), j.alert && _jsxs("p", { className: "note", children: ["Alert open since ", fmtDate(j.alertAt ?? j.lastAt), ": ", j.alert === 'missed' ? 'no result arrived in time' : j.alert === 'warning' ? 'the last run finished with a warning' : `the last ${n(j.fails) > 1 ? n(j.fails) + ' runs' : 'run'} failed`, j.ticketNumber ? ` · ticket ${j.ticketNumber}` : '', "."] }), _jsxs("div", { className: "grid", children: [_jsxs("section", { className: "card", children: [_jsx("h2", { style: { marginTop: 0 }, children: "Job" }), _jsxs("dl", { className: "kv", children: [_jsx("dt", { children: "Software" }), _jsx("dd", { children: j.toolName }), _jsx("dt", { children: "Device" }), _jsx("dd", { children: j.assetId ? _jsx("a", { href: `#/assets/${j.assetId}`, children: j.assetName }) : j.assetName || '–' }), _jsx("dt", { children: "Customer" }), _jsx("dd", { children: j.company || '–' }), _jsx("dt", { children: "Reported by" }), _jsx("dd", { children: SOURCE[j.source] ?? j.source }), _jsx("dt", { children: "Expected" }), _jsx("dd", { children: n(j.everyHours) > 0 ? `every ${j.everyHours} hours (missed after ${n(j.everyHours) + n(data.graceHours)} hours without a result)` : 'no schedule (never missed)' }), _jsx("dt", { children: "Watched" }), _jsx("dd", { children: j.watched ? 'Yes' : 'No: no status and no alerts' }), j.notes && _jsxs(_Fragment, { children: [_jsx("dt", { children: "Notes" }), _jsx("dd", { style: { whiteSpace: 'pre-wrap' }, children: j.notes })] })] })] }), _jsxs("section", { className: "card", children: [_jsx("h2", { style: { marginTop: 0 }, children: "Last 30 days" }), _jsx(Ring, { title: "Runs", unit: "runs", empty: "No runs in the last 30 days.", parts: [
                                    { key: 's', label: 'Success', value: n(mo.success), tone: 'ok' }, { key: 'w', label: 'Warning', value: n(mo.warning), tone: 'warn' }, { key: 'f', label: 'Failed', value: n(mo.failed), tone: 'bad' }
                                ] }), _jsxs("dl", { className: "kv", children: [_jsx("dt", { children: "Last run" }), _jsx("dd", { children: j.lastAt ? `${fmtDate(j.lastAt)} (${ago(j.lastAt)})` : 'none yet' }), _jsx("dt", { children: "Last good backup" }), _jsx("dd", { children: j.lastSuccessAt ? `${fmtDate(j.lastSuccessAt)} (${ago(j.lastSuccessAt)})` : 'never' })] })] })] }), _jsx("h2", { children: "Runs" }), runs.length === 0 ? _jsx(Empty, { children: "No runs yet." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Result" }), _jsx("th", { children: "Finished" }), _jsx("th", { children: "Took" }), _jsx("th", { children: "Size" }), _jsx("th", { children: "Message" })] }) }), _jsx("tbody", { children: runs.map((r) => _jsxs("tr", { children: [_jsx("td", { children: _jsx(StatusBadge, { s: r.result }) }), _jsx("td", { children: fmtDate(r.ended) }), _jsx("td", { children: r.minutes != null ? `${r.minutes} min` : '–' }), _jsx("td", { children: n(r.bytes) > 0 ? fmtBytes(n(r.bytes)) : '–' }), _jsx("td", { className: "small", style: { maxWidth: 520, overflowWrap: 'anywhere' }, children: r.message || _jsx("span", { className: "muted", children: "\u2013" }) })] }, r.id)) })] }) }), edit && _jsx(EditJob, { j: j, onClose: () => setEdit(false), onSaved: () => { setEdit(false); reload(); } })] });
}
function EditJob({ j, onClose, onSaved }) {
    const [every, setEvery] = useState(String(j.everyHours));
    const [watched, setWatched] = useState(!!j.watched);
    const [notes, setNotes] = useState(String(j.notes ?? ''));
    const { err, busy, submit } = useSubmit(onSaved);
    return _jsx(Modal, { title: `Change ${j.name}`, onClose: onClose, children: _jsxs("form", { className: "form", onSubmit: (e) => { e.preventDefault(); void submit(() => api(`/api/backup/jobs/${j.id}`, { method: 'PUT', body: { everyHours: Number(every), watched, notes } })); }, children: [_jsxs("label", { children: [_jsx("span", { children: "Expected every (hours; 24 for daily, 168 for weekly, 0 for no schedule)" }), _jsx("input", { type: "number", min: 0, max: 744, value: every, onChange: (e) => setEvery(e.target.value) })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: watched, onChange: (e) => setWatched(e.target.checked) }), " Watch this job (status, alerts, dashboard)"] }), _jsxs("label", { children: [_jsx("span", { children: "Notes" }), _jsx("textarea", { rows: 3, value: notes, onChange: (e) => setNotes(e.target.value) })] }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { type: "button", onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary", disabled: busy, children: "Save" })] })] }) });
}
function Devices() {
    const { data, error, loading } = useLoad('/api/backup/devices');
    const list = Array.isArray(data) ? data : [];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Devices with backup software" }), _jsx("p", { className: "muted", children: "What the agent found on each device in the last week. Its results are read every 15 minutes." }), _jsx(Loading, { loading: loading && !data, error: error }), data && (list.length === 0 ? _jsx(Empty, { children: "The agent has not found Veeam, Windows Server Backup or Azure Backup on any device yet." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Device" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Backup software" }), _jsx("th", { children: "Jobs" }), _jsx("th", { children: "Seen" })] }) }), _jsx("tbody", { children: list.map((d) => _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("a", { href: `#/assets/${d.assetId}`, children: d.name }), _jsx("div", { className: "muted small", children: d.tag })] }), _jsx("td", { children: d.company || '–' }), _jsx("td", { children: (Array.isArray(d.tools) ? d.tools : []).map((t) => t.name).join(', ') }), _jsx("td", { children: n(d.jobs) > 0 ? _jsx("a", { href: jobsHref('all', 'asset=' + d.assetId), children: d.jobs }) : _jsx("span", { className: "muted", children: "no results yet" }) }), _jsx("td", { children: ago(d.seenAt) })] }, d.assetId)) })] }) }))] });
}
function useCompanies() { const c = useLoad('/api/assets/companies'); return Array.isArray(c.data) ? c.data : []; }
function Settings() {
    const { data, error, loading, reload } = useLoad('/api/backup/settings');
    const companies = useCompanies();
    const [f, setF] = useState(null);
    const [msg, setMsg] = useState('');
    const [err, setErr] = useState('');
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    const s = f ?? data ?? {};
    const set = (k, v) => setF({ ...s, [k]: v });
    const own = (s.companies ?? {});
    const save = async () => { setErr(''); setMsg(''); try {
        await api('/api/backup/settings', { method: 'PUT', body: s });
        setMsg('Saved.');
        setF(null);
        reload();
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Backup alerts" }), _jsx("section", { className: "card", children: _jsxs("div", { className: "form", children: [_jsxs("label", { children: [_jsx("span", { children: "Send alerts to (everyone who looks after backups; commas between addresses)" }), _jsx("input", { value: s.recipients ?? '', onChange: (e) => set('recipients', e.target.value) })] }), _jsxs("div", { className: "row", children: [_jsxs("label", { children: [_jsx("span", { children: "Alert after this many failed runs in a row" }), _jsx("input", { type: "number", min: 1, max: 20, value: s.failuresBeforeAlert ?? 1, onChange: (e) => set('failuresBeforeAlert', Number(e.target.value)) })] }), _jsxs("label", { children: [_jsx("span", { children: "Grace before a job counts as missed (hours)" }), _jsx("input", { type: "number", min: 0, max: 72, value: s.graceHours ?? 6, onChange: (e) => set('graceHours', Number(e.target.value)) })] })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: !!s.alertWarnings, onChange: (e) => set('alertWarnings', e.target.checked) }), " A run that finishes with a warning alerts too"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: s.ticket ?? true, onChange: (e) => set('ticket', e.target.checked) }), " Open a Service Desk ticket for each alert"] }), _jsxs("label", { children: [_jsx("span", { children: "Ticket priority" }), _jsx("select", { value: s.ticketPriority ?? 'high', onChange: (e) => set('ticketPriority', e.target.value), children: ['low', 'medium', 'high', 'critical'].map((p) => _jsx("option", { value: p, children: p }, p)) })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: s.mailResolved ?? true, onChange: (e) => set('mailResolved', e.target.checked) }), " Mail again when a job is back to normal"] })] }) }), _jsxs("section", { className: "card", children: [_jsx("h2", { style: { marginTop: 0 }, children: "A customer's own recipients" }), _jsx("p", { className: "muted", children: "They get their own customer's alerts as well as the list above." }), companies.length === 0 ? _jsx(Empty, { children: "No customers yet." }) : _jsx("table", { children: _jsx("tbody", { children: companies.map((c) => _jsxs("tr", { children: [_jsx("td", { children: c.name }), _jsx("td", { children: _jsx("input", { style: { width: '100%' }, placeholder: "it@customer.example", value: own[String(c.id)] ?? '', onChange: (e) => set('companies', { ...own, [String(c.id)]: e.target.value }), "aria-label": `Recipients for ${c.name}` }) })] }, c.id)) }) })] }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { className: "primary", onClick: () => void save(), children: "Save" }), msg && _jsx("span", { className: "muted", role: "status", children: msg }), _jsx(Err, { text: err })] })] });
}
function Senders() {
    const { data, error, loading, reload } = useLoad('/api/backup/senders');
    const companies = useCompanies();
    const [pattern, setPattern] = useState('');
    const [company, setCompany] = useState('');
    const [tool, setTool] = useState('');
    const [err, setErr] = useState('');
    const list = Array.isArray(data) ? data : [];
    const add = async () => {
        setErr('');
        try {
            await api('/api/backup/senders', { method: 'POST', body: { pattern, companyId: company ? Number(company) : null, tool } });
            setPattern('');
            reload();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Report mails" }), _jsx("p", { children: "Backup software that mails a report after each job (Veeam, Acronis, Synology, QNAP, Macrium, Azure Backup and most others) can send it to the Service Desk mailbox. Mail from the senders listed here is read as a backup result and never becomes a ticket. The result and job come from the subject; the customer from the sender." }), _jsx("p", { className: "muted", children: "Needs the mailbox under Administration \u203A Mail \u203A Incoming mail. A sender is an address (backup@nas.customer.com) or a whole domain (@customer.com)." }), _jsxs("section", { className: "card", children: [_jsxs("div", { className: "row", children: [_jsxs("label", { children: [_jsx("span", { children: "Sender" }), _jsx("input", { value: pattern, onChange: (e) => setPattern(e.target.value), placeholder: "backup@nas.customer.com or @customer.com" })] }), _jsxs("label", { children: [_jsx("span", { children: "Customer" }), _jsxs("select", { value: company, onChange: (e) => setCompany(e.target.value), children: [_jsx("option", { value: "", children: "No customer" }), companies.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] })] }), _jsxs("label", { children: [_jsx("span", { children: "Software (when the mail does not say)" }), _jsxs("select", { value: tool, onChange: (e) => setTool(e.target.value), children: [_jsx("option", { value: "", children: "Work it out from the mail" }), Object.entries(TOOLS).map(([k, v]) => _jsx("option", { value: k, children: v }, k))] })] })] }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { className: "primary", disabled: !pattern.trim(), onClick: () => void add(), children: "Add sender" }), _jsx(Err, { text: err })] })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (list.length === 0 ? _jsx(Empty, { children: "No senders yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Sender" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Software" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((s) => _jsxs("tr", { children: [_jsx("td", { children: s.pattern }), _jsx("td", { children: s.company || '–' }), _jsx("td", { children: s.tool ? TOOLS[s.tool] ?? s.tool : 'From the mail' }), _jsx("td", { children: _jsx(Act, { label: "Remove", danger: true, run: async () => { await api(`/api/backup/senders/${s.id}`, { method: 'DELETE' }); reload(); } }) })] }, s.id)) })] }))] });
}
function Keys() {
    const { data, error, loading, reload } = useLoad('/api/backup/keys');
    const companies = useCompanies();
    const [name, setName] = useState('');
    const [company, setCompany] = useState('');
    const [token, setToken] = useState('');
    const [err, setErr] = useState('');
    const list = Array.isArray(data) ? data : [];
    const url = location.origin + '/api/public/backup/report';
    const create = async () => {
        setErr('');
        try {
            const r = await api('/api/backup/keys', { method: 'POST', body: { name, companyId: company ? Number(company) : null } });
            setToken(String(r.token));
            setName('');
            reload();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    const example = `$body = @{ job = "Robocopy to USB"; result = "success"; device = $env:COMPUTERNAME; tool = "other"; message = "" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "${url}" -Headers @{ Authorization = "Bearer <key>" } -ContentType "application/json" -Body $body`;
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Report address" }), _jsx("p", { children: "Scripts and tools that can call a web address after a job (Robocopy, rsync, SQL Server agent jobs, a NAS task, Acronis or Veeam post-job scripts) send their result here with a key. Each key belongs to one customer." }), _jsxs("dl", { className: "kv", children: [_jsx("dt", { children: "Address" }), _jsx("dd", { children: _jsx(Copy, { text: url }) }), _jsx("dt", { children: "Method" }), _jsxs("dd", { children: ["POST, JSON, header ", _jsx("code", { children: "Authorization: Bearer <key>" })] }), _jsx("dt", { children: "Fields" }), _jsxs("dd", { children: [_jsx("code", { children: "job" }), " and ", _jsx("code", { children: "result" }), " (success, warning or failed) are needed; ", _jsx("code", { children: "device" }), ", ", _jsx("code", { children: "tool" }), ", ", _jsx("code", { children: "started" }), ", ", _jsx("code", { children: "ended" }), ", ", _jsx("code", { children: "message" }), ", ", _jsx("code", { children: "bytes" }), " and ", _jsx("code", { children: "key" }), " (sending the same key twice stores the run once) may be added."] })] }), _jsx("pre", { className: "term", style: { minHeight: 0 }, children: example }), _jsxs("section", { className: "card", children: [_jsxs("div", { className: "row", children: [_jsxs("label", { children: [_jsx("span", { children: "Name" }), _jsx("input", { value: name, onChange: (e) => setName(e.target.value), placeholder: "Acme file server scripts" })] }), _jsxs("label", { children: [_jsx("span", { children: "Customer" }), _jsxs("select", { value: company, onChange: (e) => setCompany(e.target.value), children: [_jsx("option", { value: "", children: "No customer" }), companies.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] })] })] }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { className: "primary", disabled: !name.trim(), onClick: () => void create(), children: "Create key" }), _jsx(Err, { text: err })] })] }), token && _jsxs("p", { className: "note", children: ["The new key is shown only now. Copy it into the script: ", _jsx(Copy, { text: token })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (list.length === 0 ? _jsx(Empty, { children: "No keys yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Starts with" }), _jsx("th", { children: "Last used" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((k) => _jsxs("tr", { children: [_jsx("td", { children: k.name }), _jsx("td", { children: k.company || '–' }), _jsx("td", { children: _jsxs("code", { children: [k.prefix, "\u2026"] }) }), _jsx("td", { children: k.lastUsed ? ago(k.lastUsed) : 'never' }), _jsx("td", { children: _jsx(Act, { label: "Revoke", danger: true, confirm: `Revoke the key "${k.name}"? Scripts using it will be refused.`, run: async () => { await api(`/api/backup/keys/${k.id}`, { method: 'DELETE' }); reload(); } }) })] }, k.id)) })] }))] });
}
/** How each tool reaches DeskAsk. */
function Connect() {
    const rows = [
        ['Veeam Backup & Replication, Veeam Agent for Windows', 'Agent', 'Install the DeskAsk agent (5.1 or later) on the Veeam server or the computer with Veeam Agent. It reads the "Veeam Backup" and "Veeam Agent" event logs every 15 minutes. Veeam can also send syslog to the site controller (Options › Event Forwarding) or report mails.'],
        ['Windows Server Backup', 'Agent', 'The agent on the server reads the backup history (Get-WBJob). Nothing to set up.'],
        ['Azure Recovery Services agent (MARS)', 'Agent', 'The agent on the protected server reads the Azure Backup job history (Get-OBJob). Nothing to set up.'],
        ['Azure Backup Server (MABS) / System Center DPM', 'Agent', 'The agent on the MABS or DPM server reads the jobs of the last three days (Get-DPMJob) per protection group and data source.'],
        ['Synology (Hyper Backup, Active Backup for Business)', 'Syslog or mail', 'In Log Center (or Active Backup › Settings › Log forwarding) send logs to the site controller\'s address, UDP 514. The NAS must be an asset of that site. Or list its notification sender under Report mails.'],
        ['QNAP (Hybrid Backup Sync)', 'Syslog or mail', 'Notification Center › System logs › send to a syslog server (the site controller, UDP 514), with the NAS as an asset of that site. Or mail notifications from a listed sender.'],
        ['Acronis, Macrium, Commvault and others', 'Mail or report address', 'Point the product\'s email notifications at the Service Desk mailbox and list the sender under Report mails, or call the report address from a post-job script.'],
        ['Microsoft Azure Backup (Recovery Services vault alerts)', 'Mail', 'Send the vault\'s backup alerts (Azure Monitor action group email) to the Service Desk mailbox and list azure-noreply@microsoft.com under Report mails with the customer.'],
    ];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "How to connect each tool" }), _jsx("p", { className: "muted", children: "DeskAsk only reads results; it never starts, stops or changes a backup. A new job appears with its first result and is expected daily; change that on the job if it runs weekly or more often." }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Software" }), _jsx("th", { children: "Route" }), _jsx("th", { children: "What to do" })] }) }), _jsx("tbody", { children: rows.map(([a, b, c]) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("b", { children: a }) }), _jsx("td", { children: b }), _jsx("td", { children: c })] }, a)) })] })] });
}
