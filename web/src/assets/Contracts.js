import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';
import { VendorPicker } from './Purchase';
import { usePerms } from '../perms';
/** Whether the signed-in person may change AMC contracts: administrators, and customer administrators when an administrator has allowed it. */
export function useAmcAccess() {
    const a = useLoad('/api/assets/amc/access');
    return !!a.data?.canEdit;
}
/** AMC (annual maintenance contracts) and rentals share one screen; AMC also has coverage, visits and SLA. */
export default function Contracts({ kind }) {
    const amc = kind === 'amc';
    const path = `/api/assets/${kind}`;
    const perms = usePerms('assets');
    const amcOk = useAmcAccess();
    const canEdit = amc ? amcOk : perms.admin;
    const { data, error, loading, reload } = useLoad(path);
    const [edit, setEdit] = useState(null);
    const [renew, setRenew] = useState(null);
    const list = Array.isArray(data) ? data : [];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: amc ? 'AMC contracts' : 'Rentals' }), canEdit && _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", onClick: () => setEdit({ title: '', companyId: '', start: '', end: '', notes: '', coverage: '', visits: 0, slaPct: 0, assetIds: [], vendorId: null, contractNo: '', cost: '' }), children: amc ? 'Add AMC' : 'Add rental' }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (list.length === 0 ? _jsx(Empty, { children: "Nothing here yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Title" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Vendor" }), _jsx("th", { children: "Period" }), amc && _jsxs(_Fragment, { children: [_jsx("th", { className: "num", children: "Visits" }), _jsx("th", { className: "num", children: "SLA" }), _jsx("th", { className: "num", children: "Cost" })] }), _jsx("th", { className: "num", children: "Assets" }), canEdit && _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((c) => _jsxs("tr", { children: [_jsxs("td", { children: [c.title, c.contractNo && _jsx("div", { className: "mono muted small", children: c.contractNo }), _jsx("div", { className: "muted", children: c.coverage })] }), _jsx("td", { children: c.company }), _jsx("td", { children: c.vendorId ? _jsx("a", { href: `#/assets/vendors/${c.vendorId}`, children: c.vendor }) : _jsx("span", { className: "muted", children: "\u2013" }) }), _jsxs("td", { children: [fmtDay(c.start), " \u2192 ", fmtDay(c.end), " ", c.renewedById ? _jsx(Badge, { tone: "ok", children: "renewed" }) : c.active === false ? _jsx(Badge, { tone: "bad", children: "ended" }) : null, c.previousId && _jsx("div", { className: "muted small", children: "renewal of an earlier contract" })] }), amc && _jsxs(_Fragment, { children: [_jsx("td", { className: "num", children: c.visits }), _jsx("td", { className: "num", children: c.slaPct ? c.slaPct + '%' : '' }), _jsx("td", { className: "num", children: c.cost != null ? Number(c.cost).toLocaleString() : '' })] }), _jsx("td", { className: "num", children: (c.assetIds ?? []).length }), canEdit && _jsxs("td", { children: [_jsx("button", { onClick: () => setEdit(c), children: "Edit" }), " ", amc && !c.renewedById && _jsx("button", { onClick: () => setRenew(c), children: "Renew" }), " ", _jsx(Act, { label: "Delete", danger: true, confirm: `Delete ${c.title}?`, run: async () => { await api(`${path}/${c.id}`, { method: 'DELETE' }); reload(); } })] })] }, c.id)) })] })), edit && _jsx(Form, { amc: amc, path: path, init: edit, onlyOwn: !perms.admin, onClose: (ch) => { setEdit(null); if (ch)
                    reload(); } }), renew && _jsx(Renew, { c: renew, onClose: (ch) => { setRenew(null); if (ch)
                    reload(); } })] });
}
function Form({ amc, path, init, onlyOwn, onClose }) {
    const [v, setV] = useState({ ...init, companyId: init.companyId ?? '' });
    const f = useSubmit(() => onClose(true));
    const set = (k, x) => setV({ ...v, [k]: x });
    const companies = useLoad('/api/assets/companies');
    const [q, setQ] = useState('');
    const assets = useLoad(`/api/assets?search=${encodeURIComponent(q)}&take=100`);
    const ids = new Set(v.assetIds ?? []);
    const body = { title: v.title, companyId: v.companyId === '' ? null : Number(v.companyId), start: v.start || null, end: v.end || null, notes: v.notes, assetIds: [...ids],
        vendorId: v.vendorId ?? null, contractNo: v.contractNo ?? '',
        ...(amc ? { coverage: v.coverage, visits: Number(v.visits) || 0, slaPct: Number(v.slaPct) || 0, cost: v.cost === '' || v.cost == null ? null : Number(v.cost) } : {}) };
    return _jsx(Modal, { title: init.id ? 'Edit' : 'Add', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Title", children: _jsx("input", { value: v.title, onChange: (e) => set('title', e.target.value), autoFocus: true }) }), _jsx(Field, { label: "Customer", children: _jsxs("select", { value: v.companyId, onChange: (e) => set('companyId', e.target.value), children: [!onlyOwn && _jsx("option", { value: "", children: "(none)" }), (Array.isArray(companies.data) ? companies.data : []).map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Starts", children: _jsx("input", { type: "date", value: fmtDay(v.start) === '–' ? '' : fmtDay(v.start), onChange: (e) => set('start', e.target.value) }) }), _jsx(Field, { label: "Ends", children: _jsx("input", { type: "date", value: fmtDay(v.end) === '–' ? '' : fmtDay(v.end), onChange: (e) => set('end', e.target.value) }) })] }), _jsxs("div", { className: "row", children: [_jsx(VendorPicker, { companyId: v.companyId === '' ? null : Number(v.companyId), value: v.vendorId ?? null, onChange: (id) => set('vendorId', id), label: amc ? 'Vendor' : 'Rented from (vendor)' }), _jsx(Field, { label: "Contract number", children: _jsx("input", { value: v.contractNo ?? '', onChange: (e) => set('contractNo', e.target.value) }) }), amc && _jsx(Field, { label: "Cost", children: _jsx("input", { type: "number", min: 0, value: v.cost ?? '', onChange: (e) => set('cost', e.target.value) }) })] }), amc && _jsxs(_Fragment, { children: [_jsx(Field, { label: "Coverage", children: _jsx("textarea", { rows: 2, value: v.coverage, onChange: (e) => set('coverage', e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Visits per year", children: _jsx("input", { type: "number", value: v.visits, onChange: (e) => set('visits', e.target.value) }) }), _jsx(Field, { label: "SLA %", children: _jsx("input", { type: "number", value: v.slaPct, onChange: (e) => set('slaPct', e.target.value) }) })] })] }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: v.notes, onChange: (e) => set('notes', e.target.value) }) }), _jsx(Field, { label: `Assets covered (${ids.size} selected)`, children: _jsx("input", { placeholder: "Search to find assets", value: q, onChange: (e) => setQ(e.target.value) }) }), _jsx("div", { style: { maxHeight: 160, overflow: 'auto', border: '1px solid var(--line)', borderRadius: 6, padding: 6 }, children: (assets.data?.items ?? []).map((a) => _jsxs("label", { style: { display: 'block' }, children: [_jsx("input", { type: "checkbox", checked: ids.has(a.id), onChange: () => { const n = new Set(ids); if (n.has(a.id))
                                    n.delete(a.id);
                                else
                                    n.add(a.id); set('assetIds', [...n]); } }), " ", a.name, " ", _jsx("span", { className: "muted mono", children: a.tag })] }, a.id)) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !String(v.title).trim(), onClick: () => f.submit(() => api(init.id ? `${path}/${init.id}` : path, { method: init.id ? 'PUT' : 'POST', body })), children: "Save" })] })] }) });
}
/** A new contract that follows this one: same customer, vendor, cover and assets, from the day after it ends. */
function Renew({ c, onClose }) {
    const next = (d, days) => { const t = new Date(d + 'T00:00:00Z'); t.setUTCDate(t.getUTCDate() + days); return t.toISOString().slice(0, 10); };
    const startDefault = c.end ? next(String(c.end), 1) : new Date().toISOString().slice(0, 10);
    const yearOn = (s) => { const [y, m, d] = s.split('-').map(Number); return next(new Date(Date.UTC(y + 1, m - 1, d)).toISOString().slice(0, 10), -1); };
    const [start, setStart] = useState(startDefault);
    const [end, setEnd] = useState(yearOn(startDefault));
    const [no, setNo] = useState('');
    const [cost, setCost] = useState(c.cost != null ? String(c.cost) : '');
    const f = useSubmit(() => onClose(true));
    return _jsx(Modal, { title: `Renew ${c.title}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("p", { className: "muted", children: [c.vendor ? `With ${c.vendor}. ` : '', "The renewal keeps the cover and the ", (c.assetIds ?? []).length, " assets; the old contract stops sending reminders."] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Starts", children: _jsx("input", { type: "date", value: start, onChange: (e) => { setStart(e.target.value); if (e.target.value)
                                    setEnd(yearOn(e.target.value)); } }) }), _jsx(Field, { label: "Ends", children: _jsx("input", { type: "date", value: end, onChange: (e) => setEnd(e.target.value) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "New contract number", children: _jsx("input", { value: no, onChange: (e) => setNo(e.target.value), placeholder: c.contractNo || '' }) }), _jsx(Field, { label: "Cost", children: _jsx("input", { type: "number", min: 0, value: cost, onChange: (e) => setCost(e.target.value) }) })] }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api(`/api/assets/amc/${c.id}/renew`, { body: { start, end, contractNo: no || null, cost: cost === '' ? null : Number(cost) } })), children: "Renew" })] })] }) });
}
