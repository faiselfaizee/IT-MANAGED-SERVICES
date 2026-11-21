import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import PersonInput from './PersonInput';
import { PurchaseFields, purchaseBody } from './Purchase';
import { useState } from 'react';
import { usePerms } from '../perms';
import { Icon } from '../chrome';
import { api, go, openPage, saveFile, useLoad } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Meter, Modal, ago, fmtBytes, fmtDate, fmtDay, fmtUptime, useSubmit } from '../ui';
import { Checks, Commands, Ise, RemoteControl, Shell, Tickets } from './DetailOps';
import { AgentInstall } from './AgentInstall';
import { AgentUpdate } from './AgentUpdate';
import { DEEP, Hardware, Watch } from './Hardware';
import { Configs } from './Configs';
import { DuplicateNotice, matchesOf } from './Duplicates';
import { AttachedCard, CustomFields } from './Peripherals';
import { ServicesPanel, UninstallForm } from './Services';
import { PrinterPanel } from './Printer';
import { LIFECYCLE_TABS, useAssetsLite } from './lite';
const TABS = [
    ['overview', 'Overview'], ['storage', 'Storage'], ['network', 'Network'], ['checks', 'Service checks'], ['apps', 'Applications'], ['services', 'Services'], ['tickets', 'Tickets'],
    ['logs', 'Logs'], ['commands', 'Commands'], ['shell', 'Shell'], ['ise', 'PowerShell ISE'], ['remote', 'Remote control'],
];
/** The Hardware tab appears for devices a controller reads in depth (VMware today). */
function tabsFor(d) {
    const hw = d.hardware && Object.keys(d.hardware).length > 0;
    const printer = heroKind(d) === 'printer';
    if (!hw && !DEEP.includes(d.watch?.mode) && !d.watch?.bmc) {
        const t = [...TABS];
        if (printer)
            t.splice(1, 0, ['printer', 'Printer']);
        return t;
    }
    const t = [...TABS];
    t.splice(3, 0, ['hardware', 'Hardware']);
    if (d.watch?.mode === 'network')
        t.splice(4, 0, ['config', 'Configuration']);
    if (printer)
        t.splice(1, 0, ['printer', 'Printer']);
    return t;
}
/** The kind of device, for the coloured tile at the top of the page. */
function heroKind(d) {
    const t = `${d.category ?? ''} ${d.type ?? ''} ${d.identity?.model ?? ''}`.toLowerCase();
    const os = String(d.system?.osFamily ?? '').toLowerCase();
    if (/printer/.test(t))
        return 'printer';
    if (d.watch?.mode === 'network' || d.watch?.mode === 'snmp' || /switch|router|firewall|access point|network/.test(t))
        return 'network';
    if (/phone|mobile|tablet/.test(t))
        return 'phone';
    if (os === 'linux')
        return 'linux';
    if (/server|poweredge|proliant|thinksystem|esxi/.test(t) || /server/.test(String(d.system?.os ?? '').toLowerCase()))
        return 'server';
    return 'pc';
}
const HERO_ICON = { server: 'server', linux: 'server', network: 'network', printer: 'printer', phone: 'phone', pc: 'Assets' };
export default function Detail({ id, tab }) {
    const lite = useAssetsLite();
    const { data: d, error, loading, reload } = useLoad(`/api/assets/${id}/device`);
    const perms = usePerms('assets');
    const approval = useLoad('/api/assets/delete-approval');
    const vault = useLoad(`/api/vault/assets/${id}/items`, [id]);
    const att = useLoad(`/api/assets/${id}/attached`, [id]);
    const [modal, setModal] = useState('');
    if (!d)
        return _jsx(Loading, { loading: loading, error: error });
    const online = d.agent.online, hasAgent = !!d.agent.id;
    const quiet = !!att.data?.peripheral && !hasAgent;
    return _jsxs("div", { children: [_jsx("p", { children: _jsx("a", { href: "#/assets", children: "\u2190 All assets" }) }), _jsxs("section", { className: "asset-hero", children: [_jsx("span", { className: 'hero-ico ' + heroKind(d), children: _jsx(Icon, { name: HERO_ICON[heroKind(d)], size: 30 }) }), _jsxs("div", { className: "hero-main", children: [_jsxs("h1", { children: [d.name, " ", _jsx("span", { className: "muted mono", children: d.tag }), " ", hasAgent && _jsx(Badge, { tone: online ? 'ok' : 'bad', children: online ? 'Online' : 'Offline' }), " ", d.ownership.underAmc && _jsx(Badge, { tone: "ok", children: "AMC" }), " ", d.ownership.rented && _jsx(Badge, { children: "Rented" })] }), _jsxs("div", { className: "hero-meta", children: [d.company && _jsxs("span", { className: "for-customers", children: [_jsx("b", { children: "Customer" }), d.company] }), d.identity?.serial && _jsxs("span", { children: [_jsx("b", { children: "Serial" }), d.identity.serial] }), d.system?.ip && _jsxs("span", { children: [_jsx("b", { children: "IP" }), d.system.ip] }), (d.system?.os || d.identity?.model) && _jsxs("span", { children: [_jsx("b", { children: d.system?.os ? 'OS' : 'Model' }), d.system?.os || [d.identity?.make, d.identity?.model].filter(Boolean).join(' ')] }), d.ownership?.assignedTo && _jsxs("span", { children: [_jsx("b", { children: "Holder" }), d.ownership.assignedTo] }), d.ownership?.location && _jsxs("span", { children: [_jsx("b", { children: "Room" }), d.ownership.location] })] })] })] }), _jsxs("div", { className: "tools hero-tools", children: [perms.admin && _jsx("button", { onClick: () => setModal('ownership'), children: "Customer & category" }), perms.admin && _jsx("button", { onClick: () => setModal('handover'), children: d.ownership.assignedTo ? 'Hand over' : 'Allocate' }), _jsx("button", { onClick: () => setModal('qr'), children: "QR label" }), vault.data && _jsxs("a", { className: "button", href: `#/vault?asset=${id}`, children: ["Passwords in the Vault (", (vault.data.items ?? []).length, ")"] }), _jsx(Act, { label: "Export PDF", run: () => saveFile(`/api/assets/${id}/export.pdf`, `${d.tag}.pdf`) }), _jsx(Act, { label: "Export all details (CSV)", run: () => saveFile(`/api/assets/${id}/export.csv`, `${d.tag}-details.csv`) }), perms.modify && _jsx("button", { onClick: () => setModal('edit'), children: "Edit" }), perms.delete ? _jsx(Act, { label: "Delete", danger: true, confirm: `Delete ${d.name} (${d.tag}) for good? Its history goes with it.`, run: async () => { await api(`/api/assets/${id}`, { method: 'DELETE' }); go('assets'); } })
                        : perms.modify && _jsx("button", { className: approval.data?.required ? '' : 'danger', onClick: () => setModal('ask'), children: approval.data?.required ? 'Request deletion' : 'Delete' }), perms.admin && hasAgent && _jsx(Act, { label: "Refresh inventory", run: async () => { await api(`/api/assets/${id}/refresh`, { method: 'POST', body: {} }); reload(); } })] }), _jsx("nav", { className: "sub", "aria-label": "Asset tabs", children: tabsFor(d).filter(([k]) => !(lite && LIFECYCLE_TABS.includes(k)) && (k !== 'services' || hasAgent) && (!quiet || !['checks', 'commands', 'shell', 'ise', 'remote', 'apps', 'logs', 'network', 'storage'].includes(k))).map(([k, label]) => _jsx("a", { href: `#/assets/${id}/${k}`, className: tab === k ? 'on' : '', children: label }, k)) }), tab === 'overview' && _jsxs(_Fragment, { children: [_jsx(Overview, { id: id, d: d, reload: reload, admin: !!perms.admin, quiet: quiet }), _jsx(AttachedCard, { id: id, d: d, data: att.data, reload: att.reload, canEdit: !!perms.modify }), _jsx(Details, { id: id })] }), tab === 'printer' && _jsx(PrinterPanel, { id: id, canAct: !!perms.modify }), tab === 'storage' && _jsx(Storage, { d: d }), tab === 'network' && _jsx(Network, { d: d }), tab === 'hardware' && _jsx(Hardware, { d: d }), tab === 'config' && _jsx(Configs, { id: id }), tab === 'checks' && _jsx(Checks, { id: id }), tab === 'apps' && _jsx(Apps, { id: id, canUninstall: hasAgent && !!perms.modify }), tab === 'services' && _jsx(ServicesPanel, { assetId: id }), tab === 'tickets' && _jsx(Tickets, { id: id, d: d }), tab === 'logs' && _jsx(Logs, { id: id }), tab === 'commands' && _jsx(Commands, { id: id, d: d }), tab === 'shell' && _jsx(Shell, { id: id, d: d }), tab === 'ise' && _jsx(Ise, { id: id, d: d }), tab === 'remote' && _jsx(RemoteControl, { id: id, d: d }), modal === 'ownership' && _jsx(Ownership, { id: id, d: d, onClose: (ch) => { setModal(''); if (ch)
                    reload(); } }), modal === 'handover' && _jsx(Handover, { id: id, d: d, onClose: (ch) => { setModal(''); if (ch)
                    reload(); } }), modal === 'qr' && _jsx(QrDialog, { id: id, tag: d.tag, onClose: () => setModal('') }), modal === 'edit' && _jsx(EditAsset, { id: id, d: d, onClose: (ch) => { setModal(''); if (ch)
                    reload(); } }), modal === 'ask' && _jsx(AskDelete, { id: id, d: d, onClose: () => setModal('') })] });
}
function Overview({ id, d, reload, admin, quiet }) {
    const i = d.identity, s = d.system, o = d.ownership, a = d.agent, rm = d.remote;
    const cov = useLoad(`/api/assets/${id}/coverage`, [d]).data;
    const warrantyLeft = o.warrantyEnd ? Math.ceil((new Date(o.warrantyEnd).getTime() - Date.now()) / 86400000) : null;
    return _jsxs("div", { className: "grid", children: [_jsx(Card, { title: "Identity", children: _jsx(Dl, { rows: [['Make', i.make], ['Model', i.model], ['Serial number', i.serial], ['BIOS', i.bios], ['Processor', [i.cpuMake, i.cpuModel].filter(Boolean).join(' ')], ['Sockets / cores', i.cpus ? `${i.cpus} / ${i.cores}${i.logical ? ` (${i.logical} threads)` : ''}` : ''], ['Memory', i.ramBytes ? fmtBytes(i.ramBytes) : ''], ['Storage', i.storageBytes ? fmtBytes(i.storageBytes) : '']] }) }), !quiet && _jsx(Card, { title: "System", children: _jsx(Dl, { rows: [['Host name', s.hostname], ['DNS name', s.dnsName], ['IP address', s.ip], ['Domain', s.domainType ? `${s.domainType}${s.domain ? ': ' + s.domain : ''}` : ''], ['Operating system', [s.os, s.osVersion].filter(Boolean).join(' ')], ['Antivirus', s.antivirus ? _jsx(Badge, { tone: s.antivirus === 'Protected' ? 'ok' : 'warn', children: s.antivirus }) : ''], ['Last patch', s.lastPatch], ['Uptime', s.uptimeSeconds ? fmtUptime(s.uptimeSeconds) : '']] }) }), _jsx(Card, { title: "Ownership", children: _jsx(Dl, { rows: [['Customer', d.company || '(own)'], ['Category', d.category], ['Status', d.status], ['Assigned to', o.assignedTo], ['Site', o.site ? `${o.site}${o.siteCode ? ` (${o.siteCode})` : ''}` : ''], ['Room or area', o.location],
                        ['Vendor', o.vendor], ['Purchase order', o.poNumber], ['Invoice', o.invoiceNumber], ['Purchased', o.purchaseDate], ['Warranty period', o.warrantyMonths ? `${o.warrantyMonths} months` : ''], ['Warranty ends', o.warrantyEnd ? _jsxs(_Fragment, { children: [fmtDay(o.warrantyEnd), " ", warrantyLeft !== null && _jsx(Badge, { tone: warrantyLeft < 0 ? 'bad' : warrantyLeft < 60 ? 'warn' : 'ok', children: warrantyLeft < 0 ? 'expired' : warrantyLeft + ' days left' })] }) : ''], ['AMC', cov?.amc ? `${cov.amc.title}${cov.amc.vendor ? ' (' + cov.amc.vendor + ')' : ''}` : o.amc || (o.underAmc ? 'Yes' : '')], ['Rental', cov?.rental ? `${cov.rental.title}${cov.rental.vendor ? ' (' + cov.rental.vendor + ')' : ''}` : ''], ['Monitoring', cov ? (cov.monitored ? 'On' : 'Not monitored') : ''], ['Notes', o.notes]] }) }), !quiet && _jsxs(Card, { title: "Agent & remote access", children: [_jsx(Dl, { rows: [['Agent', a.id ? (a.online ? 'Online' : 'Offline') : 'None'], ['Last seen', a.lastSeen ? `${ago(a.lastSeen)} (${fmtDate(a.lastSeen)})` : ''], ['Data from', a.source], ['Inventory at', a.inventoryAt ? fmtDate(a.inventoryAt) : '']] }), _jsx("p", { children: _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: rm.assetAllows, onChange: async (e) => { await api(`/api/assets/${id}/remote`, { body: { on: e.target.checked } }); reload(); } }), " Allow remote commands, shell and remote control on this device"] }) }), !rm.companyAllows && _jsx("p", { className: "note", children: "Remote access is switched off for this customer. Switch it on under Companies." }), !a.id && _jsx(AgentInstall, { id: id, onDone: reload }), a.id && _jsx(AgentUpdate, { id: id, admin: admin, onDone: reload })] }), admin && !quiet && _jsx(Watch, { id: id, d: d, onChange: reload })] });
}
/** The extra details of the asset's kind (IMEI, screen size…), with the values it has. */
function Details({ id }) {
    const full = useLoad(`/api/assets/${id}`, [id]);
    const types = useLoad('/api/assets/types');
    const a = full.data?.asset;
    const t = (Array.isArray(types.data) ? types.data : []).find((x) => x.id === a?.typeId);
    const fields = (t?.fields ?? []).filter((f) => a?.custom?.[f.key] !== undefined && a?.custom?.[f.key] !== '');
    if (!a || fields.length === 0)
        return null;
    return _jsx(Card, { title: `${t?.name} details`, children: _jsx(Dl, { rows: fields.map((f) => [f.label, f.kind === 'bool' ? (a.custom[f.key] ? 'Yes' : 'No') : String(a.custom[f.key])]) }) });
}
function Storage({ d }) {
    const disks = d.storage ?? [];
    if (!disks.length)
        return _jsx(Empty, { children: "No storage details yet. They arrive with the next inventory." });
    return _jsx(_Fragment, { children: disks.map((k, n) => _jsxs(Card, { title: `${k.name || 'Disk ' + (n + 1)}${k.model ? ' · ' + k.model : ''}`, children: [_jsxs("p", { className: "muted", children: [fmtBytes(k.size_bytes), " ", k.type && _jsx(Badge, { children: k.type })] }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Volume" }), _jsx("th", { children: "File system" }), _jsx("th", { className: "num", children: "Size" }), _jsx("th", { className: "num", children: "Free" }), _jsx("th", { style: { width: 160 }, children: "Used" })] }) }), _jsx("tbody", { children: (k.partitions ?? []).map((p, j) => {
                                const used = p.size_bytes ? ((p.size_bytes - (p.free_bytes ?? 0)) / p.size_bytes) * 100 : 0;
                                return _jsxs("tr", { children: [_jsxs("td", { children: [p.mount || p.name, p.label ? ` (${p.label})` : ''] }), _jsx("td", { children: p.fs }), _jsx("td", { className: "num", children: fmtBytes(p.size_bytes) }), _jsx("td", { className: "num", children: p.free_bytes != null ? fmtBytes(p.free_bytes) : '–' }), _jsx("td", { children: p.free_bytes != null && _jsx(Meter, { pct: used }) })] }, j);
                            }) })] })] }, n)) });
}
function Network({ d }) {
    const nics = d.network ?? [];
    if (!nics.length)
        return _jsx(Empty, { children: "No network details yet." });
    return _jsxs(_Fragment, { children: [_jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Adapter" }), _jsx("th", { children: "MAC" }), _jsx("th", { children: "Addresses" }), _jsx("th", { className: "num", children: "Speed" }), _jsx("th", { children: "State" })] }) }), _jsx("tbody", { children: nics.map((n, i) => _jsxs("tr", { children: [_jsxs("td", { children: [n.name, _jsx("div", { className: "muted", children: n.description })] }), _jsx("td", { className: "mono", children: n.mac }), _jsx("td", { className: "mono", children: (n.ips ?? []).join(', ') }), _jsx("td", { className: "num", children: n.speed_mbps ? n.speed_mbps + ' Mbps' : '–' }), _jsx("td", { children: _jsx(Badge, { tone: n.connected ? 'ok' : undefined, children: n.connected ? 'Connected' : 'Down' }) })] }, i)) })] }), (d.connectedIps ?? []).length > 0 && _jsxs("p", { className: "muted", children: ["Connected addresses: ", _jsx("span", { className: "mono", children: d.connectedIps.join(', ') })] })] });
}
function Apps({ id, canUninstall }) {
    const [q, setQ] = useState('');
    const [rm, setRm] = useState(null);
    const [msg, setMsg] = useState(null);
    const [gone, setGone] = useState([]);
    const { data, error, loading } = useLoad(`/api/assets/${id}/applications?q=${encodeURIComponent(q)}`);
    const key = (a) => `${a.name}|${a.version ?? ''}`;
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search installed applications", value: q, onChange: (e) => setQ(e.target.value) }), _jsx("span", { className: "muted", children: data ? `${data.count} applications` : '' })] }), msg && _jsxs("p", { role: "status", className: msg.ok ? 'ok-note' : 'err', children: [msg.text, msg.ok ? ' The list updates when the agent next sends its inventory.' : ''] }), _jsx(Loading, { loading: loading && !data, error: error }), data && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Version" }), _jsx("th", { children: "Publisher" }), _jsx("th", { children: "Installed" }), canUninstall && _jsx("th", { children: _jsx("span", { className: "sr-only", children: "Actions" }) })] }) }), _jsx("tbody", { children: data.items.map((a, i) => _jsxs("tr", { className: gone.includes(key(a)) ? 'gone' : undefined, children: [_jsx("td", { children: a.name }), _jsx("td", { children: a.version }), _jsx("td", { children: a.publisher }), _jsx("td", { children: a.installedOn }), canUninstall && _jsx("td", { children: gone.includes(key(a)) ? _jsx("span", { className: "muted small", children: "Uninstalled" }) : _jsx("button", { type: "button", className: "small-btn", onClick: () => setRm(a), children: "Uninstall" }) })] }, i)) })] }), rm && _jsx(UninstallForm, { assetId: id, app: rm, onClose: () => setRm(null), onDone: (text, ok) => { setMsg({ text, ok }); if (ok)
                    setGone((g) => [...g, key(rm)]); setRm(null); } })] });
}
function Logs({ id }) {
    const [kind, setKind] = useState('');
    const { data, error, loading } = useLoad(`/api/assets/${id}/logs?kind=${kind}`);
    const kinds = data?.kinds ?? [];
    const brief = (t) => { try {
        const o = JSON.parse(t);
        return Object.entries(o).map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`).join(' · ');
    }
    catch {
        return t;
    } };
    return _jsxs(_Fragment, { children: [_jsx("div", { className: "tools", children: _jsxs("select", { value: kind, onChange: (e) => setKind(e.target.value), "aria-label": "Kind", children: [_jsx("option", { value: "", children: "All activity" }), kinds.map((k) => _jsx("option", { children: k }, k))] }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (data.items.length === 0 ? _jsx(Empty, { children: "Nothing recorded yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Kind" }), _jsx("th", { children: "By" }), _jsx("th", { children: "What" })] }) }), _jsx("tbody", { children: data.items.map((l, i) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: fmtDate(l.at) }), _jsx("td", { children: _jsx(Badge, { children: l.kind }) }), _jsx("td", { children: l.by }), _jsx("td", { children: brief(l.text) })] }, i)) })] }))] });
}
function Ownership({ id, d, onClose }) {
    const { data: companies } = useLoad('/api/assets/companies');
    const [co, setCo] = useState(d.companyId ? String(d.companyId) : '');
    const [cat, setCat] = useState(d.category ?? '');
    const f = useSubmit(() => onClose(true));
    const list = (Array.isArray(companies) ? companies : []);
    return _jsx(Modal, { title: "Customer and category", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Customer", children: _jsxs("select", { value: co, onChange: (e) => setCo(e.target.value), children: [_jsx("option", { value: "", children: "(own organisation)" }), list.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsx(Field, { label: "Category", children: _jsx("input", { value: cat, onChange: (e) => setCat(e.target.value), placeholder: "IT hardware, Server, Printers & peripherals\u2026" }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api(`/api/assets/${id}/ownership`, { method: 'PATCH', body: { companyId: co ? Number(co) : null, category: cat } })), children: "Save" })] })] }) });
}
function Handover({ id, d, onClose }) {
    const holder = d.ownership.assignedTo;
    const [to, setTo] = useState('');
    const [cond, setCond] = useState('good');
    const [acc, setAcc] = useState('');
    const [notes, setNotes] = useState('');
    const { data: hist } = useLoad(`/api/assets/${id}/handovers`);
    const f = useSubmit(() => onClose(true));
    const body = { condition: cond, to, accessories: acc.split(',').map((x) => x.trim()).filter(Boolean), notes };
    return _jsx(Modal, { title: holder ? `Hand over from ${holder}` : 'Allocate to someone', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [!holder && _jsx(Field, { label: "Receiving person", children: _jsx(PersonInput, { value: to, onChange: setTo, companyId: d.companyId, autoFocus: true }) }), _jsx(Field, { label: "Condition", children: _jsx("select", { value: cond, onChange: (e) => setCond(e.target.value), children: ['good', 'fair', 'damaged', 'lost'].map((c) => _jsx("option", { children: c }, c)) }) }), _jsx(Field, { label: "Accessories (comma separated)", children: _jsx("input", { value: acc, onChange: (e) => setAcc(e.target.value), placeholder: "charger, bag, mouse" }) }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: notes, onChange: (e) => setNotes(e.target.value) }) }), _jsx(Err, { text: f.err }), hist && hist.items.length > 0 && _jsxs("details", { children: [_jsxs("summary", { children: ["Handover history (", hist.items.length, ")"] }), _jsx("ul", { children: hist.items.map((h) => _jsxs("li", { children: [fmtDate(h.at), " \u00B7 ", h.kind === 'assign' ? 'allocated to' : 'handed back by', " ", _jsx("b", { children: h.holder }), " (", h.condition, ") by ", h.by, h.notes ? ' – ' + h.notes : ''] }, h.id)) })] }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api(`/api/assets/${id}/${holder ? 'handover' : 'allocate'}`, { body })), children: holder ? 'Record handover' : 'Allocate' })] })] }) });
}
export function QrDialog({ id, tag, onClose }) {
    const [content, setContent] = useState('details');
    const { data } = useLoad(`/api/assets/qr/${id}/info?content=${content}`, []);
    return _jsx(Modal, { title: `QR label for ${tag}`, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "What the code holds", children: _jsxs("select", { value: content, onChange: (e) => setContent(e.target.value), children: [_jsx("option", { value: "details", children: "Details (compact)" }), _jsx("option", { value: "full", children: "Full details" }), _jsx("option", { value: "link", children: "Link to the asset" })] }) }), data && _jsx("pre", { className: "term", style: { minHeight: 0 }, children: data.text }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: onClose, children: "Close" }), _jsx("button", { className: "primary", onClick: () => openPage(`/api/assets/qr/print?ids=${id}&content=${content}&layout=label&size=35`), children: "Print label" })] })] }) });
}
const STATUS_CHOICES = ['stock', 'in_use', 'repair', 'retired', 'lost'];
function EditAsset({ id, d, onClose }) {
    const full = useLoad(`/api/assets/${id}`);
    const cov = useLoad(`/api/assets/${id}/coverage`);
    const a = (full.data?.asset ?? null);
    if (!a || !cov.data)
        return _jsx(Modal, { title: `Edit ${d.tag}`, onClose: () => onClose(false), children: _jsx(Loading, { loading: !full.error && !cov.error, error: full.error || cov.error }) });
    return _jsx(EditForm, { id: id, d: d, a: a, cov: cov.data, onClose: onClose });
}
function EditForm({ id, d, a, cov, onClose }) {
    const [v, setV] = useState({ name: a.name ?? '', status: a.status ?? 'in_use', manufacturer: a.manufacturer ?? '', model: a.model ?? '', serial: a.serial ?? '', assignedTo: a.assignedTo ?? '',
        ipAddress: d.system?.ip ?? '', location: a.location ?? '', siteId: a.siteId ?? null, vendorId: a.vendorId ?? null, poNumber: a.poNumber ?? '', invoiceNumber: a.invoiceNumber ?? '',
        purchaseDate: a.purchaseDate ?? '', warrantyMonths: a.warrantyMonths ?? null, warrantyEnd: a.warrantyEnd ?? '',
        hostname: cov.hostname ?? '', macAddress: cov.macAddress ?? '', monitored: cov.monitored !== false, custom: a.custom ?? {}, amcId: cov.amc?.id ?? null, rentalId: cov.rental?.id ?? null });
    const [dup, setDup] = useState(null);
    const f = useSubmit(() => onClose(true));
    const set = (k, x) => setV({ ...v, [k]: x });
    const types = useLoad('/api/assets/types');
    const kind = (Array.isArray(types.data) ? types.data : []).find((x) => x.id === a.typeId);
    const remote = !(kind?.peripheral && !d.agent?.id);
    const save = (confirmDuplicate) => {
        setDup(null);
        return f.submit(async () => {
            try {
                await api(`/api/assets/${id}`, { method: 'PUT', body: { name: v.name, status: v.status, manufacturer: v.manufacturer, model: v.model, serial: v.serial, assignedTo: v.assignedTo, ipAddress: String(v.ipAddress).trim(), ...purchaseBody(v), hostname: String(v.hostname).trim(), macAddress: String(v.macAddress).trim(), monitored: v.monitored, custom: v.custom, confirmDuplicate } });
            }
            catch (e) {
                const m = matchesOf(e);
                if (m) {
                    setDup(m);
                    throw new Error('');
                }
                throw e;
            }
            const body = {};
            if ((v.amcId ?? null) !== (cov.amc?.id ?? null) && cov.canEditAmc)
                body.amcId = v.amcId;
            if ((v.rentalId ?? null) !== (cov.rental?.id ?? null) && cov.canEditRental)
                body.rentalId = v.rentalId;
            if (Object.keys(body).length)
                await api(`/api/assets/${id}/coverage`, { method: 'PUT', body });
        });
    };
    return _jsx(Modal, { title: `Edit ${d.tag}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: v.name, onChange: (e) => set('name', e.target.value), autoFocus: true }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Status", children: _jsx("select", { value: v.status, onChange: (e) => set('status', e.target.value), children: STATUS_CHOICES.map((x) => _jsx("option", { value: x, children: x.replace('_', ' ') }, x)) }) }), _jsx(Field, { label: "Assigned to (name or email)", children: _jsx(PersonInput, { value: v.assignedTo ?? '', onChange: (x) => set('assignedTo', x), companyId: a.companyId }) })] }), remote && _jsxs(_Fragment, { children: [_jsx(Field, { label: "IP address (the address DeskAsk uses to reach it)", children: _jsx("input", { value: v.ipAddress, onChange: (e) => set('ipAddress', e.target.value), placeholder: "10.0.0.25", spellCheck: false }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Host name", children: _jsx("input", { value: v.hostname, onChange: (e) => set('hostname', e.target.value), spellCheck: false }) }), _jsx(Field, { label: "MAC address", children: _jsx("input", { value: v.macAddress, onChange: (e) => set('macAddress', e.target.value), placeholder: "00:1A:2B:3C:4D:5E", spellCheck: false }) })] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: v.monitored, onChange: (e) => setV({ ...v, monitored: e.target.checked }) }), " Monitor this asset (alerts, services, checks)"] })] }), d.agent?.id && _jsx("p", { className: "muted small", children: "This asset has an agent. After you change the IP address or host name the agent is asked to report again, and what it sees replaces what you typed." }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Under AMC", children: _jsxs("select", { value: v.amcId ?? '', disabled: !cov.canEditAmc, onChange: (e) => setV({ ...v, amcId: e.target.value ? Number(e.target.value) : null }), children: [_jsx("option", { value: "", children: "No AMC" }), cov.amcChoices.map((c) => _jsxs("option", { value: c.id, children: [c.title, c.vendor ? ` (${c.vendor})` : '', c.end ? ` to ${c.end}` : ''] }, c.id))] }) }), _jsx(Field, { label: "Under rental", children: _jsxs("select", { value: v.rentalId ?? '', disabled: !cov.canEditRental, onChange: (e) => setV({ ...v, rentalId: e.target.value ? Number(e.target.value) : null }), children: [_jsx("option", { value: "", children: "Not rented" }), cov.rentalChoices.map((c) => _jsxs("option", { value: c.id, children: [c.title, c.vendor ? ` (${c.vendor})` : '', c.end ? ` to ${c.end}` : ''] }, c.id))] }) })] }), _jsx("p", { className: "muted small", children: "Contracts and their vendors are kept under AMC and Rentals; vendors under Vendors." }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Manufacturer", children: _jsx("input", { value: v.manufacturer, onChange: (e) => set('manufacturer', e.target.value) }) }), _jsx(Field, { label: "Model", children: _jsx("input", { value: v.model, onChange: (e) => set('model', e.target.value) }) }), _jsx(Field, { label: "Serial number", children: _jsx("input", { value: v.serial, onChange: (e) => set('serial', e.target.value) }) })] }), _jsx(CustomFields, { fields: (kind?.fields ?? []), value: v.custom, onChange: (c) => setV({ ...v, custom: c }) }), _jsx(PurchaseFields, { v: v, set: (p) => setV({ ...v, ...p }), companyId: a.companyId }), !a.companyId && _jsx("p", { className: "muted small", children: "Set the customer (Ownership) to choose a site." }), dup && _jsx(DuplicateNotice, { info: dup, busy: f.busy, onAnyway: () => void save(true) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !String(v.name).trim(), onClick: () => void save(false), children: "Save" })] })] }) });
}
function AskDelete({ id, d, onClose }) {
    const [reason, setReason] = useState('');
    const [sent, setSent] = useState(false);
    const f = useSubmit(() => setSent(true));
    const approval = useLoad('/api/assets/delete-approval');
    const [typed, setTyped] = useState('');
    if (approval.data && !approval.data.required)
        return _jsx(Modal, { title: `Delete ${d.tag}`, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsxs("p", { children: ["This deletes ", _jsx("strong", { children: d.name }), " (", d.tag, ") for good, with its history. It cannot be undone; the reason is kept in the audit log. To keep the record, set its status to Retired instead."] }), _jsx(Field, { label: "Why?", children: _jsx("textarea", { rows: 2, value: reason, onChange: (e) => setReason(e.target.value), autoFocus: true }) }), _jsx(Field, { label: `Type ${d.tag} to confirm`, children: _jsx("input", { value: typed, onChange: (e) => setTyped(e.target.value) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary danger", disabled: f.busy || !reason.trim() || typed.trim() !== d.tag, onClick: () => f.submit(async () => { await api(`/api/assets/${id}/delete-request`, { body: { reason } }); go('assets'); }), children: "Delete" })] })] }) });
    return _jsx(Modal, { title: `Request deletion of ${d.tag}`, onClose: onClose, children: _jsx("div", { className: "form", children: sent ? _jsxs(_Fragment, { children: [_jsx("p", { children: "Request sent. The asset stays until an approver decides; follow it under Delete requests." }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", onClick: onClose, children: "Close" }) })] }) : _jsxs(_Fragment, { children: [_jsx("p", { className: "muted", children: "You may edit assets but not delete them. An approver (an administrator, or a team lead or manager who was given the delete right) will decide." }), _jsx(Field, { label: "Why should it be deleted?", children: _jsx("textarea", { rows: 3, value: reason, onChange: (e) => setReason(e.target.value), autoFocus: true }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !reason.trim(), onClick: () => f.submit(() => api(`/api/assets/${id}/delete-request`, { body: { reason } })), children: "Send request" })] })] }) }) });
}
