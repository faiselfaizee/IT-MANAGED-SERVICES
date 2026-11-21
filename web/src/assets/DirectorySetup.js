import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { Badge, Err, Field, Loading, Modal } from '../ui';
const when = (s) => (s ? new Date(s).toLocaleString() : 'never');
/** Devices setup › a customer › Domain controller: where DeskAsk reads the customer's people from (read only), through the controller installed in their network. */
export default function DirectorySetup({ company, onClose }) {
    const { data, error, loading, reload } = useLoad(`/api/directory/companies/${company.id}`);
    const d = data?.domain;
    const [v, setV] = useState(null);
    const [msg, setMsg] = useState('');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const form = v ?? { servers: (d?.servers ?? '').replace(/\n/g, '\n'), baseDn: d?.baseDn ?? '', bindDn: d?.bindDn ?? '', password: '', controllerId: d?.controllerId ?? (data?.controllers?.length === 1 ? data.controllers[0].id : ''), tls: d?.tls ?? true };
    const set = (k, x) => setV({ ...form, [k]: x });
    const run = async (fn, done) => {
        setBusy(true);
        setErr('');
        setMsg('');
        try {
            const r = (await fn()) ?? {};
            if (done)
                setMsg(done(r));
            setV(null);
            reload();
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const ctrls = (data?.controllers ?? []);
    return _jsx(Modal, { title: `Domain controller · ${company.name}`, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "DeskAsk reads the people (name, email, department) of this customer's domain controller, only to read, so they can be listed under Inventory \u203A People and chosen when assets are handed out. The controller you pick must be installed in the customer's network." }), _jsx(Loading, { loading: loading && !data, error: error }), data && _jsxs(_Fragment, { children: [d && _jsxs("p", { children: [d.lastError ? _jsx(Badge, { children: 'Last test failed' }) : d.lastOk ? _jsx(Badge, { tone: "ok", children: "Connected" }) : _jsx(Badge, { children: "Not tested yet" }), ' ', _jsxs("span", { className: "muted", children: [d.people, " people \u00B7 last read ", when(d.syncedAt)] })] }), d?.syncError && _jsx(Err, { text: d.syncError }), _jsx(Field, { label: "Domain controllers (one per line)", children: _jsx("textarea", { rows: 2, value: form.servers, onChange: (e) => set('servers', e.target.value), placeholder: "dc01.contoso.com" }) }), _jsx(Field, { label: "Base DN", children: _jsx("input", { value: form.baseDn, onChange: (e) => set('baseDn', e.target.value), placeholder: "DC=contoso,DC=com" }) }), _jsx(Field, { label: "Read-only account", children: _jsx("input", { value: form.bindDn, onChange: (e) => set('bindDn', e.target.value), placeholder: "svc-deskask@contoso.com" }) }), _jsx(Field, { label: d ? 'Password (leave empty to keep it)' : 'Password', children: _jsx("input", { type: "password", autoComplete: "new-password", value: form.password, onChange: (e) => set('password', e.target.value) }) }), _jsx(Field, { label: "Controller in the customer's network", children: _jsxs("select", { value: form.controllerId, onChange: (e) => set('controllerId', e.target.value), children: [_jsx("option", { value: "", children: "Choose\u2026" }), ctrls.map((c) => _jsxs("option", { value: c.id, children: [c.name, c.online ? '' : ' (offline)'] }, c.id))] }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: form.tls, onChange: (e) => set('tls', e.target.checked) }), " Use a secure connection (LDAPS)"] }), msg && _jsx("p", { role: "status", children: msg }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [d && _jsx("button", { disabled: busy, onClick: () => { if (confirm('Remove this domain controller? The people read from it are removed too.'))
                                        void run(() => api(`/api/directory/companies/${company.id}`, { method: 'DELETE' }), () => 'Removed.'); }, children: "Remove" }), d && _jsx("button", { disabled: busy, onClick: () => void run(() => api(`/api/directory/companies/${company.id}/test`, { method: 'POST', body: {} }), (r) => (r.ok ? 'Connected.' : (r.error || 'The test failed.') + (r.hint ? ' ' + r.hint : ''))), children: "Test" }), d && _jsx("button", { disabled: busy, onClick: () => void run(() => api(`/api/directory/companies/${company.id}/sync`, { method: 'POST', body: {} }), (r) => (r.ok ? `${r.count} people read.` : r.error || 'The read failed.')), children: "Sync now" }), _jsx("button", { className: "primary", disabled: busy || !form.servers.trim() || !form.baseDn.trim() || !form.bindDn.trim() || !form.controllerId || (!d && !form.password), onClick: () => void run(() => api(`/api/directory/companies/${company.id}`, { method: 'PUT', body: { servers: form.servers, baseDn: form.baseDn, bindDn: form.bindDn, password: form.password, controllerId: form.controllerId, tls: form.tls } }), () => 'Saved. Press Test, then Sync now.'), children: "Save" })] })] })] }) });
}
