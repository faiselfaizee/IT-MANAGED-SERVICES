import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useRef, useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { Act, Badge, Card, Copy, Empty, Err, Field, Loading, Modal, ago, fmtDate, useSubmit } from '../ui';
import { LiveLog } from './LiveLog';
const RISK = { safe: 'ok', caution: 'warn', disruptive: 'bad' };
const DONE = ['done', 'failed', 'timeout', 'cancelled'];
/** Why remote work is not possible right now, or '' when it is. */
function remoteBlock(d) {
    if (!d.agent.id)
        return 'This device has no agent. Install the agent to run commands, open a shell or take remote control.';
    if (!d.remote.companyAllows)
        return 'Remote access is switched off for this customer (Companies).';
    if (!d.remote.assetAllows)
        return 'Remote access is switched off for this device (Overview).';
    if (!d.agent.online)
        return 'The agent is offline. Wait until it reports in.';
    return '';
}
const Blocked = ({ d }) => { const b = remoteBlock(d); return b ? _jsx("p", { className: "note", children: b }) : null; };
// ---------------------------------------------------------------- service checks
const emptyCheck = { name: '', type: 'tcp', host: '', port: 443, url: '', expect: '', status: '', insecure: false, dns: '', timeoutS: 10, enabled: true };
export function Checks({ id }) {
    const { data, error, loading, reload } = useLoad(`/api/assets/${id}/checks`);
    const [edit, setEdit] = useState(null);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setEdit({ ...emptyCheck }), children: "Add check" }), data && !data.collector && _jsx("span", { className: "muted", children: "Checks run from a site collector. Choose one under Collectors and set this device to be monitored." })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (data.items.length === 0 ? _jsx(Empty, { children: "No service checks yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Kind" }), _jsx("th", { children: "Target" }), _jsx("th", { children: "Result" }), _jsx("th", { children: "When" }), _jsx("th", {})] }) }), _jsx("tbody", { children: data.items.map((c) => _jsxs("tr", { children: [_jsxs("td", { children: [c.name, !c.enabled && _jsx(Badge, { children: "off" })] }), _jsx("td", { children: c.type }), _jsx("td", { className: "mono", children: c.type === 'http' ? c.url : c.type === 'dns' ? c.host : (c.host || 'device') + ':' + c.port }), _jsx("td", { children: c.lastOk === null ? _jsx("span", { className: "muted", children: "not run yet" }) : _jsxs(_Fragment, { children: [_jsx(Badge, { tone: c.lastOk ? 'ok' : 'bad', children: c.lastOk ? 'OK' : 'Failed' }), " ", c.lastMs ? Math.round(c.lastMs) + ' ms' : '', c.lastDays !== null ? ` · certificate ${Math.round(c.lastDays)} days` : '', _jsx("div", { className: "muted", children: c.lastDetail })] }) }), _jsx("td", { children: c.lastAt ? ago(c.lastAt) : '' }), _jsxs("td", { children: [_jsx("button", { onClick: () => setEdit(c), children: "Edit" }), " ", _jsx(Act, { label: "Delete", danger: true, confirm: `Delete the check "${c.name}"?`, run: async () => { await api(`/api/assets/checks/${c.id}`, { method: 'DELETE' }); reload(); } })] })] }, c.id)) })] })), edit && _jsx(CheckForm, { id: id, init: edit, onClose: (ch) => { setEdit(null); if (ch)
                    reload(); } })] });
}
function CheckForm({ id, init, onClose }) {
    const [c, setC] = useState(init);
    const f = useSubmit(() => onClose(true));
    const set = (k, v) => setC({ ...c, [k]: v });
    return _jsx(Modal, { title: init.id ? 'Edit check' : 'New service check', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: c.name, onChange: (e) => set('name', e.target.value), placeholder: "Web site, RDP\u2026" }) }), _jsx(Field, { label: "Kind", children: _jsxs("select", { value: c.type, onChange: (e) => set('type', e.target.value), children: [_jsx("option", { value: "tcp", children: "Port open (TCP)" }), _jsx("option", { value: "http", children: "Web page (HTTP)" }), _jsx("option", { value: "ssl", children: "Certificate expiry" }), _jsx("option", { value: "dns", children: "DNS answer" })] }) })] }), (c.type === 'tcp' || c.type === 'ssl') && _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Port", children: _jsx("input", { type: "number", value: c.port, onChange: (e) => set('port', Number(e.target.value)) }) }), _jsx(Field, { label: "Host (empty = this device)", children: _jsx("input", { value: c.host, onChange: (e) => set('host', e.target.value) }) })] }), c.type === 'http' && _jsxs(_Fragment, { children: [_jsx(Field, { label: "Web address", children: _jsx("input", { value: c.url, onChange: (e) => set('url', e.target.value), placeholder: "https://\u2026" }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Expected status (optional)", children: _jsx("input", { value: c.status, onChange: (e) => set('status', e.target.value), placeholder: "200" }) }), _jsx(Field, { label: "Page must contain (optional)", children: _jsx("input", { value: c.expect, onChange: (e) => set('expect', e.target.value) }) })] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: c.insecure, onChange: (e) => set('insecure', e.target.checked) }), " Accept an untrusted certificate"] })] }), c.type === 'dns' && _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Name to look up", children: _jsx("input", { value: c.host, onChange: (e) => set('host', e.target.value) }) }), _jsx(Field, { label: "DNS server (optional)", children: _jsx("input", { value: c.dns, onChange: (e) => set('dns', e.target.value) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Timeout (seconds)", children: _jsx("input", { type: "number", value: c.timeoutS, onChange: (e) => set('timeoutS', Number(e.target.value)) }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: c.enabled, onChange: (e) => set('enabled', e.target.checked) }), " Enabled"] })] }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api(init.id ? `/api/assets/${id}/checks/${init.id}` : `/api/assets/${id}/checks`, { method: init.id ? 'PUT' : 'POST', body: c })), children: "Save" })] })] }) });
}
// ---------------------------------------------------------------- tickets (real Service Desk tickets; old links when it is not licensed)
export function Tickets({ id, d }) {
    const sd = useLoad(`/api/servicedesk/tickets?assetId=${id}&take=100`);
    const [open, setOpen] = useState(false);
    if (sd.error && !sd.data)
        return _jsx(LegacyTickets, { id: id });
    const items = (sd.data?.items ?? []);
    const active = items.filter((t) => !['resolved', 'closed'].includes(t.status)).length;
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setOpen(true), children: "Raise ticket" }), sd.data && _jsxs("span", { className: "muted", children: [active, " open of ", sd.data.total] })] }), _jsx(Loading, { loading: sd.loading && !sd.data, error: "" }), sd.data && (items.length === 0 ? _jsx(Empty, { children: "No tickets for this asset." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Ticket" }), _jsx("th", { children: "Subject" }), _jsx("th", { children: "Priority" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Raised" })] }) }), _jsx("tbody", { children: items.map((t) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: _jsx("a", { href: `#/servicedesk/${t.id}`, children: t.number }) }), _jsxs("td", { children: [t.subject, _jsx("div", { className: "muted", children: t.assignee ? `Assigned to ${t.assignee}` : 'Not assigned' })] }), _jsx("td", { children: t.priority }), _jsx("td", { children: String(t.status).replace('_', ' ') }), _jsxs("td", { children: [fmtDate(t.createdAt), " \u00B7 ", t.requester] })] }, t.id)) })] })), open && _jsx(TicketForm, { id: id, d: d, onClose: (ch) => { setOpen(false); if (ch)
                    sd.reload(); } })] });
}
function TicketForm({ id, d, onClose }) {
    const [subject, setSubject] = useState('');
    const [pri, setPri] = useState('medium');
    const [desc, setDesc] = useState('');
    const f = useSubmit(() => onClose(true));
    const companyId = d?.companyId ?? null;
    return _jsx(Modal, { title: "Raise a ticket for this asset", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Subject", children: _jsx("input", { value: subject, onChange: (e) => setSubject(e.target.value), autoFocus: true }) }), _jsx(Field, { label: "Priority", children: _jsx("select", { value: pri, onChange: (e) => setPri(e.target.value), children: ['low', 'medium', 'high', 'critical'].map((p) => _jsx("option", { children: p }, p)) }) }), _jsx(Field, { label: "Details", children: _jsx("textarea", { rows: 3, value: desc, onChange: (e) => setDesc(e.target.value) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !subject.trim(), onClick: () => f.submit(() => api('/api/servicedesk/tickets', { body: { subject, priority: pri, description: desc, assetId: Number(id), ...(companyId ? { companyId } : {}) } })), children: "Raise" })] })] }) });
}
// ---------------------------------------------------------------- old links (kept while the Service Desk is not licensed)
function LegacyTickets({ id }) {
    const { data, error, loading, reload } = useLoad(`/api/assets/${id}/tickets`);
    const [open, setOpen] = useState(false);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setOpen(true), children: "Raise ticket" }), data && _jsxs("span", { className: "muted", children: [data.openCount, " open"] })] }), _jsx("p", { className: "note", children: "The Service Desk is not part of this licence. Tickets raised here are kept as links on the asset." }), _jsx(Loading, { loading: loading && !data, error: error }), data && (data.items.length === 0 ? _jsx(Empty, { children: "No tickets for this asset." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Ticket" }), _jsx("th", { children: "Title" }), _jsx("th", { children: "Priority" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Raised" })] }) }), _jsx("tbody", { children: data.items.map((t) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: t.ref }), _jsxs("td", { children: [t.title, _jsx("div", { className: "muted", children: t.note })] }), _jsx("td", { children: t.priority }), _jsx("td", { children: _jsx("select", { value: t.status, onChange: async (e) => { await api(`/api/assets/tickets/${t.id}`, { method: 'PATCH', body: { status: e.target.value } }); reload(); }, children: ['open', 'in_progress', 'resolved', 'closed'].map((s) => _jsx("option", { value: s, children: s.replace('_', ' ') }, s)) }) }), _jsxs("td", { children: [fmtDate(t.createdAt), " \u00B7 ", t.createdBy] })] }, t.id)) })] })), open && _jsx(LegacyForm, { id: id, onClose: (ch) => { setOpen(false); if (ch)
                    reload(); } })] });
}
function LegacyForm({ id, onClose }) {
    const [title, setTitle] = useState('');
    const [pri, setPri] = useState('normal');
    const [note, setNote] = useState('');
    const f = useSubmit(() => onClose(true));
    return _jsx(Modal, { title: "Raise a ticket", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Title", children: _jsx("input", { value: title, onChange: (e) => setTitle(e.target.value), autoFocus: true }) }), _jsx(Field, { label: "Priority", children: _jsx("select", { value: pri, onChange: (e) => setPri(e.target.value), children: ['low', 'normal', 'high', 'urgent'].map((p) => _jsx("option", { children: p }, p)) }) }), _jsx(Field, { label: "Details", children: _jsx("textarea", { rows: 3, value: note, onChange: (e) => setNote(e.target.value) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !title.trim(), onClick: () => f.submit(() => api(`/api/assets/${id}/tickets`, { body: { title, priority: pri, note } })), children: "Raise" })] })] }) });
}
// ---------------------------------------------------------------- troubleshooting commands
export function Commands({ id, d }) {
    const os = d.system.osFamily === 'linux' ? 'linux' : 'windows';
    const [q, setQ] = useState('');
    const [cat, setCat] = useState('');
    const [pick, setPick] = useState(null);
    const [job, setJob] = useState(null);
    const { data, error, loading } = useLoad(`/api/assets/commands?q=${encodeURIComponent(q)}`);
    const jobs = useLoad(`/api/assets/${id}/jobs`);
    const mine = (data?.items ?? []).filter((c) => c.enabled && (c.os === 'any' || c.os === os));
    const items = mine.filter((c) => !cat || c.category === cat);
    const cats = [...new Set(mine.map((c) => c.category))].sort((x, y) => x.localeCompare(y));
    const count = (k) => mine.filter((c) => c.category === k).length;
    return _jsxs(_Fragment, { children: [_jsx(Blocked, { d: d }), _jsxs("div", { className: "cmdbox", children: [_jsxs("aside", { className: "cmdcats", "aria-label": "Command categories", children: [_jsx("input", { placeholder: "Search all commands", value: q, onChange: (e) => { setQ(e.target.value); setCat(''); }, "aria-label": "Search commands" }), _jsxs("button", { className: cat === '' ? 'on' : '', onClick: () => setCat(''), children: ["All ", _jsx("span", { children: mine.length })] }), cats.map((k) => _jsxs("button", { className: cat === k ? 'on' : '', onClick: () => setCat(k), children: [k, " ", _jsx("span", { children: count(k) })] }, k))] }), _jsxs("div", { className: "cmdlist", children: [_jsx(Loading, { loading: loading && !data, error: error }), _jsxs("p", { className: "muted small", children: [items.length, " command", items.length === 1 ? '' : 's', " for ", os === 'linux' ? 'Linux' : 'Windows', cat ? ` in ${cat}` : ''] }), data && items.length === 0 && _jsx(Empty, { children: "No commands here." }), _jsx("div", { className: "cmdcards", children: items.map((c) => _jsxs("div", { className: "cmdcard", children: [_jsxs("div", { className: "cmdtop", children: [_jsx("strong", { children: c.title }), _jsx(Badge, { tone: RISK[c.risk], children: c.risk })] }), _jsx("p", { className: "muted small", children: c.description }), _jsxs("div", { className: "cmdfoot", children: [!cat && _jsx("span", { className: "muted small", children: c.category }), _jsx("button", { disabled: !!remoteBlock(d), onClick: () => setPick(c), children: "Run\u2026" })] })] }, c.id)) })] })] }), _jsx("h2", { children: "Recent runs" }), Array.isArray(jobs.data) && (jobs.data.length === 0 ? _jsx(Empty, { children: "Nothing has been run on this device." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Command" }), _jsx("th", { children: "By" }), _jsx("th", { children: "Result" }), _jsx("th", {})] }) }), _jsx("tbody", { children: jobs.data.map((j) => _jsxs("tr", { children: [_jsx("td", { children: fmtDate(j.created) }), _jsx("td", { children: j.title }), _jsx("td", { children: j.by }), _jsx("td", { children: _jsx(Badge, { tone: j.status === 'done' && !j.exit ? 'ok' : DONE.includes(j.status) ? 'bad' : undefined, children: j.status }) }), _jsx("td", { children: _jsx("button", { onClick: () => setJob(j.id), children: "Output" }) })] }, j.id)) })] })), pick && _jsx(RunCommand, { id: id, cmd: pick, onClose: (jid) => { setPick(null); if (jid) {
                    setJob(jid);
                    jobs.reload();
                } } }), job && _jsx(JobView, { jid: job, onClose: () => { setJob(null); jobs.reload(); } })] });
}
function RunCommand({ id, cmd, onClose }) {
    const ps = cmd.params ?? [];
    const [vals, setVals] = useState(Object.fromEntries(ps.map((p) => [p.name, p.default ?? ''])));
    const [ok, setOk] = useState(cmd.risk !== 'disruptive');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    return _jsx(Modal, { title: cmd.title, onClose: () => onClose(), children: _jsxs("div", { className: "form", children: [_jsxs("p", { children: [cmd.description, " ", _jsx(Badge, { tone: RISK[cmd.risk], children: cmd.risk })] }), ps.map((p) => _jsx(Field, { label: p.label || p.name, children: _jsx("input", { value: vals[p.name], onChange: (e) => setVals({ ...vals, [p.name]: e.target.value }) }) }, p.name)), cmd.risk === 'disruptive' && _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: ok, onChange: (e) => setOk(e.target.checked) }), " I understand this can disrupt the user or the service"] }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(), children: "Cancel" }), _jsx("button", { className: "primary", disabled: busy || !ok, onClick: async () => { setBusy(true); setErr(''); try {
                                const r = await api(`/api/assets/${id}/run`, { body: { commandId: cmd.id, params: vals, confirm: ok } });
                                onClose(r.id);
                            }
                            catch (e) {
                                setErr(e.message);
                            }
                            finally {
                                setBusy(false);
                            } }, children: "Run on device" })] })] }) });
}
export function JobView({ jid, onClose }) {
    const { data, reload } = useLoad(`/api/assets/jobs/${jid}`);
    const running = !data || !DONE.includes(data.status);
    useInterval(reload, running ? 2000 : null);
    return _jsxs(Modal, { title: data ? data.title : 'Running…', onClose: onClose, children: [data && _jsxs("p", { children: [_jsx(Badge, { tone: data.status === 'done' && !data.exit ? 'ok' : DONE.includes(data.status) ? 'bad' : undefined, children: data.status }), " ", data.exit !== null && data.exit !== undefined ? `exit code ${data.exit}` : '', " ", _jsx("span", { className: "muted", children: data.shell })] }), _jsx("pre", { className: "term", children: data?.output || (running ? 'Waiting for the agent…' : '(no output)') }), _jsxs("div", { className: "actions", children: [running && data && _jsx(Act, { label: "Cancel run", danger: true, run: async () => { await api(`/api/assets/jobs/${jid}/cancel`, { body: {} }); reload(); } }), _jsx("button", { onClick: onClose, children: "Close" })] })] });
}
// ---------------------------------------------------------------- live elevated shell
export function Shell({ id, d }) {
    const win = d.system.osFamily !== 'linux';
    const shells = win ? ['cmd', 'powershell'] : ['bash'];
    const [shell, setShell] = useState(shells[0]);
    const [sid, setSid] = useState('');
    const [out, setOut] = useState('');
    const [closed, setClosed] = useState(false);
    const [line, setLine] = useState('');
    const [err, setErr] = useState('');
    const seq = useRef(0);
    const box = useRef(null);
    const hist = useRef([]);
    const hi = useRef(-1);
    useEffect(() => {
        if (!sid)
            return;
        let stop = false;
        (async () => {
            while (!stop) {
                try {
                    const r = await api(`/api/assets/shell/${sid}/output?since=${seq.current}&wait=20`);
                    if (stop)
                        break;
                    if (r.chunks.length) {
                        seq.current = r.seq;
                        setOut((o) => (o + r.chunks.map((c) => c.data).join('')).slice(-200000));
                    }
                    if (r.closed) {
                        setClosed(true);
                        break;
                    }
                }
                catch (e) {
                    if (!stop) {
                        setErr(e.message);
                        await new Promise((x) => setTimeout(x, 3000));
                    }
                }
            }
        })();
        return () => { stop = true; };
    }, [sid]);
    useEffect(() => { if (box.current)
        box.current.scrollTop = box.current.scrollHeight; }, [out]);
    const start = async () => { setErr(''); try {
        const r = await api(`/api/assets/${id}/shell`, { body: { shell } });
        seq.current = 0;
        setOut('');
        setClosed(false);
        setSid(r.sid);
    }
    catch (e) {
        setErr(e.message);
    } };
    const send = async () => {
        if (!sid || closed)
            return;
        const t = line;
        setLine('');
        if (t.trim()) {
            hist.current.push(t);
            hi.current = hist.current.length;
        }
        try {
            await api(`/api/assets/shell/${sid}/input`, { body: { data: t + '\n' } });
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(_Fragment, { children: [_jsx(Blocked, { d: d }), _jsx("p", { className: "note", children: "An elevated shell on the device. Everything you type is recorded in the audit log, and the shell closes after 30 minutes without input." }), !sid || closed ? _jsxs("div", { className: "tools", children: [_jsx("select", { value: shell, onChange: (e) => setShell(e.target.value), "aria-label": "Shell", children: shells.map((s) => _jsx("option", { children: s }, s)) }), _jsx("button", { className: "primary", disabled: !!remoteBlock(d), onClick: start, children: closed ? 'Open a new shell' : 'Open shell' }), closed && _jsx("span", { className: "muted", children: "The shell was closed." })] }) : null, sid && _jsxs(_Fragment, { children: [_jsx("pre", { className: "term", ref: box, "aria-label": "Shell output", children: out || 'Waiting for the shell to start…' }), _jsxs("div", { className: "tools", children: [_jsx("input", { style: { flex: 1 }, className: "mono", value: line, disabled: closed, placeholder: "Type a command and press Enter", "aria-label": "Command line", onChange: (e) => setLine(e.target.value), onKeyDown: (e) => { if (e.key === 'Enter')
                                    void send();
                                else if (e.key === 'ArrowUp' && hist.current.length) {
                                    hi.current = Math.max(0, hi.current - 1);
                                    setLine(hist.current[hi.current] ?? '');
                                }
                                else if (e.key === 'ArrowDown') {
                                    hi.current = Math.min(hist.current.length, hi.current + 1);
                                    setLine(hist.current[hi.current] ?? '');
                                } } }), _jsx("button", { disabled: closed, onClick: send, children: "Send" }), _jsx(Act, { label: "Close shell", danger: true, disabled: closed, run: async () => { await api(`/api/assets/shell/${sid}/close`, { body: {} }); setClosed(true); } })] })] }), _jsx(Err, { text: err })] });
}
// ---------------------------------------------------------------- PowerShell / script editor
export function Ise({ id, d }) {
    const win = d.system.osFamily !== 'linux';
    const [shell, setShell] = useState(win ? 'powershell' : 'bash');
    const [body, setBody] = useState(win ? 'Get-ComputerInfo | Select-Object CsName, OsName, OsVersion\n' : 'uname -a\n');
    const [timeout, setTimeoutS] = useState(300);
    const [job, setJob] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    return _jsxs(_Fragment, { children: [_jsx(Blocked, { d: d }), _jsxs("div", { className: "tools", children: [_jsx("select", { value: shell, onChange: (e) => setShell(e.target.value), "aria-label": "Shell", children: (win ? ['powershell', 'cmd'] : ['bash']).map((s) => _jsx("option", { children: s }, s)) }), _jsxs("label", { children: ["Time limit ", _jsx("input", { type: "number", style: { width: 80 }, value: timeout, onChange: (e) => setTimeoutS(Number(e.target.value)) }), " s"] }), _jsx("button", { className: "primary", disabled: busy || !body.trim() || !!remoteBlock(d), onClick: async () => { setBusy(true); setErr(''); try {
                            const r = await api(`/api/assets/${id}/run`, { body: { body, shell, timeout, script: true } });
                            setJob(r.id);
                        }
                        catch (e) {
                            setErr(e.message);
                        }
                        finally {
                            setBusy(false);
                        } }, children: "Run script" })] }), _jsx("textarea", { rows: 16, value: body, onChange: (e) => setBody(e.target.value), spellCheck: false, "aria-label": "Script" }), _jsx(Err, { text: err }), job && _jsx(JobView, { jid: job, onClose: () => setJob(null) })] });
}
// ---------------------------------------------------------------- RustDesk remote control
export function RemoteControl({ id, d }) {
    const { data, error, reload } = useLoad(`/api/assets/${id}/remote-control`);
    const log = useLoad(`/api/assets/${id}/remote-control/log`);
    const [link, setLink] = useState(null);
    const live = data && (data.state === 'pending');
    useInterval(() => { reload(); log.reload(); }, live ? 1500 : null);
    if (!data)
        return _jsx(Loading, { loading: true, error: error });
    const block = remoteBlock(d);
    return _jsxs(_Fragment, { children: [_jsx(Blocked, { d: d }), !data.enabled && _jsx("p", { className: "note", children: "Remote control is not switched on for this installation. An administrator sets the RustDesk servers under Settings." }), data.hasAgent && !data.windows && _jsx("p", { className: "note", children: "Remote control is available for Windows computers only for now." }), _jsxs(Card, { title: "RustDesk", children: [_jsxs("p", { className: "muted", style: { margin: 0 }, children: ["Agent version ", data.agentVersion || 'unknown'] }), _jsxs("p", { children: ["State: ", _jsx(Badge, { tone: data.state === 'ready' ? 'ok' : data.state === 'failed' || data.state === 'stalled' ? 'bad' : undefined, children: data.state || 'not set up' }), " ", data.id && _jsxs("span", { className: "mono", children: ["ID ", data.id] }), " ", data.mode && _jsxs("span", { className: "muted", children: ["(", data.mode, ")"] })] }), data.msg && _jsx("p", { className: "muted", children: data.msg }), _jsxs("div", { className: "tools", children: [_jsx(Act, { label: "Set up unattended access", disabled: !data.enabled || !data.windows || !!block, run: async () => { await api(`/api/assets/${id}/remote-control/setup`, { body: { mode: 'unattended' } }); reload(); log.reload(); } }), _jsx(Act, { label: "Set up attended (user approves)", disabled: !data.enabled || !data.windows || !!block, run: async () => { await api(`/api/assets/${id}/remote-control/setup`, { body: { mode: 'attended' } }); reload(); log.reload(); } }), _jsx(Act, { label: "Connect", primary: true, disabled: data.state !== 'ready' || !!block, run: async () => { setLink(await api(`/api/assets/${id}/remote-control/connect`, { body: { via: 'web' } })); } }), _jsx(Act, { label: "Remove", danger: true, disabled: !data.state || !data.hasAgent, confirm: "Remove remote control from this computer?", run: async () => { await api(`/api/assets/${id}/remote-control/remove`, { body: {} }); reload(); } })] }), link && _jsxs("div", { className: "note", children: [_jsx("p", { children: "Open this link on your computer (RustDesk must be installed there):" }), _jsx("a", { href: link.link, children: "Open RustDesk session" }), link.password && _jsxs("p", { children: ["Password: ", _jsx(Copy, { text: link.password })] }), _jsx("p", { className: "muted", children: "The session is recorded in the audit log." })] })] }), _jsxs(Card, { title: "Set-up progress", children: [log.data && _jsx(LiveLog, { items: log.data.items, running: data.state === 'pending', empty: "No activity yet. Press Set up to start; every step appears here as the agent does it." }), data.state === 'pending' && !(log.data?.items ?? []).some((x) => String(x.text).includes('picked up')) && _jsxs("p", { className: "note", children: ["The agent has not answered yet. It runs version ", data.agentVersion || 'unknown', "; RustDesk set-up needs 2.9.0 or newer. An older agent ignores the request, so nothing starts on the computer. Check the agent version under Agents & collectors and wait for it to update."] }), data.state === 'pending' && _jsx("p", { className: "muted", children: "Waiting for the agent on the computer. It starts within a few seconds when it is online; a download of about 25 MB follows." })] })] });
}
