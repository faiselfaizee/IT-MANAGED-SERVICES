import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import PersonInput from './PersonInput';
import { useState } from 'react';
import { api, useLoad } from '../http';
import { usePerms } from '../perms';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';
import { PurchaseFields, purchaseBody } from './Purchase';
/** The extra fields a kind of asset defines (IMEI and phone number for a phone, the screen size of a monitor). */
export function CustomFields({ fields, value, onChange }) {
    if (fields.length === 0)
        return null;
    const set = (k, x) => { const n = { ...value }; if (x === '' || x === undefined || x === null)
        delete n[k];
    else
        n[k] = x; onChange(n); };
    return _jsxs("fieldset", { className: "custom-fields", children: [_jsx("legend", { children: "Details of this kind" }), _jsx("div", { className: "row", children: fields.map((f) => _jsx(Field, { label: f.label, children: f.kind === 'bool' ? _jsx("input", { type: "checkbox", checked: value[f.key] === true, onChange: (e) => set(f.key, e.target.checked ? true : undefined) })
                        : f.kind === 'choice' ? _jsxs("select", { value: value[f.key] ?? '', onChange: (e) => set(f.key, e.target.value), children: [_jsx("option", { value: "", children: "(not set)" }), (f.options ?? []).map((o) => _jsx("option", { children: o }, o))] })
                            : f.kind === 'number' ? _jsx("input", { type: "number", value: value[f.key] ?? '', onChange: (e) => set(f.key, e.target.value === '' ? undefined : Number(e.target.value)) })
                                : _jsx("input", { type: f.kind === 'date' ? 'date' : 'text', value: value[f.key] ?? '', onChange: (e) => set(f.key, e.target.value) }) }, f.key)) })] });
}
/** Choose the main device (the desktop or laptop) an accessory belongs to, by its serial number, tag or name. */
export function MainDevicePicker({ value, onChange, companyId, selfId }) {
    const [q, setQ] = useState('');
    const found = useLoad(q.trim().length >= 2 ? `/api/assets?search=${encodeURIComponent(q.trim())}&take=8${companyId ? `&company=${companyId}` : ''}` : null, [q, companyId]);
    const items = (found.data?.items ?? []).filter((a) => a.id !== selfId);
    if (value)
        return _jsxs("div", { className: "attached-to", children: ["Attached to ", _jsx("b", { children: value.name }), " ", _jsx("span", { className: "mono muted", children: value.tag }), value.serial ? _jsxs(_Fragment, { children: [" \u00B7 serial ", _jsx("b", { className: "mono", children: value.serial })] }) : null, ' ', _jsx("button", { type: "button", onClick: () => onChange(null), "aria-label": "Detach from the main device", children: "\u2715" })] });
    return _jsxs("div", { children: [_jsx(Field, { label: "Attached to (main device: search by its serial number, tag or name)", children: _jsx("input", { value: q, onChange: (e) => setQ(e.target.value), placeholder: "Serial number of the desktop or laptop" }) }), items.length > 0 && _jsx("ul", { className: "pick", children: items.map((a) => _jsx("li", { children: _jsxs("button", { type: "button", onClick: () => { onChange(a); setQ(''); }, children: [a.tag, " \u00B7 ", a.name, a.serial ? ` · ${a.serial}` : '', a.assignedTo ? ` · ${a.assignedTo}` : ''] }) }, a.id)) })] });
}
/** On an asset's page: the main device it is attached to, or the accessories attached to it, with their serial numbers. */
export function AttachedCard({ id, d, data, reload, canEdit }) {
    const [mode, setMode] = useState('');
    const [err, setErr] = useState('');
    if (!data)
        return null;
    if (!data.parent && (data.items ?? []).length === 0 && !canEdit)
        return null;
    const parent = data.parent;
    const kids = (data.items ?? []);
    const put = async (parentId) => { setErr(''); try {
        await api(`/api/assets/${id}/attach`, { method: 'PUT', body: { parentId } });
        setMode('');
        reload();
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(Card, { title: "Attached items", children: [parent && _jsxs("p", { children: ["Attached to ", _jsx("a", { href: `#/assets/${parent.id}`, children: parent.name }), " ", _jsx("span", { className: "muted mono", children: parent.tag }), parent.serial ? _jsxs(_Fragment, { children: [" \u00B7 serial ", _jsx("b", { className: "mono", children: parent.serial })] }) : null, parent.assignedTo ? _jsxs(_Fragment, { children: [" \u00B7 ", parent.assignedTo] }) : null, canEdit && _jsxs(_Fragment, { children: [" ", _jsx("button", { onClick: () => void put(null), children: "Detach" })] })] }), kids.length > 0 ? _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Kind" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Serial number" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Warranty ends" })] }) }), _jsx("tbody", { children: kids.map((k) => _jsxs("tr", { children: [_jsx("td", { children: k.type }), _jsxs("td", { children: [_jsx("a", { href: `#/assets/${k.id}`, children: k.name }), " ", _jsx("span", { className: "muted mono", children: k.tag })] }), _jsx("td", { className: "mono", children: k.serial || '–' }), _jsx("td", { children: String(k.status).replace('_', ' ') }), _jsx("td", { children: k.warrantyEnd ? fmtDay(k.warrantyEnd) : '' })] }, k.id)) })] })
                : !parent && _jsx(Empty, { children: "Nothing is attached. Attach the monitors, keyboard, mouse and docking station to the desktop or laptop they came with, so support can find them by its serial number." }), _jsx(Err, { text: err }), canEdit && _jsxs("div", { className: "tools", children: [!parent && _jsx("button", { onClick: () => setMode('attach'), children: "Attach to a main device\u2026" }), !parent && _jsx("button", { onClick: () => setMode('add'), children: "Add accessories\u2026" })] }), mode === 'attach' && _jsx(Modal, { title: "Attach to a main device", onClose: () => setMode(''), children: _jsxs("div", { className: "form", children: [_jsx(MainDevicePicker, { value: null, onChange: (m) => { if (m)
                                void put(Number(m.id)); }, companyId: d.companyId, selfId: Number(id) }), _jsx("p", { className: "muted small", children: "Purchase, warranty, site and holder that this asset does not have yet are copied from the main device." }), _jsx(Err, { text: err }), _jsx("div", { className: "actions", children: _jsx("button", { onClick: () => setMode(''), children: "Cancel" }) })] }) }), mode === 'add' && _jsx(AddSeveral, { parent: { id: Number(id), tag: d.tag, name: d.name, serial: d.identity?.serial ?? '' }, companyId: d.companyId, onClose: (ch) => { setMode(''); if (ch)
                    reload(); } })] });
}
/** Add several of one kind in one go: a list of serial numbers, or a quantity; attached to a main device if wanted. */
export function AddSeveral({ parent, companyId, onClose }) {
    const types = useLoad('/api/assets/types');
    const companies = useLoad('/api/assets/companies');
    const tl = Array.isArray(types.data) ? types.data : [];
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const [v, setV] = useState({ typeId: '', name: '', manufacturer: '', model: '', assignedTo: '', serials: '', quantity: 1, companyId: companyId ?? null });
    const [main, setMain] = useState(parent ?? null);
    const [res, setRes] = useState(null);
    const f = useSubmit(() => undefined);
    const set = (k, x) => setV({ ...v, [k]: x });
    const co = (main ? (parent ? companyId : v.companyId) : (v.companyId ?? (cos.length === 1 ? cos[0].id : null)));
    const serials = String(v.serials).split(/[\n,;\t]+/).map((s) => s.trim()).filter(Boolean);
    const go = () => f.submit(async () => {
        const body = { typeId: Number(v.typeId), companyId: co ?? undefined, parentId: main ? Number(main.id) : undefined, name: v.name, manufacturer: v.manufacturer, model: v.model, assignedTo: v.assignedTo, ...purchaseBody(v) };
        if (serials.length)
            body.items = serials.map((serial) => ({ serial }));
        else
            body.quantity = Math.max(1, Number(v.quantity) || 1);
        setRes(await api('/api/assets/bulk-add', { body }));
    });
    if (res)
        return _jsx(Modal, { title: "Added", onClose: () => onClose(true), children: _jsxs("div", { className: "form", children: [_jsxs("p", { role: "status", children: [_jsx("b", { children: (res.created ?? []).length }), " added", (res.skipped ?? []).length > 0 ? `, ${res.skipped.length} not added` : '', "."] }), res.created.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Tag" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Serial number" })] }) }), _jsx("tbody", { children: res.created.map((c) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: _jsx("a", { href: `#/assets/${c.id}`, children: c.tag }) }), _jsx("td", { children: c.name }), _jsx("td", { className: "mono", children: c.serial })] }, c.id)) })] }), res.skipped.length > 0 && _jsxs(_Fragment, { children: [_jsx("h4", { children: "Not added" }), _jsx("ul", { children: res.skipped.map((s, i) => _jsxs("li", { children: [_jsx("span", { className: "mono", children: s.serial || s.name }), ": ", s.reason] }, i)) })] }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", onClick: () => onClose(true), children: "Close" }) })] }) });
    return _jsx(Modal, { title: "Add several", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "Kind", children: _jsxs("select", { value: v.typeId, onChange: (e) => set('typeId', e.target.value), children: [_jsx("option", { value: "", children: "(choose)" }), tl.map((t) => _jsx("option", { value: t.id, children: t.name }, t.id))] }) }), !parent && _jsx(Field, { label: "Customer", children: _jsxs("select", { value: co ?? '', onChange: (e) => set('companyId', e.target.value ? Number(e.target.value) : null), children: [cos.length !== 1 && _jsx("option", { value: "", children: "(own organisation)" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) })] }), _jsx(MainDevicePicker, { value: main, onChange: setMain, companyId: co }), _jsx(Field, { label: "Serial numbers, one per line", children: _jsx("textarea", { rows: 4, value: v.serials, onChange: (e) => set('serials', e.target.value), placeholder: 'CN12345678\nCN12345679' }) }), serials.length === 0 ? _jsx(Field, { label: "Or how many, without serial numbers", children: _jsx("input", { type: "number", min: 1, max: 200, value: v.quantity, onChange: (e) => set('quantity', e.target.value) }) }) : _jsxs("p", { className: "muted small", children: [serials.length, " to add."] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Name (optional)", children: _jsx("input", { value: v.name, onChange: (e) => set('name', e.target.value), placeholder: "Dell mouse" }) }), _jsx(Field, { label: "Manufacturer", children: _jsx("input", { value: v.manufacturer, onChange: (e) => set('manufacturer', e.target.value) }) }), _jsx(Field, { label: "Model", children: _jsx("input", { value: v.model, onChange: (e) => set('model', e.target.value) }) })] }), _jsx(Field, { label: "Assigned to (name or email)", children: _jsx("input", { value: v.assignedTo, onChange: (e) => set('assignedTo', e.target.value) }) }), main && _jsx("p", { className: "muted small", children: "Purchase, warranty, vendor, site and holder you leave empty are copied from the main device." }), _jsx(PurchaseFields, { v: v, set: (p) => setV({ ...v, ...p }), companyId: co ?? null }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !v.typeId, onClick: () => void go(), children: "Add" })] })] }) });
}
const esc = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
/** A sheet to sign when assets are handed over: what the person holds, with serial numbers. */
export function printSheet(holder, company, items) {
    const w = window.open('', '_blank');
    if (!w)
        return;
    const rows = items.map((a) => `<tr><td>${esc(a.type)}</td><td>${esc(a.tag)}</td><td>${esc(a.name)}</td><td>${esc([a.manufacturer, a.model].filter(Boolean).join(' '))}</td><td>${esc(a.serial)}</td><td>${esc(a.parentTag)}</td></tr>`).join('');
    w.document.write(`<!doctype html><title>Handover ${esc(holder)}</title><style>body{font:14px sans-serif;margin:32px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #888;padding:6px;text-align:left}.sig{margin-top:60px;display:flex;gap:48px}.sig div{flex:1;border-top:1px solid #000;padding-top:4px}</style>`
        + `<h1>Asset handover</h1><p><b>${esc(holder)}</b>${company ? ' · ' + esc(company) : ''} · ${esc(new Date().toLocaleDateString())}</p>`
        + `<table><thead><tr><th>Kind</th><th>Tag</th><th>Name</th><th>Make and model</th><th>Serial number</th><th>Attached to</th></tr></thead><tbody>${rows}</tbody></table>`
        + `<p>I have received the items above in working condition and will return them when asked.</p><div class="sig"><div>Received by (signature, date)</div><div>Handed over by (signature, date)</div></div>`);
    w.document.close();
    w.focus();
    w.print();
}
/** What each person holds: one list, hand several things back at once, give things from stock, print the sheet. */
export function People() {
    const perms = usePerms('assets');
    const [q, setQ] = useState('');
    const [who, setWho] = useState(null);
    const [newName, setNewName] = useState('');
    const [give, setGive] = useState(false);
    const holders = useLoad(`/api/assets/holders?search=${encodeURIComponent(q)}`, [q]);
    const directory = useLoad(`/api/assets/directory-people?limit=500&search=${encodeURIComponent(q)}`, [q]);
    // people who hold something, then everyone else the customer's domain controller lists (holding nothing yet)
    const held = (holders.data?.items ?? []);
    const key = (n, c) => n.toLowerCase() + '|' + (c ?? '');
    const known = new Set(held.map((h) => key(h.name, h.companyId)));
    const others = (directory.data?.items ?? []).filter((p) => !known.has(key(p.name, p.companyId)))
        .map((p) => ({ name: p.name, companyId: p.companyId, company: p.company, assets: 0, accessories: 0, detail: [p.email, p.department].filter(Boolean).join(' · ') }));
    const list = [...held, ...others];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "People" }), _jsx("p", { className: "muted", children: "Everything handed to a person: the laptop, the monitors, the phone. Choose a person to see their items, take several back at once or print the sheet they sign." }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search people", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Search people" }), perms.admin && _jsxs(_Fragment, { children: [_jsx(PersonInput, { placeholder: "Give things to someone new (name or email)", value: newName, onChange: setNewName, label: "Someone new" }), _jsx("button", { disabled: !newName.trim(), onClick: () => { setWho({ name: newName.trim(), companyId: null, company: '' }); setGive(true); }, children: "Give\u2026" })] })] }), _jsx(Loading, { loading: holders.loading && !holders.data, error: holders.error }), holders.data && (list.length === 0 ? _jsx(Empty, { children: "No one holds anything yet. Set up the customer's domain controller under Devices setup to list their people here." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Person" }), _jsx("th", { children: "Customer" }), _jsx("th", { className: "num", children: "Items" }), _jsx("th", { className: "num", children: "Accessories" })] }) }), _jsx("tbody", { children: list.map((h, i) => _jsxs("tr", { className: who?.name === h.name && who?.companyId === h.companyId ? 'on' : '', children: [_jsxs("td", { children: [_jsx("a", { href: "#/assets/people", onClick: (e) => { e.preventDefault(); setWho({ name: h.name, companyId: h.companyId, company: h.company }); }, children: h.name }), h.detail && _jsx("div", { className: "muted", children: h.detail })] }), _jsx("td", { children: h.company }), _jsx("td", { className: "num", children: h.assets }), _jsx("td", { className: "num", children: h.accessories })] }, i)) })] })), who && _jsx(Holder, { who: who, admin: !!perms.admin, onChanged: holders.reload, onGive: () => setGive(true) }, who.name + who.companyId), give && who && _jsx(Give, { to: who.name, companyId: who.companyId, onClose: (ch) => { setGive(false); if (ch) {
                    holders.reload();
                    setWho({ ...who });
                } } })] });
}
function Holder({ who, admin, onChanged, onGive }) {
    const { data, error, loading, reload } = useLoad(`/api/assets/holder?name=${encodeURIComponent(who.name)}${who.companyId ? `&companyId=${who.companyId}` : ''}`, [who.name, who.companyId]);
    const items = (data?.items ?? []);
    const [sel, setSel] = useState(new Set());
    const [msg, setMsg] = useState('');
    const toggle = (id) => setSel((s) => { const n = new Set(s); if (n.has(id))
        n.delete(id);
    else
        n.add(id); return n; });
    const back = async () => {
        setMsg('');
        try {
            const r = await api('/api/assets/holder/handover', { body: { ids: [...sel], mode: 'return', condition: 'good', includeAttached: true } });
            setSel(new Set());
            setMsg(`${r.done} taken back${(r.skipped ?? []).length ? `, ${r.skipped.length} skipped` : ''}.`);
            reload();
            onChanged();
        }
        catch (e) {
            setMsg(e.message);
        }
    };
    return _jsxs(Card, { title: who.name, children: [_jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsxs(Empty, { children: [who.name, " holds nothing."] }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [admin && _jsx("th", {}), _jsx("th", { children: "Kind" }), _jsx("th", { children: "Tag" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Serial number" }), _jsx("th", { children: "Attached to" }), _jsx("th", { children: "Warranty ends" })] }) }), _jsx("tbody", { children: items.map((a) => _jsxs("tr", { children: [admin && _jsx("td", { children: _jsx("input", { type: "checkbox", "aria-label": 'Select ' + a.name, checked: sel.has(a.id), onChange: () => toggle(a.id) }) }), _jsxs("td", { children: [a.type || '–', " ", a.peripheral && _jsx(Badge, { children: "accessory" })] }), _jsx("td", { className: "mono", children: a.tag }), _jsx("td", { children: _jsx("a", { href: `#/assets/${a.id}`, children: a.name }) }), _jsx("td", { className: "mono", children: a.serial || '–' }), _jsx("td", { className: "mono", children: a.parentTag }), _jsx("td", { children: a.warrantyEnd ? fmtDay(a.warrantyEnd) : '' })] }, a.id)) })] })), _jsxs("div", { className: "tools", children: [admin && _jsx("button", { onClick: onGive, children: "Give more\u2026" }), admin && _jsx(Act, { label: `Take back selected (${sel.size})`, confirm: `Take ${sel.size} item${sel.size === 1 ? '' : 's'} back from ${who.name}? Items attached to a chosen one come with it.`, run: back }), _jsx("button", { disabled: items.length === 0, onClick: () => printSheet(who.name, who.company, items), children: "Print handover sheet" }), msg && _jsx("span", { role: "status", children: msg })] })] });
}
/** Hand things from stock to a person. */
function Give({ to, companyId, onClose }) {
    const [q, setQ] = useState('');
    const [sel, setSel] = useState(new Set());
    const found = useLoad(`/api/assets?search=${encodeURIComponent(q)}&status=stock&take=40${companyId ? `&company=${companyId}` : ''}`, [q, companyId]);
    const items = (found.data?.items ?? []).filter((a) => !a.assignedTo);
    const f = useSubmit(() => onClose(true));
    const toggle = (id) => setSel((s) => { const n = new Set(s); if (n.has(id))
        n.delete(id);
    else
        n.add(id); return n; });
    return _jsx(Modal, { title: `Give to ${to}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Search the stock", children: _jsx("input", { value: q, onChange: (e) => setQ(e.target.value), placeholder: "Name, tag or serial number", autoFocus: true }) }), items.length === 0 ? _jsx(Empty, { children: "Nothing in stock matches." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", {}), _jsx("th", { children: "Kind" }), _jsx("th", { children: "Tag" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Serial number" })] }) }), _jsx("tbody", { children: items.map((a) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("input", { type: "checkbox", "aria-label": 'Select ' + a.name, checked: sel.has(a.id), onChange: () => toggle(a.id) }) }), _jsx("td", { children: a.type }), _jsx("td", { className: "mono", children: a.tag }), _jsx("td", { children: a.name }), _jsx("td", { className: "mono", children: a.serial })] }, a.id)) })] }), _jsx("p", { className: "muted small", children: "Items attached to a chosen one go with it." }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsxs("button", { className: "primary", disabled: f.busy || sel.size === 0, onClick: () => void f.submit(() => api('/api/assets/holder/handover', { body: { ids: [...sel], mode: 'assign', to, condition: 'good', includeAttached: true } })), children: ["Give ", sel.size || ''] })] })] }) });
}
/** How many of each kind are in stock, in use or in repair. */
export function Stock() {
    const companies = useLoad('/api/assets/companies');
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const [co, setCo] = useState('');
    const { data, error, loading } = useLoad(`/api/assets/stock${co ? `?companyId=${co}` : ''}`, [co]);
    const rows = (data?.items ?? []);
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Stock" }), _jsx("p", { className: "muted", children: "How many of each kind are in stock, handed out or in repair. Choose a number to see those assets." }), cos.length > 1 && _jsx("div", { className: "tools", children: _jsxs("select", { value: co, onChange: (e) => setCo(e.target.value), "aria-label": "Customer", children: [_jsx("option", { value: "", children: "All customers" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (rows.length === 0 ? _jsx(Empty, { children: "No assets yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Kind" }), _jsx("th", { className: "num", children: "In stock" }), _jsx("th", { className: "num", children: "In use" }), _jsx("th", { className: "num", children: "In repair" }), _jsx("th", { className: "num", children: "Retired" }), _jsx("th", { className: "num", children: "Lost" }), _jsx("th", { className: "num", children: "Total" })] }) }), _jsx("tbody", { children: rows.map((r) => {
                            // each count opens All assets with that kind, status and customer
                            const to = (status, n, bold = false) => {
                                const t = _jsx(_Fragment, { children: bold ? _jsx("b", { children: n }) : n });
                                if (!n)
                                    return t;
                                return _jsx("a", { href: `#/assets/list?typeId=${r.typeId ?? 'none'}${status ? `&status=${status}` : ''}${co ? `&company=${co}` : ''}`, "aria-label": `${n} ${r.type}${status ? ' ' + status.replace('_', ' ') : ''}`, children: t });
                            };
                            return _jsxs("tr", { children: [_jsxs("td", { children: [r.type, " ", r.peripheral && _jsx(Badge, { children: "accessory" })] }), _jsx("td", { className: "num", children: to('stock', r.stock) }), _jsx("td", { className: "num", children: to('in_use', r.inUse) }), _jsx("td", { className: "num", children: to('repair', r.repair) }), _jsx("td", { className: "num", children: to('retired', r.retired) }), _jsx("td", { className: "num", children: to('lost', r.lost) }), _jsx("td", { className: "num", children: to('', r.total, true) })] }, r.type);
                        }) })] }))] });
}
const KINDS = ['text', 'number', 'date', 'bool', 'choice'];
const slug = (s) => { const k = s.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 30); return /^[a-z]/.test(k) ? k : ('f_' + k).slice(0, 30); };
/** The kinds of asset and the extra details each one keeps. */
export function Types() {
    const { data, error, loading, reload } = useLoad('/api/assets/types');
    const [edit, setEdit] = useState(null);
    const list = Array.isArray(data) ? data : [];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Kinds of asset" }), _jsxs("p", { className: "muted", children: ["Each kind keeps its own extra details (a phone's IMEI, a monitor's screen size). A kind marked ", _jsx("b", { children: "not reached remotely" }), " has no agent, shell or remote control and is not watched by monitoring."] }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", onClick: () => setEdit('new'), children: "New kind" }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Kind" }), _jsx("th", { children: "Details" }), _jsx("th", { children: "Not reached remotely" }), _jsx("th", { className: "num", children: "Assets" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((t) => _jsxs("tr", { children: [_jsx("td", { children: t.name }), _jsx("td", { className: "muted", children: (t.fields ?? []).map((f) => f.label).join(', ') }), _jsx("td", { children: t.peripheral ? 'Yes' : '' }), _jsx("td", { className: "num", children: t.assets }), _jsx("td", { children: _jsx("button", { onClick: () => setEdit(t), children: "Change" }) })] }, t.id)) })] }), edit && _jsx(TypeForm, { t: edit === 'new' ? null : edit, onClose: (ch) => { setEdit(null); if (ch)
                    reload(); } })] });
}
function TypeForm({ t, onClose }) {
    const [name, setName] = useState(t?.name ?? '');
    const [peripheral, setPeripheral] = useState(!!t?.peripheral);
    const [text, setText] = useState((t?.fields ?? []).map((f) => `${f.label}:${f.kind}${f.kind === 'choice' ? ':' + (f.options ?? []).join('/') : ''}`).join('\n'));
    const f = useSubmit(() => onClose(true));
    const fields = () => {
        const used = new Set();
        const old = (t?.fields ?? []);
        return text.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
            const [label, kind = 'text', opts = ''] = l.split(':').map((x) => x.trim());
            if (!KINDS.includes(kind))
                throw new Error(`"${l}": the kind must be one of ${KINDS.join(', ')}`);
            let key = old.find((o) => o.label === label)?.key ?? slug(label);
            let n = 2;
            while (used.has(key))
                key = (slug(label) + '_' + n++).slice(0, 30);
            used.add(key);
            return { key, label, kind, ...(kind === 'choice' ? { options: opts.split('/').map((x) => x.trim()).filter(Boolean) } : {}) };
        });
    };
    return _jsx(Modal, { title: t ? `Change ${t.name}` : 'New kind of asset', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: name, onChange: (e) => setName(e.target.value), autoFocus: true }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: peripheral, onChange: (e) => setPeripheral(e.target.checked) }), " Not reached remotely (no agent, shell or remote control; not monitored)"] }), _jsx(Field, { label: "Extra details, one per line as  Label:kind  (kinds: text, number, date, bool, choice; a choice lists its options like  OS:choice:Android/iOS)", children: _jsx("textarea", { rows: 6, value: text, onChange: (e) => setText(e.target.value), placeholder: 'IMEI:text\nStorage (GB):number', spellCheck: false }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !name.trim(), onClick: () => void f.submit(() => api(t ? `/api/assets/types/${t.id}` : '/api/assets/types', { method: t ? 'PUT' : 'POST', body: { name: name.trim(), peripheral, fields: fields() } })), children: "Save" })] })] }) });
}
