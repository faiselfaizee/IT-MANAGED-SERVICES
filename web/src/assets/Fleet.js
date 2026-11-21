import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { Act, Badge, Card, Copy, Empty, Err, Field, Modal, ago, fmtDate, useSubmit } from '../ui';
import { MODES } from './Hardware';
import { CountUp } from '../Rings';
/** Agents (installed on devices) and site collectors (inside a customer's network). */
export default function Fleet() {
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Agents & collectors" }), _jsx(Agents, {}), _jsx(Collectors, {})] });
}
function Agents() {
    const tokens = useLoad('/api/assets/enrolments');
    const dash = useLoad('/api/assets/dashboard');
    const bin = useLoad('/api/assets/recycle');
    const [add, setAdd] = useState(false);
    const [show, setShow] = useState(null);
    const tl = Array.isArray(tokens.data) ? tokens.data : [];
    const m = (dash.data?.all ?? {});
    const n = (v) => Number(v ?? 0);
    return _jsxs(_Fragment, { children: [_jsx("h2", { children: "Agents" }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setAdd(true), children: "New enrolment token" }), _jsx("span", { className: "muted", children: "Run the install command on a computer to put an agent on it. It reports hardware and software and allows remote work." })] }), dash.data && _jsx("div", { className: "agent-counts", children: [['Live', n(m.agentOnline), 'online', 'ok'], ['Live, update due', n(m.agentUpdate), 'update', 'warn'], ['Not reporting', n(m.agentOffline), 'offline', 'bad'], ['No agent', n(m.agentNone), 'none', 'none']].map(([l, v, f, tone]) => _jsxs("a", { className: "co-tile agent-count", href: `#/assets/list?agent=${f}`, children: [_jsxs("span", { className: "muted", children: [_jsx("i", { className: `dot tone-${tone}`, "aria-hidden": "true" }), " ", l] }), _jsx("b", { children: _jsx(CountUp, { value: v }) }), _jsx("span", { className: "open", children: "Show these assets" })] }, f)) }), _jsx("p", { className: "muted", children: "Each agent is shown with its asset: open a count to see the computers, then choose them to update, move to the recycle bin or uninstall their agents together." }), _jsx("h2", { children: "Enrolment tokens" }), tl.length === 0 ? _jsx(Empty, { children: "No tokens." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Label" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Token" }), _jsx("th", { className: "num", children: "Used" }), _jsx("th", { children: "Expires" }), _jsx("th", {})] }) }), _jsx("tbody", { children: tl.map((t) => _jsxs("tr", { children: [_jsxs("td", { children: [t.label, t.monitorOnly && _jsxs(_Fragment, { children: [" ", _jsx(Badge, { children: "monitor only" })] })] }), _jsx("td", { children: t.company || '(own)' }), _jsxs("td", { className: "mono", children: [t.prefix, "\u2026"] }), _jsx("td", { className: "num", children: t.uses }), _jsx("td", { children: t.revoked ? _jsx(Badge, { tone: "bad", children: "revoked" }) : t.expires ? fmtDate(t.expires) : 'never' }), _jsxs("td", { children: [!t.revoked && _jsxs(_Fragment, { children: [_jsx("button", { onClick: async () => setShow(await api(`/api/assets/enrolments/${t.id}/scripts`)), children: "Install commands" }), " ", _jsx(Act, { label: "Revoke", danger: true, run: async () => { await api(`/api/assets/enrolments/${t.id}`, { method: 'DELETE' }); tokens.reload(); } }), " "] }), _jsx(Act, { label: "Delete", confirm: `Move the token "${t.label || t.prefix}" to the recycle bin? It stops working at once; you can restore it for 30 days.`, run: async () => { await api(`/api/assets/enrolments/${t.id}/bin`, { method: 'POST', body: {} }); tokens.reload(); bin.reload(); } })] })] }, t.id)) })] }), _jsx(RecycleBin, { bin: bin, onRestore: () => { dash.reload(); tokens.reload(); } }), add && _jsx(TokenForm, { path: "/api/assets/enrolments", title: "New enrolment token", monitor: true, onClose: (r) => { setAdd(false); if (r) {
                    setShow(r);
                    tokens.reload();
                } } }), show && _jsxs(Modal, { title: "Install commands", onClose: () => setShow(null), children: [_jsx(Commands, { c: show.commands }), _jsx("div", { className: "actions", children: _jsx("button", { onClick: () => setShow(null), children: "Close" }) })] })] });
}
function Commands({ c }) {
    return _jsxs("div", { children: [_jsx("p", { children: "Windows (elevated PowerShell):" }), _jsx(Copy, { text: c.windows }), _jsx("p", { children: "Linux (as root):" }), _jsx(Copy, { text: c.linux }), c.upgradeWindows && _jsxs(_Fragment, { children: [_jsx("p", { className: "muted", children: "Upgrade an existing install:" }), _jsx(Copy, { text: c.upgradeWindows }), _jsx(Copy, { text: c.upgradeLinux })] })] });
}
function TokenForm({ path, title, monitor, onClose }) {
    const companies = useLoad('/api/assets/companies');
    const [co, setCo] = useState('');
    const [label, setLabel] = useState('');
    const [days, setDays] = useState(14);
    const [mo, setMo] = useState(false);
    const f = useSubmit(() => undefined);
    const [err, setErr] = useState('');
    return _jsx(Modal, { title: title, onClose: () => onClose(), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Customer", children: _jsxs("select", { value: co, onChange: (e) => setCo(e.target.value), children: [_jsx("option", { value: "", children: "(own organisation)" }), (Array.isArray(companies.data) ? companies.data : []).map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Label", children: _jsx("input", { value: label, onChange: (e) => setLabel(e.target.value) }) }), _jsx(Field, { label: "Valid for (days, 0 = no end)", children: _jsx("input", { type: "number", value: days, onChange: (e) => setDays(Number(e.target.value)) }) })] }), monitor && _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: mo, onChange: (e) => setMo(e.target.checked) }), " Monitor only (no remote commands, shell or remote control)"] }), _jsx(Err, { text: err || f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(), children: "Cancel" }), _jsx("button", { className: "primary", onClick: async () => { setErr(''); try {
                                onClose(await api(path, { body: { companyId: co ? Number(co) : undefined, label, days, monitorOnly: monitor ? mo : undefined } }));
                            }
                            catch (e) {
                                setErr(e.message);
                            } }, children: "Create" })] })] }) });
}
function Collectors() {
    const { data, reload } = useLoad('/api/assets/collectors');
    const [add, setAdd] = useState(false);
    const [show, setShow] = useState(null);
    const [open, setOpen] = useState(null);
    const list = (data?.items ?? []);
    return _jsxs(_Fragment, { children: [_jsx("h2", { children: "Site collectors" }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setAdd(true), children: "New collector token" }), _jsx("span", { className: "muted", children: "A collector is a small service inside a customer's network. It finds devices, checks that they are up, runs service checks and reads details for devices that cannot have an agent." })] }), list.length === 0 ? _jsx(Empty, { children: "No collectors are installed yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Collector" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Status" }), _jsx("th", { className: "num", children: "Devices" }), _jsx("th", { className: "num", children: "Found" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((c) => _jsxs("tr", { children: [_jsxs("td", { children: [c.name, _jsxs("div", { className: "muted", children: [c.hostname, " ", c.version] })] }), _jsx("td", { children: c.company || '(own)' }), _jsxs("td", { children: [_jsx(Badge, { tone: c.online ? 'ok' : 'bad', children: c.online ? 'online' : 'offline' }), " ", ago(c.lastSeen)] }), _jsx("td", { className: "num", children: c.devices }), _jsx("td", { className: "num", children: c.found }), _jsx("td", { children: _jsx("button", { onClick: () => setOpen(c), children: "Manage" }) })] }, c.id)) })] }), add && _jsx(TokenForm, { path: "/api/assets/collectors/tokens", title: "New collector token", onClose: (r) => { setAdd(false); if (r)
                    setShow(r); } }), show && _jsxs(Modal, { title: "Collector install commands", onClose: () => setShow(null), children: [_jsx(Commands, { c: show.commands }), _jsx("div", { className: "actions", children: _jsx("button", { onClick: () => setShow(null), children: "Close" }) })] }), open && _jsx(Manage, { c: open, onClose: () => { setOpen(null); reload(); } })] });
}
function Manage({ c, onClose }) {
    const [cidrs, setCidrs] = useState(c.scanCidrs ?? '');
    const [mode, setMode] = useState(c.monitorDefault ?? 'ping');
    const [name, setName] = useState(c.name);
    const [auto, setAuto] = useState(!!c.autoInstall);
    const [err, setErr] = useState('');
    const found = useLoad(`/api/assets/collectors/${c.id}/found`);
    const log = useLoad(`/api/assets/collectors/${c.id}/log`);
    const [pick, setPick] = useState(new Set());
    const [res, setRes] = useState('');
    useInterval(() => { found.reload(); log.reload(); }, 8000);
    const items = (found.data?.items ?? []);
    return _jsx(Modal, { title: `Collector ${c.name}`, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: name, onChange: (e) => setName(e.target.value) }) }), _jsx(Field, { label: "Ranges to scan (comma separated, each up to a /22)", children: _jsx("input", { value: cidrs, onChange: (e) => setCidrs(e.target.value), placeholder: "192.168.1.0/24, 10.0.5.0/24" }) }), _jsx(Field, { label: "Watch new devices by", children: _jsx("select", { value: mode, onChange: (e) => setMode(e.target.value), children: MODES.filter(([k]) => k).map(([k, l]) => _jsx("option", { value: k, children: l }, k)) }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: auto, onChange: (e) => setAuto(e.target.checked) }), " Install the agent automatically on Windows and Linux computers this controller finds"] }), _jsx("p", { className: "muted", style: { marginTop: 0 }, children: "Each scan adds new computers as assets and installs the agent with the saved login for this customer (Assets > Credentials). Printers, cameras and network devices are left alone. Without a saved login nothing is installed; the activity list says so." }), _jsx(Err, { text: err }), _jsxs("div", { className: "tools", children: [_jsx(Act, { label: "Save settings", run: async () => { setErr(''); try {
                                await api(`/api/assets/collectors/${c.id}`, { method: 'PATCH', body: { name, scanCidrs: cidrs, monitorDefault: mode, autoInstall: auto } });
                            }
                            catch (e) {
                                setErr(e.message);
                                throw e;
                            } } }), _jsx(Act, { label: "Scan now", primary: true, run: async () => { await api(`/api/assets/collectors/${c.id}/scan`, { body: {} }); log.reload(); } }), _jsx(Act, { label: "Install agents on all devices", run: async () => { const r = await api(`/api/assets/collectors/${c.id}/agent-install-all`, { body: {} }); setRes(`${r.queued} devices queued.`); } }), _jsx(Act, { label: "Move to recycle bin", danger: true, confirm: "Move this site controller to the recycle bin? It stops working at once and its devices are no longer watched. You can restore it, with its devices, for 30 days.", run: async () => { await api(`/api/assets/collectors/${c.id}`, { method: 'DELETE' }); onClose(); } })] }), res && _jsx("p", { className: "muted", children: res }), _jsxs("h3", { children: ["Found by scans (", items.length, ")"] }), items.length === 0 ? _jsx(Empty, { children: "Nothing found yet. Set ranges and choose Scan now; the collector answers within a minute." }) : _jsxs(_Fragment, { children: [_jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", {}), _jsx("th", { children: "Address" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Looks like" })] }) }), _jsx("tbody", { children: items.map((h) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("input", { type: "checkbox", disabled: h.known, checked: pick.has(h.ip), onChange: () => setPick((s) => { const n = new Set(s); if (n.has(h.ip))
                                                        n.delete(h.ip);
                                                    else
                                                        n.add(h.ip); return n; }), "aria-label": 'Select ' + h.ip }) }), _jsx("td", { className: "mono", children: h.ip }), _jsx("td", { children: h.hostname }), _jsxs("td", { children: [[h.cat, h.sub].filter(Boolean).join(' / '), " ", h.known && (h.knownAs ? _jsx("a", { href: `#/assets/${h.knownAs.id}`, children: _jsx(Badge, { tone: "ok", children: `${h.knownAs.name} (same ${h.knownAs.why})` }) }) : _jsx(Badge, { tone: "ok", children: "in assets" }))] })] }, h.ip)) })] }), _jsx("div", { className: "tools", children: _jsx(Act, { label: `Add ${pick.size} as assets`, primary: true, disabled: pick.size === 0, run: async () => { const r = await api(`/api/assets/collectors/${c.id}/adopt`, { body: { ips: [...pick] } }); setRes(`${r.added} added, ${r.skipped} skipped${r.limited ? `, ${r.limited} over the licence limit` : ''}.`); setPick(new Set()); found.reload(); } }) })] }), _jsx("h3", { children: "Activity" }), _jsx(Card, { children: log.data && (log.data.items.length === 0 ? _jsx(Empty, { children: "No activity yet." }) : _jsx("ul", { style: { margin: 0, paddingLeft: 18, maxHeight: 180, overflow: 'auto' }, children: log.data.items.map((l) => _jsxs("li", { children: [_jsx("span", { className: "muted mono", children: fmtDate(l.at) }), " ", l.level !== 'info' && _jsx(Badge, { tone: l.level === 'error' ? 'bad' : 'warn', children: l.level }), " ", l.text] }, l.id)) })) }), _jsx("div", { className: "actions", children: _jsx("button", { onClick: onClose, children: "Close" }) })] }) });
}
function RecycleBin({ bin, onRestore }) {
    const ag = (bin.data?.agents ?? []);
    const tk = (bin.data?.tokens ?? []);
    const cl = (bin.data?.collectors ?? []);
    if (ag.length + tk.length + cl.length === 0)
        return null;
    const row = (kind, id, what, when) => _jsxs("tr", { children: [_jsx("td", { children: what }), _jsx("td", { children: kind === 'agent' ? 'Agent' : kind === 'collector' ? 'Site controller' : 'Enrolment token' }), _jsx("td", { children: fmtDate(when) }), _jsxs("td", { children: [_jsx(Act, { label: "Restore", run: async () => { await api(`/api/assets/recycle/${kind}/${id}/restore`, { method: 'POST', body: {} }); bin.reload(); onRestore(); } }), " ", _jsx(Act, { label: "Delete for good", danger: true, confirm: "Delete this for good? It cannot be restored.", run: async () => { await api(`/api/assets/recycle/${kind}/${id}`, { method: 'DELETE' }); bin.reload(); } })] })] }, kind + id);
    return _jsxs(_Fragment, { children: [_jsx("h2", { children: "Recycle bin" }), _jsx("p", { className: "muted", children: "Removed items are kept for 30 days, then deleted for good. A restored agent starts reporting again if its software is still on the computer; a restored site controller gets back the devices it watched." }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Item" }), _jsx("th", { children: "Kind" }), _jsx("th", { children: "Removed" }), _jsx("th", {})] }) }), _jsxs("tbody", { children: [ag.map((a) => row('agent', a.id, a.hostname || a.id, a.deletedAt)), tk.map((t) => row('token', t.id, `${t.label || 'Token'} (${t.prefix}…)`, t.deletedAt)), cl.map((c) => row('collector', c.id, `${c.name}${c.company ? ` (${c.company})` : ''}, watched ${c.devices} devices`, c.deletedAt))] })] })] });
}
