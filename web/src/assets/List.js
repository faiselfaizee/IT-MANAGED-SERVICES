import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, download, go, openPage, saveFile, useLoad } from '../http';
import { usePerms } from '../perms';
import { Act, Badge, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';
import { PurchaseFields, purchaseBody } from './Purchase';
import { DuplicateNotice, matchesOf } from './Duplicates';
import { AddSeveral, CustomFields, MainDevicePicker } from './Peripherals';
const STATUSES = ['stock', 'in_use', 'repair', 'retired', 'lost'];
const label = (s) => (s ?? '').replace('_', ' ');
/** Filters that come from the dashboard (a ring part), with the words shown for them. */
const DRILL = {
    compliance: { compliant: 'Compliant', noncompliant: 'Not compliant', nodata: 'No compliance data' },
    warranty: { covered: 'Warranty covered', soon: 'Warranty ends within 30 days', expired: 'Warranty expired', unknown: 'Warranty not recorded' },
    kind: { computer: 'Computers', server: 'Servers and hypervisors', network: 'Network devices', printer: 'Printers', other: 'Other devices' },
    health: { bad: 'Hardware not OK' },
};
const AGENT_WORDS = { online: 'Agent live', offline: 'Agent not reporting', update: 'Agent update due', none: 'No agent' };
export default function List() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const company = qs.get('company') ?? '';
    const typeId = qs.get('typeId') ?? '';
    const place = qs.get('place') ?? '';
    const vendor = qs.get('vendor') ?? '';
    const placeInfo = useLoad(place ? '/api/assets/places' : null);
    const vendorInfo = useLoad(vendor ? `/api/assets/vendors/${vendor}` : null);
    const placeName = place ? String((placeInfo.data?.items ?? []).find((p) => String(p.id) === place)?.name ?? '…') : '';
    const vendorName = vendor ? String(vendorInfo.data?.vendor?.name ?? '…') : '';
    const drill = Object.keys(DRILL).map((k) => [k, qs.get(k) ?? '']).filter(([k, v]) => v && DRILL[k][v]);
    const perms = usePerms('assets');
    const [search, setSearch] = useState('');
    const [status, setStatus] = useState(qs.get('status') ?? '');
    const [agent, setAgent] = useState(qs.get('agent') ?? '');
    const [skip, setSkip] = useState(0);
    const take = 50;
    const companies = useLoad('/api/assets/companies');
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const types = useLoad(typeId && typeId !== 'none' ? '/api/assets/types' : null);
    const typeName = typeId === 'none' ? 'No kind' : typeId ? String((Array.isArray(types.data) ? types.data : []).find((x) => String(x.id) === typeId)?.name ?? '…') : '';
    const pickCompany = (v) => { const n = new URLSearchParams(qs); if (v)
        n.set('company', v);
    else
        n.delete('company'); const t = n.toString(); location.hash = '#/assets/list' + (t ? '?' + t : ''); setSkip(0); };
    const co = useLoad(company ? `/api/assets/dashboard?company=${encodeURIComponent(company)}` : null);
    const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
    const without = (key) => { const n = new URLSearchParams(qs); n.delete(key); const t = n.toString(); return '#/assets/list' + (t ? '?' + t : ''); };
    const [sel, setSel] = useState(new Set());
    const [add, setAdd] = useState(false);
    const [several, setSeveral] = useState(false);
    const [print, setPrint] = useState(false);
    const filters = `&agent=${agent}` + (typeId ? `&typeId=${typeId}` : '') + (company ? `&company=${encodeURIComponent(company)}` : '') + (place ? `&place=${place}` : '') + (vendor ? `&vendor=${vendor}` : '') + drill.map(([k, v]) => `&${k}=${v}`).join('');
    const exportQs = `search=${encodeURIComponent(search)}&status=${status}${filters}`;
    const q = `/api/assets?search=${encodeURIComponent(search)}&status=${status}${filters}&skip=${skip}&take=${take}`;
    const { data, error, loading, reload } = useLoad(q);
    const items = (data?.items ?? []);
    const total = (data?.total ?? 0);
    const toggle = (id) => setSel((s) => { const n = new Set(s); if (n.has(id))
        n.delete(id);
    else
        n.add(id); return n; });
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/assets", children: "Assets" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), company ? _jsxs(_Fragment, { children: [_jsx("a", { href: `#/assets/customer/${company}`, children: coName }), _jsx("span", { "aria-hidden": "true", children: "\u203A" })] }) : null, _jsx("span", { "aria-current": "page", children: drill.length ? drill.map(([k, v]) => DRILL[k][v]).join(', ') : agent ? AGENT_WORDS[agent] : 'All assets' })] }), _jsx("h1", { children: company ? coName : 'All assets' }), (company || place || vendor || typeId || drill.length > 0) && _jsxs("div", { className: "filter-chips", children: [typeId && _jsxs("a", { href: without('typeId'), title: "Every kind", children: [typeName, " \u2715"] }), company && _jsxs("a", { href: without('company'), title: "Show every customer", children: [coName, " \u2715"] }), place && _jsxs("a", { href: without('place'), title: "Every location", children: ["At ", placeName, " \u2715"] }), vendor && _jsxs("a", { href: without('vendor'), title: "Every vendor", children: ["From ", vendorName, " \u2715"] }), drill.map(([k, v]) => _jsxs("a", { href: without(k), title: "Remove this filter", children: [DRILL[k][v], " \u2715"] }, k))] }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search name, tag, serial, person, site, PO\u2026", value: search, onChange: (e) => { setSearch(e.target.value); setSkip(0); }, style: { minWidth: 260 } }), cos.length > 0 && _jsxs("select", { value: company, onChange: (e) => pickCompany(e.target.value), "aria-label": "Customer", children: [_jsx("option", { value: "", children: "All customers" }), cos.map((c) => _jsx("option", { value: String(c.id), children: c.name }, c.id)), _jsx("option", { value: "none", children: "No customer" })] }), _jsxs("select", { value: status, onChange: (e) => { setStatus(e.target.value); setSkip(0); }, "aria-label": "Status", children: [_jsx("option", { value: "", children: "Any status" }), STATUSES.map((s) => _jsx("option", { value: s, children: label(s) }, s))] }), _jsxs("select", { value: agent, onChange: (e) => { setAgent(e.target.value); setSkip(0); }, "aria-label": "Agent", children: [_jsx("option", { value: "", children: "Any agent state" }), _jsx("option", { value: "online", children: "Agent live, up to date" }), _jsx("option", { value: "offline", children: "Agent offline" }), _jsx("option", { value: "update", children: "Agent update due" }), _jsx("option", { value: "none", children: "No agent" })] }), perms.add && _jsx("button", { className: "primary", onClick: () => setAdd(true), children: "Add asset" }), perms.add && _jsx("button", { onClick: () => setSeveral(true), children: "Add several\u2026" }), _jsxs("button", { disabled: sel.size === 0, onClick: () => setPrint(true), children: ["QR labels (", sel.size, ")"] }), _jsx("button", { onClick: () => download('/api/assets/export.csv?' + exportQs, 'assets.csv'), children: "Export list (CSV)" }), _jsx(Act, { label: "Export all details (CSV)", run: () => saveFile('/api/assets/export-full.csv?' + exportQs, 'assets-full-details.csv') })] }), perms.modify && sel.size > 0 && _jsx(BulkMonitor, { ids: [...sel], onDone: () => undefined }), perms.admin && sel.size > 0 && _jsx(BulkAgents, { ids: [...sel], total: total, query: q, onSelect: (ids) => setSel(new Set(ids)), onDone: () => { setSel(new Set()); reload(); } }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No assets match." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: _jsx("input", { type: "checkbox", "aria-label": "Select all", checked: items.every((i) => sel.has(i.id)), onChange: (e) => setSel(e.target.checked ? new Set([...sel, ...items.map((i) => i.id)]) : new Set([...sel].filter((x) => !items.some((i) => i.id === x)))) }) }), _jsx("th", { children: "Tag" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Type" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "OS" }), _jsx("th", { children: "IP address" }), _jsx("th", { children: "Agent" }), _jsx("th", { children: "Assigned to" }), _jsx("th", { children: "Site" }), _jsx("th", { children: "Warranty" })] }) }), _jsx("tbody", { children: items.map((a) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("input", { type: "checkbox", "aria-label": 'Select ' + a.name, checked: sel.has(a.id), onChange: () => toggle(a.id) }) }), _jsx("td", { className: "mono", children: a.tag }), _jsxs("td", { children: [_jsx("a", { href: `#/assets/${a.id}`, children: a.name }), [a.manufacturer, a.model].filter(Boolean).length ? _jsx("div", { className: "muted small", children: [a.manufacturer, a.model].filter(Boolean).join(' ') }) : null] }), _jsx("td", { children: a.type ?? '' }), _jsx("td", { children: label(a.status) }), _jsx("td", { children: a.os ?? '' }), _jsx("td", { className: "mono", children: a.ip ?? '' }), _jsx("td", { children: _jsx(AgentCell, { a: (a.agent ?? { state: 'none' }) }) }), _jsx("td", { children: a.assignedTo }), _jsx("td", { children: a.site ? _jsxs(_Fragment, { children: [a.site, a.siteCode ? _jsxs("span", { className: "muted mono", children: [" ", a.siteCode] }) : null, _jsx("div", { className: "muted small", children: [a.location, a.state, a.country].filter(Boolean).join(', ') })] }) : a.location }), _jsx("td", { children: fmtDay(a.warrantyEnd) })] }, a.id)) })] })), _jsxs("div", { className: "tools", style: { marginTop: 10 }, children: [_jsx("button", { disabled: skip === 0, onClick: () => setSkip(Math.max(0, skip - take)), children: "Previous" }), _jsx("span", { className: "muted", children: total ? `${skip + 1}–${Math.min(total, skip + take)} of ${total}` : '' }), _jsx("button", { disabled: skip + take >= total, onClick: () => setSkip(skip + take), children: "Next" })] }), several && _jsx(AddSeveral, { onClose: (ch) => { setSeveral(false); if (ch)
                    reload(); } }), add && _jsx(AddAsset, { onClose: (id) => { setAdd(false); if (id)
                    go(`assets/${id}`);
                else
                    reload(); } }), print && _jsx(PrintDialog, { ids: [...sel], onClose: () => setPrint(false) })] });
}
/** Agent actions for the chosen assets: update, move to the recycle bin, uninstall; and choosing every asset that matches the filters. */
/** Choose which of the selected assets monitoring watches. */
function BulkMonitor({ ids, onDone }) {
    const [msg, setMsg] = useState('');
    const run = async (monitored) => { try {
        const r = await api('/api/assets/bulk-monitor', { body: { ids, monitored } });
        setMsg(`${monitored ? 'Monitoring switched on' : 'Monitoring switched off'} for ${r.changed} asset${r.changed === 1 ? '' : 's'}.`);
        onDone();
    }
    catch (e) {
        setMsg(e.message);
    } };
    return _jsxs("div", { className: "tools", children: [_jsx("button", { onClick: () => void run(true), children: "Monitor selected" }), _jsx("button", { onClick: () => void run(false), children: "Stop monitoring selected" }), msg && _jsx("span", { className: "muted", children: msg })] });
}
function BulkAgents({ ids, total, query, onSelect, onDone }) {
    const [msg, setMsg] = useState('');
    const [busy, setBusy] = useState(false);
    const run = async (action) => {
        setBusy(true);
        setMsg('');
        try {
            let done = 0;
            const skipped = {};
            for (let i = 0; i < ids.length; i += 1000) {
                const r = await api('/api/assets/agents/bulk', { body: { action, assetIds: ids.slice(i, i + 1000) } });
                done += Number(r.done ?? 0);
                for (const [k, v] of Object.entries((r.skipped ?? {})))
                    skipped[k] = (skipped[k] ?? 0) + v;
            }
            const why = { no_agent: 'have no agent', up_to_date: 'are up to date', agent_offline: 'are offline', busy: 'are already updating', no_build: 'have no build on the server' };
            const rest = Object.entries(skipped).map(([k, v]) => `${v} ${why[k] ?? k}`).join(', ');
            setMsg(`${action === 'update' ? 'Update asked for' : action === 'bin' ? 'Moved to the recycle bin:' : 'Uninstall sent to'} ${done} agent${done === 1 ? '' : 's'}.${rest ? ' Skipped: ' + rest + '.' : ''}`);
            if (action !== 'update')
                onDone();
        }
        catch (e) {
            setMsg(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const all = async () => {
        setBusy(true);
        try {
            const out = [];
            for (let skip = 0; skip < Math.min(total, 5000); skip += 200) {
                const r = await api(query.replace(/&skip=\d+&take=\d+/, '') + `&skip=${skip}&take=200`);
                out.push(...(r.items ?? []).map((x) => Number(x.id)));
            }
            onSelect(out);
        }
        catch (e) {
            setMsg(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsxs("div", { className: "bulk", role: "region", "aria-label": "Agent actions", children: [_jsxs("b", { children: [ids.length, " chosen"] }), ids.length < total && _jsxs("button", { disabled: busy, onClick: all, children: ["Choose all ", Math.min(total, 5000), " matching"] }), _jsx("button", { disabled: busy, onClick: () => run('update'), children: "Update agents" }), _jsx(Act, { label: "Move agents to bin", confirm: `Move the agents of ${ids.length} assets to the recycle bin? They stop reporting; the assets stay, and you can restore the agents for 30 days.`, run: () => run('bin') }), _jsx(Act, { label: "Uninstall agents", danger: true, confirm: `Uninstall the agent from ${ids.length} computers? The assets stay.`, run: () => run('uninstall') }), msg && _jsx("span", { role: "status", children: msg })] });
}
/** Online, offline or no agent, with the version and a flag when an update is due. */
function AgentCell({ a }) {
    if (a.state === 'none')
        return _jsx("span", { className: "muted", children: "No agent" });
    const on = a.state === 'online';
    return _jsxs("span", { className: "agent-cell", children: [_jsx(Badge, { tone: on ? 'ok' : 'bad', children: on ? 'Online' : 'Offline' }), ' ', _jsx("span", { className: "muted mono", title: a.lastSeen ? `Last seen ${new Date(String(a.lastSeen)).toLocaleString()}` : 'Never seen', children: String(a.version || '') }), a.updateDue && _jsxs(_Fragment, { children: [' ', _jsx(Badge, { tone: "warn", children: "update due" })] })] });
}
function AddAsset({ onClose }) {
    const [v, setV] = useState({ name: '', status: 'in_use', manufacturer: '', model: '', serial: '', location: '', assignedTo: '', companyId: null, ipAddress: '', macAddress: '', hostname: '', monitored: true, custom: {} });
    const types = useLoad('/api/assets/types');
    const [main, setMain] = useState(null);
    const companies = useLoad('/api/assets/companies');
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const f = useSubmit(() => undefined);
    const [err, setErr] = useState('');
    const [dup, setDup] = useState(null);
    const set = (k, x) => setV({ ...v, [k]: x });
    const companyId = (v.companyId ?? (cos.length === 1 ? cos[0].id : null));
    const kind = (Array.isArray(types.data) ? types.data : []).find((t) => t.id === v.typeId);
    const remote = !kind?.peripheral;
    const create = async (confirmDuplicate) => {
        setErr('');
        if (!confirmDuplicate)
            setDup(null);
        try {
            const r = await api('/api/assets', { body: { name: v.name, typeId: v.typeId, status: v.status, manufacturer: v.manufacturer, model: v.model, serial: v.serial, assignedTo: v.assignedTo, companyId, ...purchaseBody(v), ipAddress: String(v.ipAddress).trim(), macAddress: String(v.macAddress).trim(), hostname: String(v.hostname).trim(), monitored: remote ? v.monitored : false, custom: v.custom, parentId: main ? Number(main.id) : undefined, confirmDuplicate } });
            onClose(r.id);
        }
        catch (e) {
            const m = matchesOf(e);
            if (m)
                setDup(m);
            else
                setErr(e.message);
        }
    };
    return _jsx(Modal, { title: "Add asset", onClose: () => onClose(), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: v.name, onChange: (e) => set('name', e.target.value), autoFocus: true }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Customer", children: _jsxs("select", { value: companyId ?? '', onChange: (e) => setV({ ...v, companyId: e.target.value ? Number(e.target.value) : null, siteId: null, vendorId: null }), children: [cos.length !== 1 && _jsx("option", { value: "", children: "(own organisation)" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsx(Field, { label: "Type", children: _jsxs("select", { value: v.typeId ?? '', onChange: (e) => setV({ ...v, typeId: e.target.value ? Number(e.target.value) : undefined }), children: [_jsx("option", { value: "", children: "(none)" }), (Array.isArray(types.data) ? types.data : []).map((t) => _jsx("option", { value: t.id, children: t.name }, t.id))] }) }), _jsx(Field, { label: "Status", children: _jsx("select", { value: v.status, onChange: (e) => set('status', e.target.value), children: STATUSES.map((s) => _jsx("option", { value: s, children: label(s) }, s)) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Manufacturer", children: _jsx("input", { value: v.manufacturer, onChange: (e) => set('manufacturer', e.target.value) }) }), _jsx(Field, { label: "Model", children: _jsx("input", { value: v.model, onChange: (e) => set('model', e.target.value) }) }), _jsx(Field, { label: "Serial number", children: _jsx("input", { value: v.serial, onChange: (e) => set('serial', e.target.value) }) })] }), _jsx(Field, { label: "Assigned to (name or email)", children: _jsx("input", { value: v.assignedTo, onChange: (e) => set('assignedTo', e.target.value) }) }), remote ? _jsxs(_Fragment, { children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "IP address (DeskAsk reaches the device here)", children: _jsx("input", { value: v.ipAddress, onChange: (e) => set('ipAddress', e.target.value), placeholder: "10.0.0.25", spellCheck: false }) }), _jsx(Field, { label: "MAC address", children: _jsx("input", { value: v.macAddress, onChange: (e) => set('macAddress', e.target.value), placeholder: "00:1A:2B:3C:4D:5E", spellCheck: false }) })] }), _jsx(Field, { label: "Host name", children: _jsx("input", { value: v.hostname, onChange: (e) => set('hostname', e.target.value), spellCheck: false }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: v.monitored, onChange: (e) => setV({ ...v, monitored: e.target.checked }) }), " Monitor this asset (alerts, services, checks)"] }), _jsx("p", { className: "muted small", children: "To install the agent from a controller, save the asset with its IP address, then open it and use \"Install agent through the controller\"." })] }) : _jsxs(_Fragment, { children: [_jsxs("p", { className: "muted small", children: [kind?.name, " is not reached remotely, so it has no agent or monitoring. Give it its own serial number."] }), _jsx(MainDevicePicker, { value: main, onChange: setMain, companyId: companyId })] }), _jsx(CustomFields, { fields: (kind?.fields ?? []), value: v.custom, onChange: (c) => setV({ ...v, custom: c }) }), _jsx(PurchaseFields, { v: v, set: (p) => setV({ ...v, ...p }), companyId: companyId }), dup && _jsx(DuplicateNotice, { info: dup, busy: f.busy, onAnyway: () => void create(true) }), _jsx(Err, { text: err || f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(), children: "Cancel" }), _jsx("button", { className: "primary", disabled: !String(v.name).trim(), onClick: () => void create(false), children: "Create" })] })] }) });
}
export function PrintDialog({ ids, onClose }) {
    const [layout, setLayout] = useState('sheet');
    const [content, setContent] = useState('details');
    const [size, setSize] = useState('35');
    const [text, setText] = useState(true);
    return _jsx(Modal, { title: `Print QR labels (${ids.length})`, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "Layout", children: _jsxs("select", { value: layout, onChange: (e) => setLayout(e.target.value), children: [_jsx("option", { value: "sheet", children: "A4 sheet, 10 \u00D7 10" }), _jsx("option", { value: "label", children: "One label each" })] }) }), _jsx(Field, { label: "Content", children: _jsxs("select", { value: content, onChange: (e) => setContent(e.target.value), children: [_jsx("option", { value: "details", children: "Details (compact)" }), _jsx("option", { value: "full", children: "Full details" }), _jsx("option", { value: "link", children: "Link to the asset" })] }) })] }), layout === 'label' && _jsx(Field, { label: "Label size", children: _jsxs("select", { value: size, onChange: (e) => setSize(e.target.value), children: [_jsx("option", { value: "25", children: "25 mm" }), _jsx("option", { value: "35", children: "35 mm" }), _jsx("option", { value: "50", children: "50 mm" })] }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: text, onChange: (e) => setText(e.target.checked) }), " Print the tag under each code"] }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary", onClick: () => { void openPage(`/api/assets/qr/print?ids=${ids.join(',')}&layout=${layout}&content=${content}&size=${size}&text=${text ? 1 : 0}`); onClose(); }, children: "Open print page" })] })] }) });
}
