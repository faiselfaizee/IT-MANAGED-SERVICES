import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDate, useSubmit } from '../ui';
import { JobView } from './DetailOps';
/** Everything that was run remotely, across all devices. */
export function RemoteLog() {
    const [days, setDays] = useState(30);
    const { data, error, loading } = useLoad(`/api/assets/remote-log?days=${days}`);
    const [job, setJob] = useState(null);
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Remote activity" }), _jsx("div", { className: "tools", children: _jsx("select", { value: days, onChange: (e) => setDays(Number(e.target.value)), "aria-label": "Period", children: [1, 7, 30, 90, 365].map((d) => _jsxs("option", { value: d, children: ["Last ", d, " day", d > 1 ? 's' : ''] }, d)) }) }), _jsx(Loading, { loading: loading && !data, error: error }), data && (data.items.length === 0 ? _jsx(Empty, { children: "Nothing was run in this period." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Device" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "What" }), _jsx("th", { children: "By" }), _jsx("th", { children: "Result" }), _jsx("th", {})] }) }), _jsx("tbody", { children: data.items.map((r) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: fmtDate(r.at) }), _jsx("td", { children: _jsx("a", { href: `#/assets/${r.assetId}/commands`, children: r.asset }) }), _jsx("td", { children: r.company }), _jsxs("td", { children: [r.title, _jsx("div", { className: "muted mono", style: { maxHeight: 60, overflow: 'hidden' }, children: r.what })] }), _jsx("td", { children: r.who }), _jsx("td", { children: _jsx(Badge, { tone: r.status === 'done' && !r.exit ? 'ok' : undefined, children: r.status }) }), _jsx("td", { children: _jsx("button", { onClick: () => setJob(r.id), children: "Output" }) })] }, r.id)) })] })), job && _jsx(JobView, { jid: job, onClose: () => setJob(null) })] });
}
/** The troubleshooting command repository (what the Commands tab offers). */
export function Repository() {
    const [q, setQ] = useState('');
    const { data, error, loading, reload } = useLoad(`/api/assets/commands?q=${encodeURIComponent(q)}`);
    const [edit, setEdit] = useState(null);
    const blank = { title: '', description: '', category: 'General', os: 'windows', shell: 'powershell', body: '', risk: 'safe', params: [], timeout: 120 };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Troubleshooting command repository" }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Search commands", value: q, onChange: (e) => setQ(e.target.value) }), _jsx("button", { className: "primary", onClick: () => setEdit({ ...blank }), children: "Add command" }), _jsxs("span", { className: "muted", children: [data?.count ?? 0, " commands. Use ", '{{name}}', " in the text for values asked when it runs."] })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Command" }), _jsx("th", { children: "System" }), _jsx("th", { children: "Shell" }), _jsx("th", { children: "Risk" }), _jsx("th", {})] }) }), _jsx("tbody", { children: data.items.map((c) => _jsxs("tr", { style: c.enabled ? undefined : { opacity: 0.5 }, children: [_jsxs("td", { children: [_jsx("b", { children: c.title }), " ", c.builtin && _jsx(Badge, { children: "built in" }), _jsxs("div", { className: "muted", children: [c.category, " \u00B7 ", c.description] })] }), _jsx("td", { children: c.os }), _jsx("td", { children: c.shell }), _jsx("td", { children: _jsx(Badge, { tone: c.risk === 'safe' ? 'ok' : c.risk === 'caution' ? 'warn' : 'bad', children: c.risk }) }), _jsxs("td", { children: [_jsx("button", { onClick: () => setEdit(c), children: "Edit" }), " ", _jsx(Act, { label: c.enabled ? (c.builtin ? 'Disable' : 'Delete') : 'Enable', danger: c.enabled, run: async () => { if (c.enabled)
                                                await api(`/api/assets/commands/${c.id}`, { method: 'DELETE' });
                                            else
                                                await api(`/api/assets/commands/${c.id}`, { method: 'PUT', body: { enabled: true } }); reload(); } })] })] }, c.id)) })] }), edit && _jsx(CommandForm, { init: edit, onClose: (ch) => { setEdit(null); if (ch)
                    reload(); } })] });
}
function CommandForm({ init, onClose }) {
    const [v, setV] = useState({ ...init, paramsText: JSON.stringify(init.params ?? [], null, 1) });
    const f = useSubmit(() => onClose(true));
    const set = (k, x) => setV({ ...v, [k]: x });
    const [err, setErr] = useState('');
    const save = () => f.submit(async () => {
        let params;
        try {
            params = JSON.parse(v.paramsText || '[]');
        }
        catch {
            throw new Error('The parameters must be a JSON list, for example [{"name":"host","label":"Host","default":"8.8.8.8"}].');
        }
        return api(init.id ? `/api/assets/commands/${init.id}` : '/api/assets/commands', { method: init.id ? 'PUT' : 'POST', body: { title: v.title, description: v.description, category: v.category, os: v.os, shell: v.shell, body: v.body, risk: v.risk, params, timeout: Number(v.timeout) || 120 } });
    });
    return _jsx(Modal, { title: init.id ? 'Edit command' : 'Add command', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Title", children: _jsx("input", { value: v.title, onChange: (e) => set('title', e.target.value) }) }), _jsx(Field, { label: "Description", children: _jsx("input", { value: v.description, onChange: (e) => set('description', e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Category", children: _jsx("input", { value: v.category, onChange: (e) => set('category', e.target.value) }) }), _jsx(Field, { label: "System", children: _jsxs("select", { value: v.os, onChange: (e) => set('os', e.target.value), children: [_jsx("option", { children: "windows" }), _jsx("option", { children: "linux" }), _jsx("option", { children: "any" })] }) }), _jsx(Field, { label: "Shell", children: _jsxs("select", { value: v.shell, onChange: (e) => set('shell', e.target.value), children: [_jsx("option", { children: "cmd" }), _jsx("option", { children: "powershell" }), _jsx("option", { children: "bash" })] }) }), _jsx(Field, { label: "Risk", children: _jsxs("select", { value: v.risk, onChange: (e) => set('risk', e.target.value), children: [_jsx("option", { children: "safe" }), _jsx("option", { children: "caution" }), _jsx("option", { children: "disruptive" })] }) })] }), _jsx(Field, { label: "Command", children: _jsx("textarea", { rows: 5, value: v.body, onChange: (e) => set('body', e.target.value) }) }), _jsx(Field, { label: "Parameters (JSON)", children: _jsx("textarea", { rows: 4, value: v.paramsText, onChange: (e) => set('paramsText', e.target.value) }) }), _jsx(Err, { text: err || f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !v.title.trim() || !v.body.trim(), onClick: () => { setErr(''); void save(); }, children: "Save" })] })] }) });
}
/** RustDesk server details used by remote control. */
export function Settings() {
    const { data, error, reload } = useLoad('/api/assets/settings/remote-control');
    const [v, setV] = useState(null);
    const [saved, setSaved] = useState(false);
    const cur = v ?? data;
    const set = (k, x) => { setSaved(false); setV({ ...cur, [k]: x }); };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Settings" }), _jsx(DeleteApprovalCard, {}), _jsx("h2", { children: "Remote control" }), _jsx(Err, { text: error }), cur && _jsx(Card, { title: "RustDesk", children: _jsxs("div", { className: "form", style: { maxWidth: 560 }, children: [_jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: !!cur.enabled, onChange: (e) => set('enabled', e.target.checked) }), " Remote control is switched on"] }), _jsx(Field, { label: "ID / rendezvous server", children: _jsx("input", { value: cur.idServer, onChange: (e) => set('idServer', e.target.value), placeholder: "rd.example.com" }) }), _jsx(Field, { label: "Relay server (optional)", children: _jsx("input", { value: cur.relay, onChange: (e) => set('relay', e.target.value) }) }), _jsx(Field, { label: "Public key (id_ed25519.pub)", children: _jsx("input", { value: cur.key, onChange: (e) => set('key', e.target.value) }) }), _jsx(Field, { label: "Installer address (https://\u2026)", children: _jsx("input", { value: cur.installer, onChange: (e) => set('installer', e.target.value) }) }), _jsx(Field, { label: "Installer SHA-256 (optional)", children: _jsx("input", { value: cur.sha256, onChange: (e) => set('sha256', e.target.value), className: "mono" }) }), _jsxs("div", { className: "actions", children: [_jsx(Act, { label: "Save", primary: true, run: async () => { await api('/api/assets/settings/remote-control', { method: 'PUT', body: cur }); setSaved(true); setV(null); reload(); } }), saved && _jsx("span", { className: "muted", children: "Saved." })] })] }) })] });
}
/** Whether deleting an asset waits for an approver (off by default). */
function DeleteApprovalCard() {
    const { data, reload } = useLoad('/api/assets/delete-approval');
    if (!data)
        return null;
    return _jsxs(Card, { title: "Deleting assets", children: [_jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: !!data.required, onChange: async (e) => { await api('/api/assets/delete-approval', { method: 'PUT', body: { required: e.target.checked } }); reload(); } }), "Deleting needs an approver"] }), _jsx("p", { className: "muted small", children: data.required
                    ? 'People who may edit assets but were not given the delete right send a request; an administrator or someone with the delete right decides (Delete requests).'
                    : 'People who may edit an asset can delete it themselves, giving a reason and typing the asset tag to confirm. Deleting is permanent; the reason is kept in the audit log.' })] });
}
