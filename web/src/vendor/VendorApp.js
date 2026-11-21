import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useState } from 'react';
import { api, go, useLoad } from '../http';
import PdfDesign from '../PdfDesign';
import { Act, Badge, Card, Copy, Dl, Empty, Err, Field, Loading, Modal, copyText, fmtDate, fmtDay } from '../ui';
/** Module names and the one size limit each is sold by. */
export const MODS = [
    { key: 'servicedesk', name: 'Service Desk', limit: 'agents', unit: 'agents' },
    { key: 'assets', name: 'Asset management', limit: 'max_assets', unit: 'assets' },
    { key: 'monitoring', name: 'Monitoring', limit: 'max_devices', unit: 'devices' },
    { key: 'adsuite', name: 'AD Manager', limit: 'max_domains', unit: 'domains' },
    { key: 'vault', name: 'Vault', limit: 'max_users', unit: 'users' },
    { key: 'backup', name: 'Backup', limit: 'max_devices', unit: 'devices' },
];
const STATE = { trial: ['warn', 'Trial'], active: ['ok', 'Active'], cancelled: ['warn', 'Cancelled (runs to its end)'], ended: ['bad', 'Ended'] };
const day = (d) => new Date(Date.now() + d * 86400000).toISOString().slice(0, 10);
const linesText = (l) => MODS.filter((m) => l?.[m.key]).map((m) => { const v = l[m.key]?.[m.limit]; return m.name + (v != null ? ' ' + v : ' (no limit)'); }).join(', ');
/** The vendor console: subscriptions, licences, licence keys, customers licensed before the store, the price list and the signing key. */
export default function VendorApp({ path }) {
    const status = useLoad('/api/commerce/status');
    const tab = path[0] ?? '';
    const tabs = [['', 'Subscriptions'], ['orders', 'Orders'], ['invoices', 'Invoices'], ['new', 'New subscription'], ['unlinked', 'Not linked yet'], ['prices', 'Price list'], ['business', 'Business details'], ['pages', 'Website pages'], ['link', 'Website link'], ['payments', 'Payments'], ['key', 'Signing key']];
    const ready = !!status.data?.signing?.ready;
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Vendor console" }), _jsx("nav", { className: "sub", "aria-label": "Vendor console", children: tabs.map(([k, label]) => _jsx("a", { href: '#/vendor' + (k ? '/' + k : ''), className: tab === k || (k === '' && tab === 'sub') ? 'on' : '', children: label }, k)) }), status.data && !ready && tab !== 'key' && _jsxs("p", { role: "alert", className: "err", children: ["Licences cannot be issued yet: import the licence signing key first (", _jsx("a", { href: "#/vendor/key", children: "Signing key" }), ")."] }), tab === 'sub' && path[1] ? _jsx(SubDetail, { id: path[1] })
                : tab === 'new' ? _jsx(NewSub, {})
                    : tab === 'unlinked' ? _jsx(Unlinked, {})
                        : tab === 'prices' ? _jsx(Prices, {})
                            : tab === 'orders' ? _jsx(Orders, {})
                                : tab === 'invoices' ? _jsx(InvoiceList, {})
                                    : tab === 'business' ? _jsxs(_Fragment, { children: [_jsx(BusinessDetails, {}), _jsx(PdfDesign, { url: "/api/commerce/invoice-style", previewUrl: "/api/commerce/invoice-style/preview.pdf", withNote: true, title: "Invoice design", intro: "The look of the invoices the store sends: header colour and letters, borders, text, a foot line and the closing note (payment terms, bank details). The sample invoice uses the saved design and your business details." })] })
                                        : tab === 'pages' ? _jsx(WebsitePages, { slug: path[1] })
                                            : tab === 'link' ? _jsx(WebsiteLink, {})
                                                : tab === 'payments' ? _jsx(Payments, {})
                                                    : tab === 'key' ? _jsx(SigningKey, { status: status.data, reload: status.reload })
                                                        : _jsx(SubList, {})] });
}
function SubList() {
    const [q, setQ] = useState('');
    const list = useLoad('/api/commerce/subscriptions?q=' + encodeURIComponent(q), [q]);
    return _jsxs(Card, { children: [_jsx("div", { className: "form", children: _jsx(Field, { label: "Find", children: _jsx("input", { value: q, onChange: (e) => setQ(e.target.value), placeholder: "Company, email or customer id" }) }) }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && list.data.length === 0 ? _jsx(Empty, { children: "No subscriptions yet. Create one, or link customers who already have a licence (Not linked yet)." }) : null, list.data && list.data.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Customer" }), _jsx("th", { children: "Type" }), _jsx("th", { children: "Customer id" }), _jsx("th", { children: "Modules" }), _jsx("th", { children: "Ends" }), _jsx("th", { children: "State" })] }) }), _jsx("tbody", { children: list.data.map((x) => {
                            const s = x.sub;
                            const [tone, label] = STATE[s.state] ?? [undefined, s.state];
                            return _jsxs("tr", { onClick: () => go('vendor/sub/' + s.id), style: { cursor: 'pointer' }, children: [_jsxs("td", { children: [_jsx("b", { children: s.account }), _jsx("div", { className: "small muted", children: x.email })] }), _jsx("td", { children: s.kind === 'saas' ? 'SaaS' : 'On-prem' }), _jsx("td", { children: s.tenantId || _jsx("span", { className: "muted", children: "set at activation" }) }), _jsx("td", { className: "small", children: linesText(s.lines) }), _jsx("td", { children: fmtDay(s.endsAt) }), _jsx("td", { children: _jsx(Badge, { tone: tone, children: label }) })] }, s.id);
                        }) })] })] });
}
/** Choose modules and sizes from the price list, or a custom number. */
function ModulePicker({ lines, onChange }) {
    const plans = useLoad('/api/commerce/plans');
    return _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Module" }), _jsx("th", { children: "Size" })] }) }), _jsx("tbody", { children: MODS.map((m) => {
                    const cur = lines[m.key];
                    const v = cur?.[m.limit];
                    const sizes = (plans.data ?? []).filter((p) => p.module === m.key && p.active);
                    const sel = !cur ? '' : v == null ? 'unl' : sizes.some((p) => p.limits?.[m.limit] === v) ? 'n:' + v : 'custom';
                    const set = (val, n) => {
                        const next = { ...lines };
                        if (val === '')
                            delete next[m.key];
                        else if (val === 'unl')
                            next[m.key] = {};
                        else
                            next[m.key] = { [m.limit]: n ?? Number(val.slice(2)) };
                        onChange(next);
                    };
                    return _jsxs("tr", { children: [_jsx("td", { children: m.name }), _jsxs("td", { children: [_jsxs("select", { "aria-label": m.name + ' size', value: sel, onChange: (e) => set(e.target.value === 'custom' ? 'custom' : e.target.value, e.target.value === 'custom' ? (v ?? 10) : undefined), children: [_jsx("option", { value: "", children: "Not included" }), sizes.filter((p) => p.limits?.[m.limit] != null).map((p) => _jsx("option", { value: 'n:' + p.limits[m.limit], children: p.sizeLabel }, p.id)), _jsx("option", { value: "unl", children: "No limit" }), _jsx("option", { value: "custom", children: "Custom number\u2026" })] }), sel === 'custom' && _jsx("input", { type: "number", min: 0, style: { width: 110, marginLeft: 8 }, "aria-label": m.name + ' ' + m.unit, value: v ?? 0, onChange: (e) => set('custom', Math.max(0, Number(e.target.value) || 0)) }), sel === 'custom' && _jsxs("span", { className: "muted small", children: [" ", m.unit] })] })] }, m.key);
                }) })] });
}
function NewSub() {
    const accounts = useLoad('/api/commerce/accounts');
    const [kind, setKind] = useState('saas');
    const [accountId, setAccountId] = useState('');
    const [acc, setAcc] = useState({ name: '', email: '', gstin: '', country: 'IN', address: '' });
    const [tenantId, setTenantId] = useState('');
    const [plan, setPlan] = useState('business');
    const [period, setPeriod] = useState('year');
    const [lines, setLines] = useState({});
    const [startsAt, setStarts] = useState(day(0));
    const [endsAt, setEnds] = useState(day(365));
    const [grace, setGrace] = useState('');
    const [notes, setNotes] = useState('');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const save = async () => {
        setBusy(true);
        setErr('');
        try {
            const r = await api('/api/commerce/subscriptions', { body: { kind, accountId: accountId || undefined, account: accountId ? undefined : acc, tenantId, plan, period, lines, startsAt, endsAt, graceDays: grace === '' ? undefined : Number(grace), notes } });
            go('vendor/sub/' + r.id);
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsx(Card, { title: "New subscription", children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Type", children: _jsxs("select", { value: kind, onChange: (e) => setKind(e.target.value), children: [_jsx("option", { value: "saas", children: "SaaS (on this platform)" }), _jsx("option", { value: "onprem", children: "On-prem (licence key)" })] }) }), _jsx(Field, { label: "Customer", children: _jsxs("select", { value: accountId, onChange: (e) => setAccountId(e.target.value), children: [_jsx("option", { value: "", children: "New customer\u2026" }), (accounts.data ?? []).map((a) => _jsxs("option", { value: a.id, children: [a.name, " (", a.email, ")"] }, a.id))] }) }), !accountId && _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Company name", children: _jsx("input", { value: acc.name, onChange: (e) => setAcc({ ...acc, name: e.target.value }) }) }), _jsx(Field, { label: "Email", children: _jsx("input", { type: "email", value: acc.email, onChange: (e) => setAcc({ ...acc, email: e.target.value }) }) }), _jsx(Field, { label: "GSTIN (optional)", children: _jsx("input", { value: acc.gstin, onChange: (e) => setAcc({ ...acc, gstin: e.target.value.toUpperCase() }) }) }), _jsx(Field, { label: "Country", children: _jsx("input", { value: acc.country, maxLength: 2, style: { width: 70 }, onChange: (e) => setAcc({ ...acc, country: e.target.value.toUpperCase() }) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: kind === 'saas' ? 'Customer id on this platform' : 'Customer id (optional: taken from the first activation)', children: _jsx("input", { value: tenantId, onChange: (e) => setTenantId(e.target.value.toLowerCase()), placeholder: "acme" }) }), _jsx(Field, { label: "Plan name", children: _jsx("input", { value: plan, onChange: (e) => setPlan(e.target.value) }) }), _jsx(Field, { label: "Billing", children: _jsxs("select", { value: period, onChange: (e) => setPeriod(e.target.value), children: [_jsx("option", { value: "year", children: "Yearly" }), _jsx("option", { value: "month", children: "Monthly" }), _jsx("option", { value: "manual", children: "Manual / special deal" })] }) })] }), _jsx(ModulePicker, { lines: lines, onChange: setLines }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Starts", children: _jsx("input", { type: "date", value: startsAt, onChange: (e) => setStarts(e.target.value) }) }), _jsx(Field, { label: "Ends", children: _jsx("input", { type: "date", value: endsAt, onChange: (e) => setEnds(e.target.value) }) }), _jsx(Field, { label: 'Grace days (default ' + (kind === 'saas' ? 7 : 14) + ')', children: _jsx("input", { type: "number", min: 0, max: 90, value: grace, onChange: (e) => setGrace(e.target.value) }) })] }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: notes, onChange: (e) => setNotes(e.target.value) }) }), _jsx(Err, { text: err }), _jsx("p", { className: "muted small", children: kind === 'saas' ? 'The customer is licensed as soon as you save.' : 'After saving, create a licence key for the customer, or a licence file for an offline site.' }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy || Object.keys(lines).length === 0, onClick: save, children: "Create subscription" }) })] }) });
}
function SubDetail({ id }) {
    const d = useLoad('/api/commerce/subscriptions/' + id, [id]);
    const [editing, setEditing] = useState(false);
    const [shown, setShown] = useState(null);
    const [fileFor, setFileFor] = useState(null);
    if (!d.data)
        return _jsx(Loading, { loading: d.loading, error: d.error });
    const s = d.data.sub;
    const a = (d.data.account ?? {});
    const [tone, label] = STATE[s.state] ?? [undefined, s.state];
    const keys = (d.data.keys ?? []);
    const licences = (d.data.licences ?? []);
    return _jsxs(_Fragment, { children: [_jsx("p", { children: _jsx("a", { href: "#/vendor", children: "\u2039 All subscriptions" }) }), _jsxs(Card, { title: (a.name ?? '') + ' · ' + (s.kind === 'saas' ? 'SaaS' : 'On-prem'), children: [_jsx(Dl, { rows: [['State', _jsx(Badge, { tone: tone, children: label }, "s")], ['Email', a.email], ['GSTIN', a.gstin], ['Customer id', s.tenantId || 'set at the first activation'], ['Plan', s.plan],
                            ['Modules', linesText(s.lines)], ['Period', fmtDay(s.startsAt) + ' to ' + fmtDay(s.endsAt) + ' (+' + s.graceDays + ' grace days)'], ['Billing', s.period], ['Notes', s.notes]] }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => setEditing(true), children: "Change" }), s.kind === 'saas'
                                ? _jsx(Act, { label: "Issue the licence again", run: async () => { await api('/api/commerce/subscriptions/' + id + '/licence', { body: {} }); d.reload(); } })
                                : _jsx("button", { onClick: () => setFileFor({ tenantId: s.tenantId ?? '', installation: '' }), children: "Licence file (offline site)" }), s.state !== 'ended' && _jsx(Act, { label: "Extend 7 days", run: async () => { await api('/api/commerce/subscriptions/' + id + '/extend', { body: { days: 7 } }); d.reload(); } }), s.state !== 'ended' && _jsx(Act, { label: "Extend 14 days", run: async () => { await api('/api/commerce/subscriptions/' + id + '/extend', { body: { days: 14 } }); d.reload(); } }), s.kind === 'saas' && (s.state === 'trial' || s.state === 'ended') && s.tenantId && _jsx(Act, { label: "Delete customer and data", danger: true, run: async () => {
                                    const typed = window.prompt('This deletes ' + s.tenantId + ' and its database. It cannot be undone. Type the customer id to confirm:');
                                    if (typed !== s.tenantId)
                                        return;
                                    await api('/api/commerce/subscriptions/' + id + '/delete-customer', { body: { confirm: typed } });
                                    d.reload();
                                } }), s.state !== 'ended' && _jsx(Act, { label: "End now", danger: true, confirm: "End this subscription now? SaaS: the customer's modules turn read-only at once. On-prem: renewals stop and the licence ends when the installation next checks.", run: async () => { await api('/api/commerce/subscriptions/' + id + '/end', { method: 'POST' }); d.reload(); } })] })] }), s.kind === 'onprem' && _jsxs(Card, { title: "Licence keys", children: [keys.length === 0 ? _jsx(Empty, { children: "No key yet. Create one and send it to the customer; they type it on their Licence page." }) : null, keys.map((k) => _jsxs("div", { className: "card", style: { marginBottom: 8 }, children: [_jsxs("p", { children: [_jsxs("b", { children: ["Key ending ", k.hint] }), " ", k.revoked ? _jsx(Badge, { tone: "bad", children: "Withdrawn" }) : _jsx(Badge, { tone: "ok", children: "In use" }), " ", _jsxs("span", { className: "muted small", children: ["for ", k.maxActivations, " installation(s), made ", fmtDay(k.createdAt)] }), !k.revoked && _jsx("span", { style: { marginLeft: 8 }, children: _jsx(Act, { label: "Withdraw", danger: true, confirm: "Withdraw this key? Installations using it stop getting renewals and it cannot be activated again.", run: async () => { await api('/api/commerce/keys/' + k.id + '/revoke', { method: 'POST' }); d.reload(); } }) })] }), k.activations.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Installation" }), _jsx("th", { children: "Customer id" }), _jsx("th", { children: "Address" }), _jsx("th", { children: "Version" }), _jsx("th", { children: "Activated" }), _jsx("th", { children: "Last check" }), _jsx("th", {})] }) }), _jsx("tbody", { children: k.activations.map((x) => _jsxs("tr", { children: [_jsx("td", { className: "small", children: _jsx("code", { children: x.installation }) }), _jsx("td", { children: x.tenantId }), _jsx("td", { className: "small", children: x.host }), _jsx("td", { className: "small", children: x.version }), _jsx("td", { children: fmtDay(x.activatedAt) }), _jsx("td", { children: x.releasedAt ? _jsxs("span", { className: "muted", children: ["released ", fmtDay(x.releasedAt)] }) : x.lastCheck ? fmtDate(x.lastCheck) : '–' }), _jsx("td", { children: !x.releasedAt && _jsx(Act, { label: "Release", confirm: "Free this installation's place so the key can be used elsewhere?", run: async () => { await api('/api/commerce/activations/' + x.id + '/release', { method: 'POST' }); d.reload(); } }) })] }, x.id)) })] })] }, k.id)), _jsx("div", { className: "actions", children: _jsx(Act, { label: "Create a licence key", primary: true, run: async () => {
                                const r = await api('/api/commerce/subscriptions/' + id + '/keys', { body: { maxActivations: 1 } });
                                d.reload();
                                setShown({ title: 'New licence key', text: String(r.key), note: 'This is the only time the whole key is shown. Send it to the customer: they type it on their Licence page (Administration › Licence).' });
                            } }) })] }), _jsx(Card, { title: "Licences issued", children: licences.length === 0 ? _jsx(Empty, { children: "None yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Issued" }), _jsx("th", { children: "Customer id" }), _jsx("th", { children: "Installation" }), _jsx("th", { children: "Ends" }), _jsx("th", { children: "Why" }), _jsx("th", { children: "By" }), _jsx("th", {})] }) }), _jsx("tbody", { children: licences.map((l) => _jsxs("tr", { children: [_jsx("td", { children: fmtDate(l.issuedAt) }), _jsx("td", { children: l.tenantId }), _jsx("td", { className: "small", children: l.installation ? _jsxs("code", { children: [l.installation.slice(0, 8), "\u2026"] }) : '–' }), _jsx("td", { children: fmtDay(l.expiresAt) }), _jsx("td", { className: "small", children: l.reason }), _jsx("td", { className: "small", children: l.issuedBy }), _jsx("td", { children: _jsx(Act, { label: "Show", run: async () => { const r = await api('/api/commerce/licences/' + l.id); setShown({ title: 'Licence', text: String(r.token), note: 'Paste this on the customer\'s Licence page (Install a new licence).' }); } }) })] }, l.id)) })] }) }), editing && _jsx(EditSub, { sub: s, onClose: () => setEditing(false), onSaved: () => { setEditing(false); d.reload(); } }), fileFor && _jsx(Modal, { title: "Licence file for an offline site", onClose: () => setFileFor(null), children: _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "The customer sees both ids on their Licence page. The licence works only on that installation." }), _jsx(Field, { label: "Customer id", children: _jsx("input", { value: fileFor.tenantId, onChange: (e) => setFileFor({ ...fileFor, tenantId: e.target.value.toLowerCase() }) }) }), _jsx(Field, { label: "Installation id", children: _jsx("input", { value: fileFor.installation, onChange: (e) => setFileFor({ ...fileFor, installation: e.target.value.trim().toLowerCase() }) }) }), _jsxs("div", { className: "actions", children: [_jsx(Act, { label: "Issue licence", primary: true, run: async () => {
                                        const r = await api('/api/commerce/subscriptions/' + id + '/licence', { body: fileFor });
                                        setFileFor(null);
                                        d.reload();
                                        setShown({ title: 'Licence file', text: String(r.token), note: 'Send this text to the customer; they paste it on their Licence page.' });
                                    } }), _jsx("button", { onClick: () => setFileFor(null), children: "Cancel" })] })] }) }), shown && _jsxs(Modal, { title: shown.title, onClose: () => setShown(null), children: [_jsx("p", { className: "muted", children: shown.note }), _jsx("textarea", { readOnly: true, rows: shown.text.length > 60 ? 6 : 1, value: shown.text, style: { width: '100%', fontFamily: 'monospace' }, onFocus: (e) => e.target.select() }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", onClick: () => copyText(shown.text), children: "Copy" }), _jsx("button", { onClick: () => setShown(null), children: "Close" })] })] })] });
}
function EditSub({ sub, onClose, onSaved }) {
    const [lines, setLines] = useState(sub.lines ?? {});
    const [endsAt, setEnds] = useState(fmtDay(sub.endsAt));
    const [grace, setGrace] = useState(String(sub.graceDays));
    const [plan, setPlan] = useState(sub.plan);
    const [state, setState] = useState(sub.state);
    const [notes, setNotes] = useState(sub.notes ?? '');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const save = async () => {
        setBusy(true);
        setErr('');
        try {
            await api('/api/commerce/subscriptions/' + sub.id, { method: 'PUT', body: { lines, endsAt, graceDays: Number(grace), plan, state, notes } });
            onSaved();
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsx(Modal, { title: "Change subscription", onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx(ModulePicker, { lines: lines, onChange: setLines }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Ends", children: _jsx("input", { type: "date", value: endsAt, onChange: (e) => setEnds(e.target.value) }) }), _jsx(Field, { label: "Grace days", children: _jsx("input", { type: "number", min: 0, max: 90, value: grace, onChange: (e) => setGrace(e.target.value) }) }), _jsx(Field, { label: "Plan name", children: _jsx("input", { value: plan, onChange: (e) => setPlan(e.target.value) }) }), _jsx(Field, { label: "State", children: _jsxs("select", { value: state, onChange: (e) => setState(e.target.value), children: [_jsx("option", { value: "active", children: "Active" }), _jsx("option", { value: "cancelled", children: "Cancelled (runs to its end)" }), _jsx("option", { value: "ended", children: "Ended" })] }) })] }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: notes, onChange: (e) => setNotes(e.target.value) }) }), _jsx("p", { className: "muted small", children: sub.kind === 'saas' ? 'The customer gets the new licence as soon as you save.' : 'Activated installations get the new licence at their next daily check (or at once with Check now on their Licence page).' }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy || Object.keys(lines).length === 0, onClick: save, children: "Save" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
function Unlinked() {
    const list = useLoad('/api/commerce/unlinked');
    const [linking, setLinking] = useState(null);
    const [name, setName] = useState('');
    const [email, setEmail] = useState('');
    const [token, setToken] = useState('');
    const [err, setErr] = useState('');
    const link = async () => {
        setErr('');
        try {
            const r = await api('/api/commerce/link', { body: { ...linking, accountName: name, email } });
            setLinking(null);
            go('vendor/sub/' + r.id);
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(_Fragment, { children: [_jsxs(Card, { title: "SaaS customers without a subscription", children: [_jsx("p", { className: "muted", children: "These customers were licensed before the store existed. Linking makes a subscription from the licence they already have; nothing changes for them, and from then on changes and renewals are made here." }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && list.data.length === 0 ? _jsx(Empty, { children: "Every customer on this platform is linked." }) : null, list.data && list.data.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Customer id" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Address" }), _jsx("th", { children: "Licence" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.data.map((t) => _jsxs("tr", { children: [_jsx("td", { children: t.tenantId }), _jsx("td", { children: t.name }), _jsx("td", { className: "small", children: t.host }), _jsx("td", { className: "small", children: t.licence ? _jsxs(_Fragment, { children: [t.licence.plan, ", ", t.licence.modules.join(', '), ", ends ", fmtDay(t.licence.expires), " (", t.licence.state, ")"] }) : _jsx("span", { className: "muted", children: "no licence" }) }), _jsx("td", { children: t.licence ? _jsx("button", { onClick: () => { setName(t.name); setEmail(''); setErr(''); setLinking({ tenantId: t.tenantId }); }, children: "Link" }) : null })] }, t.tenantId)) })] })] }), _jsx(Card, { title: "Link an on-prem customer's licence", children: _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "Paste a licence you issued earlier with the licence tool. A subscription is made from it, so you can renew it and give the customer a licence key." }), _jsx(Field, { label: "Licence", children: _jsx("textarea", { rows: 4, value: token, onChange: (e) => setToken(e.target.value), spellCheck: false }) }), _jsx("div", { className: "actions", children: _jsx("button", { disabled: token.trim().length < 50, onClick: () => { setName(''); setEmail(''); setErr(''); setLinking({ token: token.trim() }); }, children: "Link\u2026" }) })] }) }), linking && _jsx(Modal, { title: "Link to a subscription", onClose: () => setLinking(null), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Company name", children: _jsx("input", { value: name, onChange: (e) => setName(e.target.value) }) }), _jsx(Field, { label: "Email", children: _jsx("input", { type: "email", value: email, onChange: (e) => setEmail(e.target.value) }) }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", onClick: link, children: "Link" }), _jsx("button", { onClick: () => setLinking(null), children: "Cancel" })] })] }) })] });
}
function Prices() {
    const plans = useLoad('/api/commerce/plans');
    const prods = useLoad('/api/commerce/products');
    const [addSize, setAddSize] = useState(null);
    const names = useMemo(() => Object.fromEntries((prods.data ?? []).map((p) => [String(p.key), String(p.name)])), [prods.data]);
    const modules = useMemo(() => [...new Set([...(prods.data ?? []).map((p) => String(p.key)), ...(plans.data ?? []).map((p) => String(p.module))])], [prods.data, plans.data]);
    return _jsxs(_Fragment, { children: [_jsx(Products, { prods: prods, onChanged: () => { prods.reload(); plans.reload(); } }), _jsxs(Card, { title: "Price list", children: [_jsx("p", { className: "muted", children: "Yearly and monthly prices for SaaS, the yearly on-prem price, and the India price in rupees before GST. The store (release 6.1) sells from this list." }), _jsx(Loading, { loading: plans.loading && !plans.data, error: plans.error }), plans.data && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Module" }), _jsx("th", { children: "Size" }), _jsx("th", { children: "SaaS yearly (USD)" }), _jsx("th", { children: "SaaS monthly (USD)" }), _jsx("th", { children: "On-prem yearly (USD)" }), _jsx("th", { children: "India yearly (\u20B9)" }), _jsx("th", { children: "On sale" }), _jsx("th", {})] }) }), _jsx("tbody", { children: modules.flatMap((m) => (plans.data ?? []).filter((p) => p.module === m).map((p) => _jsx(PriceRow, { name: names[m] ?? m, plan: p, saved: plans.reload }, p.id))) })] }), _jsx("div", { className: "tools", children: _jsx("button", { onClick: () => setAddSize({ module: modules[0] ?? '' }), disabled: modules.length === 0, children: "Add a size" }) }), addSize && _jsx(AddSize, { modules: modules, names: names, plans: plans.data ?? [], start: String(addSize.module), onClose: () => setAddSize(null), onSaved: () => { setAddSize(null); plans.reload(); prods.reload(); } })] })] });
}
/** The products on sale. Switching one off removes it from the store and the website at once; the website can switch products too and sees this same list. */
function Products({ prods, onChanged }) {
    const [adding, setAdding] = useState(false);
    const [err, setErr] = useState('');
    const [f, setF] = useState({ key: '', name: '', tagline: '', sizeLabel: '', year: '', inr: '' });
    const set = (id, body) => api('/api/commerce/products/' + id, { method: 'PUT', body }).then(onChanged);
    const del = (p) => { if (!confirm(`Delete ${p.name}? Its sizes and prices are removed from the store and the website. This cannot be undone.`))
        return Promise.resolve(); return api('/api/commerce/products/' + p.key, { method: 'DELETE' }).then(onChanged); };
    const add = async () => {
        setErr('');
        const n = (x) => (x === '' ? undefined : Number(x));
        try {
            await api('/api/commerce/products', { method: 'POST', body: { key: f.key, name: f.name, tagline: f.tagline, sizeLabel: f.sizeLabel, limits: {}, prices: { saas: { usd: { year: n(f.year) }, inr: { year: n(f.inr) } } } } });
            setAdding(false);
            setF({ key: '', name: '', tagline: '', sizeLabel: '', year: '', inr: '' });
            onChanged();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(Card, { title: "Products on sale", children: [_jsx("p", { className: "muted", children: "The platform keeps one list of products. Switch a product off and it leaves the store, the website and the free trial at once; customers who already have it keep it. The website can switch products too: it asks this list, so both sides always show the same." }), _jsx(Loading, { loading: prods.loading && !prods.data, error: prods.error }), prods.data && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Product" }), _jsx("th", { children: "Description" }), _jsx("th", { children: "On sale" }), _jsx("th", {})] }) }), _jsx("tbody", { children: prods.data.map((p) => _jsx(ProductRow, { p: p, save: set, remove: del }, p.key)) })] }), _jsx("div", { className: "tools", children: _jsx("button", { onClick: () => setAdding(true), children: "Add a product" }) }), adding && _jsxs(Modal, { title: "Add a product", onClose: () => setAdding(false), children: [_jsxs("div", { className: "form", children: [_jsx(Field, { label: "Code (letters and digits, for example helpdesk2)", children: _jsx("input", { value: f.key, onChange: (e) => setF({ ...f, key: e.target.value }), maxLength: 20 }) }), _jsx(Field, { label: "Name", children: _jsx("input", { value: f.name, onChange: (e) => setF({ ...f, name: e.target.value }), maxLength: 60 }) }), _jsx(Field, { label: "Description", children: _jsx("input", { value: f.tagline, onChange: (e) => setF({ ...f, tagline: e.target.value }), maxLength: 200 }) }), _jsx(Field, { label: "First size, for example Up to 10 users", children: _jsx("input", { value: f.sizeLabel, onChange: (e) => setF({ ...f, sizeLabel: e.target.value }), maxLength: 60 }) }), _jsx(Field, { label: "SaaS yearly price (USD)", children: _jsx("input", { type: "number", min: 0, value: f.year, onChange: (e) => setF({ ...f, year: e.target.value }) }) }), _jsx(Field, { label: "India yearly price (\u20B9, before GST)", children: _jsx("input", { type: "number", min: 0, value: f.inr, onChange: (e) => setF({ ...f, inr: e.target.value }) }) }), _jsx("p", { className: "muted", children: "A product that is not part of the program yet is listed and can be sold, but customers receive it only when a release that contains it is installed." }), _jsx(Err, { text: err })] }), _jsxs("div", { className: "tools", style: { marginTop: 14 }, children: [_jsx("button", { className: "primary", onClick: add, children: "Add product" }), _jsx("button", { onClick: () => setAdding(false), children: "Cancel" })] })] })] });
}
function ProductRow({ p, save, remove }) {
    const [name, setName] = useState(String(p.name));
    const [tag, setTag] = useState(String(p.tagline ?? ''));
    return _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("input", { value: name, style: { width: 170 }, "aria-label": p.key + ' name', onChange: (e) => setName(e.target.value) }), !p.built && _jsx("div", { className: "muted", children: "Not in the program yet" })] }), _jsx("td", { children: _jsx("input", { value: tag, style: { width: 300 }, "aria-label": p.key + ' description', onChange: (e) => setTag(e.target.value) }) }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: !!p.onSale, "aria-label": name + ' on sale', onChange: (e) => void save(p.key, { onSale: e.target.checked }) }) }), _jsxs("td", { children: [_jsx(Act, { label: "Save", run: () => save(p.key, { name, tagline: tag }) }), !p.built && _jsx(Act, { label: "Delete", run: () => remove(p) })] })] });
}
/** One more size (edition) of a product: a name, what it allows (for example agents=25) and its prices. */
function AddSize({ modules, names, plans, start, onClose, onSaved }) {
    const [f, setF] = useState({ module: start, label: '', limits: '', year: '', month: '', onprem: '', inr: '' });
    const [err, setErr] = useState('');
    const sample = plans.find((p) => p.module === f.module);
    const keys = Object.keys((sample?.limits ?? {}));
    const n = (x) => (x === '' ? undefined : Number(x));
    const save = async () => {
        setErr('');
        const limits = {};
        for (const part of f.limits.split(',').map((x) => x.trim()).filter(Boolean)) {
            const [k, v] = part.split('=').map((x) => x.trim());
            if (!k || v === undefined || v === '' || Number.isNaN(Number(v))) {
                setErr('Limits look like agents=25 (several: agents=25, assets=500).');
                return;
            }
            limits[k] = Number(v);
        }
        try {
            await api('/api/commerce/plans', { method: 'POST', body: { module: f.module, sizeLabel: f.label, limits, prices: { saas: { usd: { year: n(f.year), month: n(f.month) }, inr: { year: n(f.inr) } }, onprem: { usd: { year: n(f.onprem) } } } } });
            onSaved();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(Modal, { title: "Add a size", onClose: onClose, children: [_jsxs("div", { className: "form", children: [_jsx(Field, { label: "Product", children: _jsx("select", { value: f.module, onChange: (e) => setF({ ...f, module: e.target.value }), children: modules.map((m) => _jsx("option", { value: m, children: names[m] ?? m }, m)) }) }), _jsx(Field, { label: "Size name, for example Up to 25 users", children: _jsx("input", { value: f.label, maxLength: 60, onChange: (e) => setF({ ...f, label: e.target.value }) }) }), _jsx(Field, { label: "What it allows (optional)", hint: keys.length ? `This product's other sizes use: ${keys.join(', ')}. Write them like ${keys[0]}=25.` : 'For example agents=25. Leave empty when the size has no limit.', children: _jsx("input", { value: f.limits, onChange: (e) => setF({ ...f, limits: e.target.value }), placeholder: keys.length ? keys[0] + '=25' : '' }) }), _jsx(Field, { label: "SaaS yearly price (USD)", children: _jsx("input", { type: "number", min: 0, value: f.year, onChange: (e) => setF({ ...f, year: e.target.value }) }) }), _jsx(Field, { label: "SaaS monthly price (USD)", children: _jsx("input", { type: "number", min: 0, value: f.month, onChange: (e) => setF({ ...f, month: e.target.value }) }) }), _jsx(Field, { label: "On-prem yearly price (USD)", children: _jsx("input", { type: "number", min: 0, value: f.onprem, onChange: (e) => setF({ ...f, onprem: e.target.value }) }) }), _jsx(Field, { label: "India yearly price (\u20B9, before GST)", children: _jsx("input", { type: "number", min: 0, value: f.inr, onChange: (e) => setF({ ...f, inr: e.target.value }) }) }), _jsx(Err, { text: err })] }), _jsxs("div", { className: "tools", style: { marginTop: 14 }, children: [_jsx("button", { className: "primary", onClick: () => void save(), children: "Add size" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] });
}
function PriceRow({ name, plan, saved }) {
    const pr = plan.prices ?? {};
    const [v, setV] = useState({ year: pr.saas?.usd?.year ?? '', month: pr.saas?.usd?.month ?? '', onprem: pr.onprem?.usd?.year ?? '', inr: pr.saas?.inr?.year ?? '', active: !!plan.active, label: plan.sizeLabel });
    const n = (x) => (x === '' || x == null ? undefined : Number(x));
    const save = () => api('/api/commerce/plans/' + plan.id, { method: 'PUT', body: { sizeLabel: v.label, limits: plan.limits, active: v.active,
            prices: { saas: { usd: { year: n(v.year), month: n(v.month) }, inr: { year: n(v.inr) } }, onprem: { usd: { year: n(v.onprem) } } } } }).then(saved);
    const num = (k) => _jsx("input", { type: "number", min: 0, style: { width: 100 }, "aria-label": name + ' ' + v.label + ' ' + k, value: v[k], onChange: (e) => setV({ ...v, [k]: e.target.value }) });
    return _jsxs("tr", { children: [_jsx("td", { children: name }), _jsx("td", { children: _jsx("input", { value: v.label, style: { width: 140 }, "aria-label": name + ' size name', onChange: (e) => setV({ ...v, label: e.target.value }) }) }), _jsx("td", { children: num('year') }), _jsx("td", { children: num('month') }), _jsx("td", { children: num('onprem') }), _jsx("td", { children: num('inr') }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: v.active, "aria-label": name + ' ' + v.label + ' on sale', onChange: (e) => setV({ ...v, active: e.target.checked }) }) }), _jsx("td", { children: _jsx(Act, { label: "Save", run: save }) })] });
}
function SigningKey({ status, reload }) {
    const [pem, setPem] = useState('');
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const sg = (status?.signing ?? {});
    const save = async () => {
        setErr('');
        setOk('');
        try {
            await api('/api/commerce/signing-key', { method: 'PUT', body: { pem } });
            setPem('');
            setOk('The signing key is stored. It will not be shown again.');
            reload();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(Card, { title: "Licence signing key", children: [_jsx(Dl, { rows: [['State', sg.ready ? _jsx(Badge, { tone: "ok", children: "Ready" }, "r") : _jsx(Badge, { tone: "bad", children: "Not set up" }, "r")], ['Kept in', sg.source === 'configuration' ? 'the server configuration (Commerce:SigningKeyPem)' : sg.source === 'stored' ? 'this platform, sealed' : sg.source],
                    ['Key id', sg.kid ? _jsx(Copy, { text: String(sg.kid) }, "k") : ''], ['Vendor public key', status?.vendorKey ? (status?.vendorKeySource === 'configuration' ? 'From the configuration' : 'Built in') : 'Missing']] }), status?.vendorKeyProblem ? _jsx("p", { role: "alert", className: "err", children: String(status.vendorKeyProblem) }) : null, sg.source !== 'configuration' && _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "Paste the private key made with the licence tool (vendor-private.pem). It must belong to the vendor public key every installation trusts. It is sealed on this server and never shown again; keep your own offline copy in a safe place." }), _jsx(Field, { label: "Private key", children: _jsx("textarea", { rows: 6, value: pem, onChange: (e) => setPem(e.target.value), spellCheck: false, placeholder: "-----BEGIN PRIVATE KEY-----", autoComplete: "off" }) }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: pem.trim().length < 50, onClick: save, children: sg.ready ? 'Replace the key' : 'Store the key' }) })] })] });
}
const ORDER = { pending: [undefined, 'Waiting for payment'], paid: ['warn', 'Paid'], fulfilling: ['warn', 'Setting up'], fulfilled: ['ok', 'Done'], failed: ['bad', 'Needs attention'] };
const cash = (v, c) => (c === 'INR' ? '₹' + v.toLocaleString('en-IN', { maximumFractionDigits: 2 }) : '$' + v.toLocaleString('en-US', { maximumFractionDigits: 2 }));
function Orders() {
    const [status, setStatus] = useState('');
    const list = useLoad('/api/commerce/orders?status=' + status, [status]);
    return _jsxs(Card, { children: [_jsx("div", { className: "form", children: _jsx(Field, { label: "Show", children: _jsxs("select", { value: status, onChange: (e) => setStatus(e.target.value), children: [_jsx("option", { value: "", children: "All orders" }), _jsx("option", { value: "failed", children: "Needs attention" }), _jsx("option", { value: "fulfilled", children: "Done" }), _jsx("option", { value: "pending", children: "Waiting for payment" })] }) }) }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && list.data.length === 0 ? _jsx(Empty, { children: "No orders yet. Orders from the store (#/store) appear here." }) : null, list.data && list.data.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Date" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Type" }), _jsx("th", { children: "Total" }), _jsx("th", { children: "State" }), _jsx("th", { children: "Invoice" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.data.map((o) => {
                            const [tone, label] = ORDER[o.status] ?? [undefined, o.status];
                            return _jsxs("tr", { children: [_jsx("td", { children: fmtDate(o.createdAt) }), _jsxs("td", { children: [_jsx("b", { children: o.company }), _jsx("div", { className: "small muted", children: o.email })] }), _jsx("td", { children: o.kind === 'saas' ? 'SaaS ' + o.tenantId : 'On-prem' }), _jsx("td", { children: cash(o.total, o.currency) }), _jsxs("td", { children: [_jsx(Badge, { tone: tone, children: label }), o.error && _jsx("div", { className: "small err", children: o.error })] }), _jsx("td", { children: o.invoiceId ? _jsx("a", { href: '/api/commerce/invoices/' + o.invoiceId + '/pdf', onClick: (e) => { e.preventDefault(); void download(o.invoiceId, o.invoice); }, children: o.invoice }) : '' }), _jsxs("td", { children: [o.subscriptionId && _jsx("a", { href: '#/vendor/sub/' + o.subscriptionId, children: "Subscription" }), o.status === 'failed' && _jsx(Act, { label: "Try again", run: async () => { await api('/api/commerce/orders/' + o.id + '/retry', { method: 'POST' }); list.reload(); } })] })] }, o.id);
                        }) })] })] });
}
async function download(id, number) {
    const { accessToken } = await import('../auth');
    const r = await fetch('/api/commerce/invoices/' + id + '/pdf', { headers: { Authorization: 'Bearer ' + (accessToken() ?? '') } });
    if (!r.ok)
        return;
    const url = URL.createObjectURL(await r.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = 'Invoice-' + number.replaceAll('/', '-') + '.pdf';
    a.click();
    URL.revokeObjectURL(url);
}
function InvoiceList() {
    const list = useLoad('/api/commerce/invoices');
    return _jsxs(Card, { title: "Invoices", children: [_jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && list.data.length === 0 ? _jsx(Empty, { children: "No invoices yet." }) : null, list.data && list.data.length > 0 && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Number" }), _jsx("th", { children: "Date" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "GSTIN" }), _jsx("th", { children: "Taxable" }), _jsx("th", { children: "Tax" }), _jsx("th", { children: "Total" })] }) }), _jsx("tbody", { children: list.data.map((i) => _jsxs("tr", { children: [_jsx("td", { children: _jsx("a", { href: "#", onClick: (e) => { e.preventDefault(); void download(i.id, i.number); }, children: i.number }) }), _jsx("td", { children: fmtDay(i.issuedAt) }), _jsx("td", { children: i.company }), _jsx("td", { className: "small", children: i.gstin }), _jsx("td", { children: cash(i.subtotal, i.currency) }), _jsx("td", { children: cash(i.tax, i.currency) }), _jsx("td", { children: _jsx("b", { children: cash(i.total, i.currency) }) })] }, i.id)) })] })] });
}
const BIZ_FIELDS = [['legalName', 'Legal name'], ['tradeName', 'Brand name'], ['address', 'Address'], ['city', 'City'], ['state', 'State'], ['pincode', 'PIN code'], ['country', 'Country'],
    ['gstin', 'GSTIN'], ['cin', 'CIN'], ['pan', 'PAN'], ['sac', 'SAC code', 'number'], ['lut', 'LUT reference (exports)'], ['supportEmail', 'Support email'], ['supportPhone', 'Support phone'],
    ['grievanceName', 'Grievance officer'], ['grievanceDesignation', 'Designation'], ['grievanceEmail', 'Grievance email'], ['invoicePrefix', 'Invoice prefix'],
    ['termsUrl', 'Terms of Service link'], ['privacyUrl', 'Privacy Policy link'], ['refundUrl', 'Cancellation and Refund link'], ['deliveryUrl', 'Delivery policy link'], ['tenantDomain', 'Domain for customer addresses']];
/** Vendor console › Website link: the company website that sells through this platform, its shared secret, and the events sent to it. */
function WebsiteLink() {
    const l = useLoad('/api/commerce/link');
    const [url, setUrl] = useState(null);
    const [hide, setHide] = useState(null);
    const [host, setHost] = useState(null);
    const [epath, setEpath] = useState(null);
    const [secret, setSecret] = useState('');
    const [test, setTest] = useState(null);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const d = l.data;
    if (!d)
        return _jsx(Loading, { loading: l.loading, error: l.error });
    const u = url ?? String(d.websiteUrl ?? '');
    const h = hide ?? !!d.hidePlatformStore;
    const wh = host ?? String(d.websiteHost ?? '');
    const ep = epath ?? String(d.eventPath ?? '');
    const save = async () => { setErr(''); setOk(''); try {
        await api('/api/commerce/link', { method: 'PUT', body: { websiteUrl: u.trim(), hidePlatformStore: h, websiteHost: wh.trim(), eventPath: ep.trim() } });
        setUrl(null);
        setHide(null);
        setHost(null);
        setEpath(null);
        l.reload();
        setOk('Saved.');
    }
    catch (e) {
        setErr(e.message);
    } };
    const make = async () => {
        if (d.hasSecret && !confirm('Make a new secret? The website stops working with the platform until you paste the new one into its Admin › Store.'))
            return;
        setErr('');
        try {
            const r = await api('/api/commerce/link/secret', { method: 'POST', body: {} });
            setSecret(String(r.secret));
            l.reload();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    const ping = async () => { setTest(null); try {
        setTest(await api('/api/commerce/link/test', { method: 'POST', body: {} }));
        l.reload();
    }
    catch (e) {
        setTest({ ok: false, detail: e.message });
    } };
    const recent = (d.recent ?? []);
    return _jsxs(_Fragment, { children: [_jsxs(Card, { title: "Website link", children: [_jsx("p", { className: "muted", children: "The company website sells through this platform: it shows the prices and the checkout, and the platform takes the payment, creates the customer, issues the licence and the invoice, then tells the website. See docs/website-link.md." }), _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Website address", children: _jsx("input", { value: u, onChange: (e) => setUrl(e.target.value), placeholder: "https://www.deskask.in" }) }), _jsx(Field, { label: "Connect to (optional)", hint: "Only when this server cannot reach the website's public address through the firewall: the website server's internal address, for example 10.10.10.150. The name in the website address is still used for the certificate.", children: _jsx("input", { value: wh, onChange: (e) => setHost(e.target.value), placeholder: "10.10.10.150" }) }), _jsx(Field, { label: "Event path (optional)", hint: "Where the website takes events. Empty uses /store/hooks.", children: _jsx("input", { value: ep, onChange: (e) => setEpath(e.target.value), placeholder: "/store/hooks" }) }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: h, disabled: !u.trim(), onChange: (e) => setHide(e.target.checked) }), " Send visitors of the platform's own store page (#/store) to the website"] }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", onClick: () => void save(), children: "Save" }) })] }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok })] }), _jsxs(Card, { title: "Shared secret", children: [_jsxs("p", { className: "muted", children: [d.hasSecret ? _jsxs(_Fragment, { children: ["A secret is set (ends \u2026", String(d.secretHint), ", made ", d.secretAt ? fmtDate(String(d.secretAt)) : '', ")."] }) : 'No secret yet.', " Both sides sign every request with it. Paste it into the website's Admin \u203A Store."] }), secret && _jsxs("div", { className: "secret-once", children: [_jsx("p", { children: _jsx("b", { children: "Copy it now; it is not shown again." }) }), _jsx(Copy, { text: secret })] }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { onClick: () => void make(), children: d.hasSecret ? 'Make a new secret' : 'Make the secret' }), _jsx("button", { disabled: !d.hasSecret || !d.websiteUrl, onClick: () => void ping(), children: "Send a test event" })] }), test && _jsx("p", { role: "status", className: test.ok ? 'ok-note' : 'err', children: String(test.detail) }), _jsxs("p", { className: "muted small", children: ["The website receives events at ", _jsxs("code", { children: [String(d.websiteUrl || 'https://www.deskask.in'), String(d.hookPath)] }), "."] })] }), _jsx(Card, { title: "Events sent to the website", children: recent.length === 0 ? _jsx(Empty, { children: "None yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Event" }), _jsx("th", { children: "Delivered" }), _jsx("th", { children: "Last problem" })] }) }), _jsx("tbody", { children: recent.map((e) => _jsxs("tr", { children: [_jsx("td", { children: fmtDate(String(e.created)) }), _jsx("td", { className: "mono", children: e.type }), _jsx("td", { children: e.delivered ? _jsx(Badge, { tone: "ok", children: "Yes" }) : _jsx(Badge, { tone: "warn", children: `Retrying (${e.attempts})` }) }), _jsx("td", { className: "small", children: e.error })] }, String(e.id))) })] }) })] });
}
function WebsitePages({ slug }) {
    const list = useLoad('/api/commerce/pages');
    const [text, setText] = useState(null);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const pages = list.data ?? [];
    const cur = pages.find((p) => p.slug === slug) ?? pages[0];
    useEffect(() => { setText(null); setOk(''); setErr(''); }, [cur?.slug]);
    if (!cur)
        return _jsx(Loading, { loading: list.loading, error: list.error });
    const value = text ?? String(cur.markdown ?? '');
    const save = async (md) => { setErr(''); setOk(''); try {
        await api('/api/commerce/pages/' + cur.slug, { method: 'PUT', body: { markdown: md } });
        setText(null);
        list.reload();
        setOk(md ? 'Saved. The website shows it now.' : 'Back to the draft built into the release.');
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(Card, { title: "Website pages", children: [_jsxs("p", { className: "muted", children: ["The text pages of the store website. Each starts as the draft from the legal review document; edit it here when your lawyer sends changes. Write in Markdown: ", _jsx("code", { children: "### Heading" }), ", ", _jsx("code", { children: "**bold**" }), ", lists with ", _jsx("code", { children: "-" }), " or ", _jsx("code", { children: "1." }), ", and tables with ", _jsx("code", { children: "|" }), "."] }), _jsx("nav", { className: "sub", "aria-label": "Pages", children: pages.map((p) => _jsxs("a", { href: '#/vendor/pages/' + p.slug, className: p.slug === cur.slug ? 'on' : '', children: [p.title, p.edited ? '' : ' (draft)'] }, p.slug)) }), _jsx(Field, { label: cur.title, children: _jsx("textarea", { rows: 24, value: value, onChange: (e) => setText(e.target.value), style: { width: '100%', fontFamily: 'ui-monospace, Consolas, monospace', fontSize: 13 } }) }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: text == null, onClick: () => void save(value), children: "Save page" }), _jsx("a", { className: "btn", href: '#/store/' + cur.slug, target: "_blank", rel: "noreferrer", children: "View on the website" }), cur.edited && _jsx("button", { onClick: () => void save(''), children: "Go back to the draft" })] }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok })] });
}
function BusinessDetails() {
    const d = useLoad('/api/commerce/business');
    const [v, setV] = useState(null);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    useEffect(() => { if (d.data && !v)
        setV(d.data); }, [d.data, v]);
    if (!v)
        return _jsx(Loading, { loading: d.loading, error: d.error });
    const save = async () => { setErr(''); setOk(''); try {
        setV(await api('/api/commerce/business', { method: 'PUT', body: v }));
        setOk('Saved. New invoices and the store pages use these details.');
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(Card, { title: "Business details", children: [_jsx("p", { className: "muted", children: "Shown on invoices and in the store. Change the address here once your registrations are updated; invoices already issued keep the details they were made with." }), _jsxs("div", { className: "form", children: [_jsxs("div", { className: "row", children: [BIZ_FIELDS.map(([k, label]) => _jsx(Field, { label: label, children: _jsx("input", { value: v[k] ?? '', onChange: (e) => setV({ ...v, [k]: e.target.value }) }) }, k)), _jsx(Field, { label: "Trial days", children: _jsx("input", { type: "number", min: 1, max: 90, value: v.trialDays ?? 14, onChange: (e) => setV({ ...v, trialDays: Number(e.target.value) }) }) }), _jsx(Field, { label: "Keep unconverted trials (days)", children: _jsx("input", { type: "number", min: 8, max: 365, value: v.trialKeepDays ?? 45, onChange: (e) => setV({ ...v, trialKeepDays: Number(e.target.value) }) }) })] }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", onClick: () => void save(), children: "Save" }) })] })] });
}
function Payments() {
    const d = useLoad('/api/commerce/payments');
    const [keyId, setKeyId] = useState('');
    const [secret, setSecret] = useState('');
    const [hook, setHook] = useState('');
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const rz = (d.data?.razorpay ?? {});
    useEffect(() => { if (rz.keyId)
        setKeyId(String(rz.keyId)); }, [rz.keyId]);
    const save = async () => { setErr(''); setOk(''); try {
        await api('/api/commerce/payments', { method: 'PUT', body: { keyId: keyId.trim(), keySecret: secret, webhookSecret: hook } });
        setSecret('');
        setHook('');
        setOk('Saved.');
        d.reload();
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs(Card, { title: "Razorpay", children: [_jsx(Dl, { rows: [['Mode', rz.mode === 'live' ? _jsx(Badge, { tone: "ok", children: "Live" }, "m") : rz.mode === 'test' ? _jsx(Badge, { tone: "warn", children: "Test (no real money)" }, "m") : _jsx(Badge, { children: "Off" }, "m")],
                    ['Key secret', rz.hasSecret ? 'Saved' : 'Not saved'], ['Webhook secret', rz.hasWebhookSecret ? 'Saved' : 'Not saved'], ['Webhook address', location.origin + (rz.webhookPath ?? '/api/store/webhooks/razorpay')]] }), _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "In the Razorpay dashboard: Account & Settings \u203A API keys for the key id and secret; Webhooks \u203A Add new webhook with the address above, events payment.captured and order.paid, and a secret you choose. Secrets are sealed and never shown again; leave a box empty to keep the saved one." }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Key id", children: _jsx("input", { value: keyId, onChange: (e) => setKeyId(e.target.value), placeholder: "rzp_test_\u2026", spellCheck: false }) }), _jsx(Field, { label: "Key secret", children: _jsx("input", { type: "password", value: secret, onChange: (e) => setSecret(e.target.value), autoComplete: "off", placeholder: rz.hasSecret ? 'saved' : '' }) }), _jsx(Field, { label: "Webhook secret", children: _jsx("input", { type: "password", value: hook, onChange: (e) => setHook(e.target.value), autoComplete: "off", placeholder: rz.hasWebhookSecret ? 'saved' : '' }) })] }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", onClick: () => void save(), children: "Save" }), _jsx("a", { href: "#/store", target: "_blank", rel: "noreferrer", children: "Open the store" })] })] })] });
}
