import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useMemo } from 'react';
import { useLoad } from '../http';
import { Field } from '../ui';
/** "Bengaluru HQ (BLR01), Karnataka, India" */
export function placePath(all, id) {
    const parts = [];
    let p = all.find((x) => x.id === id);
    while (p) {
        parts.push(p.level === 'site' && p.code ? `${p.name} (${p.code})` : p.name);
        const parent = p.parentId;
        p = all.find((x) => x.id === parent);
    }
    return parts.join(', ');
}
/** Adds a number of months to a date and takes off a day: a 36-month warranty bought on 15 Jan 2024 ends on 14 Jan 2027. */
export function warrantyEnd(purchase, months) {
    if (!/^\d{4}-\d{2}-\d{2}$/.test(purchase) || !Number.isFinite(months) || months <= 0)
        return '';
    const [y, m, d] = purchase.split('-').map(Number);
    const t = new Date(Date.UTC(y, m - 1 + months, 1));
    const last = new Date(Date.UTC(t.getUTCFullYear(), t.getUTCMonth() + 1, 0)).getUTCDate();
    t.setUTCDate(Math.min(d, last));
    t.setUTCDate(t.getUTCDate() - 1);
    return t.toISOString().slice(0, 10);
}
/** Country, then state, then site: only this customer's. */
export function SitePicker({ companyId, value, onChange }) {
    const all = useLoad(companyId ? `/api/assets/places?company=${companyId}` : null, [companyId]);
    const places = (all.data?.items ?? []).filter((p) => p.active !== false || p.id === value);
    const sites = places.filter((p) => p.level === 'site');
    if (!companyId)
        return _jsx(Field, { label: "Site", children: _jsx("select", { disabled: true, children: _jsx("option", { children: "Choose the customer first" }) }) });
    if (all.data && sites.length === 0)
        return _jsx(Field, { label: "Site", children: _jsx("select", { disabled: true, children: _jsx("option", { children: "No sites yet: add them under Assets \u203A Locations" }) }) });
    return _jsx(Field, { label: "Site", children: _jsxs("select", { value: value ?? '', onChange: (e) => onChange(e.target.value ? Number(e.target.value) : null), children: [_jsx("option", { value: "", children: "(no site)" }), places.filter((p) => p.level === 'country').map((c) => _jsx("optgroup", { label: c.name, children: sites.filter((s) => s.parentId === c.id || places.some((st) => st.id === s.parentId && st.parentId === c.id))
                        .map((s) => _jsx("option", { value: s.id, children: placePath(places, s.id).split(', ').slice(0, -1).join(', ') }, s.id)) }, c.id))] }) });
}
/** The vendors that fit this customer: its own and the shared ones. */
export function VendorPicker({ companyId, value, onChange, label = 'Bought from (vendor)' }) {
    const all = useLoad(`/api/assets/vendors${companyId ? `?company=${companyId}` : ''}`, [companyId]);
    const list = (all.data?.items ?? []).filter((v) => (v.companyId == null || v.companyId === companyId) && (v.active !== false || v.id === value));
    return _jsx(Field, { label: label, children: _jsxs("select", { value: value ?? '', onChange: (e) => onChange(e.target.value ? Number(e.target.value) : null), children: [_jsx("option", { value: "", children: "(not recorded)" }), list.map((v) => _jsxs("option", { value: v.id, children: [v.name, v.companyId == null ? '' : ' (this customer)'] }, v.id))] }) });
}
const PERIODS = [12, 24, 36, 48, 60];
/** Site, vendor, order and warranty fields of an asset, shared by the add and edit forms. */
export function PurchaseFields({ v, set, companyId }) {
    const months = Number(v.warrantyMonths) || 0;
    const computed = useMemo(() => warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), months), [v.purchaseDate, months]);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "row", children: [_jsx(SitePicker, { companyId: companyId, value: v.siteId ?? null, onChange: (id) => set({ siteId: id }) }), _jsx(Field, { label: "Room or area", children: _jsx("input", { value: v.location ?? '', onChange: (e) => set({ location: e.target.value }), placeholder: "2nd floor, server room" }) })] }), _jsxs("div", { className: "row", children: [_jsx(VendorPicker, { companyId: companyId, value: v.vendorId ?? null, onChange: (id) => set({ vendorId: id }) }), _jsx(Field, { label: "Purchase order", children: _jsx("input", { value: v.poNumber ?? '', onChange: (e) => set({ poNumber: e.target.value }) }) }), _jsx(Field, { label: "Invoice", children: _jsx("input", { value: v.invoiceNumber ?? '', onChange: (e) => set({ invoiceNumber: e.target.value }) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Purchase date", children: _jsx("input", { type: "date", value: String(v.purchaseDate ?? '').slice(0, 10), onChange: (e) => { const end = warrantyEnd(e.target.value, months); set({ purchaseDate: e.target.value, ...(end ? { warrantyEnd: end } : {}) }); } }) }), _jsx(Field, { label: "Warranty period", children: _jsxs("select", { value: PERIODS.includes(months) || !months ? String(months || '') : 'other', onChange: (e) => {
                                const m = e.target.value === 'other' ? months || 1 : Number(e.target.value) || 0;
                                const end = warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), m);
                                set({ warrantyMonths: m || null, ...(end ? { warrantyEnd: end } : {}) });
                            }, children: [_jsx("option", { value: "", children: "(not recorded)" }), PERIODS.map((p) => _jsxs("option", { value: p, children: [p / 12, " year", p > 12 ? 's' : ''] }, p)), _jsx("option", { value: "other", children: "Other (months)" })] }) }), !!months && !PERIODS.includes(months) && _jsx(Field, { label: "Months", children: _jsx("input", { type: "number", min: 1, max: 240, value: months, onChange: (e) => { const m = Number(e.target.value) || 0; const end = warrantyEnd(String(v.purchaseDate ?? '').slice(0, 10), m); set({ warrantyMonths: m || null, ...(end ? { warrantyEnd: end } : {}) }); } }) }), _jsx(Field, { label: "Warranty ends", children: _jsx("input", { type: "date", value: String(v.warrantyEnd ?? '').slice(0, 10), onChange: (e) => set({ warrantyEnd: e.target.value }) }) })] }), computed && v.warrantyEnd && computed !== String(v.warrantyEnd).slice(0, 10) && _jsxs("p", { className: "muted small", children: ["The period gives ", computed, "; the end date you typed is kept."] })] });
}
/** The asset fields PurchaseFields edits, ready for the API. */
export function purchaseBody(v) {
    return { siteId: v.siteId ?? null, vendorId: v.vendorId ?? null, poNumber: v.poNumber ?? '', invoiceNumber: v.invoiceNumber ?? '', location: v.location ?? '',
        purchaseDate: v.purchaseDate ? String(v.purchaseDate).slice(0, 10) : null, warrantyEnd: v.warrantyEnd ? String(v.warrantyEnd).slice(0, 10) : null, warrantyMonths: v.warrantyMonths ? Number(v.warrantyMonths) : null };
}
