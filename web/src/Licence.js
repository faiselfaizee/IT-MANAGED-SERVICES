import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from './http';
import { Act, Badge, Card, Copy, Err, Field, Loading, fmtDate, fmtDay } from './ui';
const NAMES = { servicedesk: 'Service Desk', assets: 'Asset management', monitoring: 'Monitoring', adsuite: 'AD Manager', vault: 'Vault', backup: 'Backup' };
/** What each limit counts, by module and limit name (as written in the licence). */
const LIMITS = {
    assets: { max_assets: 'Assets in service', customer_write: 'Customer administrators may change data' }, monitoring: { max_devices: 'Monitored devices' },
    servicedesk: { agents: 'Staff accounts (administrators and engineers)' }, adsuite: { max_domains: 'AD domains' }, vault: { max_users: 'Vault users' }, backup: { max_devices: 'Devices with file backup' },
};
const STATE = {
    valid: ['Valid', 'ok'], grace: ['Expired, in grace period', 'warn'], expired: ['Expired', 'bad'], missing: ['No licence installed', 'bad'], invalid: ['Not valid', 'bad'], notyetvalid: ['Not valid yet', 'warn'],
};
/** Admin page: what the licence covers, and where to paste a new one. */
export default function Licence() {
    const lic = useLoad('/api/platform/license');
    const usage = useLoad('/api/platform/license/usage');
    const act = useLoad('/api/platform/license/activation');
    const [token, setToken] = useState('');
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const [busy, setBusy] = useState(false);
    const [key, setKey] = useState('');
    const [keyErr, setKeyErr] = useState('');
    const [keyOk, setKeyOk] = useState('');
    const a = act.data?.activation;
    const activate = async () => {
        setBusy(true);
        setKeyErr('');
        setKeyOk('');
        try {
            await api('/api/platform/license/activate', { body: { key: key.trim() } });
            setKey('');
            setKeyOk('Activated. Renewals and changes now arrive by themselves.');
            lic.reload();
            act.reload();
            usage.reload();
        }
        catch (e) {
            setKeyErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const l = lic.data;
    const [label, tone] = STATE[l?.state ?? ''] ?? [l?.state ?? '', undefined];
    const mods = (l?.modules ?? []);
    const install = async () => {
        setBusy(true);
        setErr('');
        setOk('');
        try {
            await api('/api/platform/license', { method: 'PUT', body: { token: token.trim() } });
            setToken('');
            setOk('The licence is installed.');
            lic.reload();
        }
        catch (e) {
            const b = e.body;
            setErr(b?.reason ? String(b.reason) : e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Licence" }), _jsx(Loading, { loading: lic.loading && !l, error: lic.error }), l && _jsxs(Card, { children: [_jsxs("p", { children: [_jsx(Badge, { tone: tone, children: label }), " ", l.plan ? _jsx("strong", { style: { marginLeft: 8 }, children: l.plan }) : null] }), l.reason && l.state !== 'valid' && _jsx("p", { className: "muted", children: l.reason }), l.expires && _jsxs("p", { className: "muted", children: ["Valid until ", fmtDay(l.expires), l.state === 'grace' ? `. Modules stop on ${fmtDay(l.graceUntil)}.` : '.'] }), _jsxs("p", { className: "muted", children: ["Customer id: ", l.tenant] })] }), l?.vendorKey?.problem ? _jsx("p", { role: "alert", className: "err", children: String(l.vendorKey.problem) }) : null, l && (l.readOnly ?? []).length > 0 && _jsx("p", { role: "alert", className: "err", children: "The licence has ended. The modules stay visible with all their data, but nothing can be changed and their background work (monitoring, backups, reminders) has stopped. Renew the licence to switch them back on." }), l && _jsx(Card, { title: "Included modules", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Module" }), _jsx("th", { children: "Included" }), _jsx("th", { children: "Used / allowed" })] }) }), _jsx("tbody", { children: Object.keys(NAMES).map((k) => {
                                const m = mods.find((x) => x.key === k);
                                const lims = (m?.limits ?? {});
                                const used = usage.data ?? {};
                                return _jsxs("tr", { children: [_jsx("td", { children: NAMES[k] }), _jsx("td", { children: m ? _jsx(Badge, { tone: "ok", children: "Yes" }) : _jsx(Badge, { children: "No" }) }), _jsx("td", { children: !m ? null : Object.keys(LIMITS[k] ?? {}).map((name) => {
                                                const allowed = lims[name];
                                                const u = used[`${k}:${name}`];
                                                if (name === 'customer_write')
                                                    return allowed ? _jsx("div", { className: "small", children: LIMITS[k][name] }, name) : null;
                                                const full = allowed != null && u != null && u >= allowed;
                                                return _jsxs("div", { className: "small", children: [LIMITS[k][name], ": ", u != null ? _jsx("b", { className: full ? 'err' : '', style: { margin: 0 }, children: u }) : '–', " / ", allowed != null ? allowed : 'no limit', full ? ' (limit reached)' : ''] }, name);
                                            }) })] }, k);
                            }) })] }) }), _jsxs(Card, { title: "Licence key (online activation)", children: [a?.activated ? _jsxs(_Fragment, { children: [_jsxs("p", { children: [_jsx(Badge, { tone: "ok", children: "Activated" }), " ", _jsxs("span", { className: "muted", children: ["Key ending ", _jsx("b", { children: a.keyHint }), ", activated ", fmtDay(a.activatedAt), "."] })] }), _jsxs("p", { className: "muted", children: ["This installation asks the licence server once a day for a newer licence, so renewals, upgrades and changes arrive by themselves. Last check: ", a.lastCheck ? fmtDate(a.lastCheck) : 'not yet', a.lastResult ? ' (' + a.lastResult + ')' : '', "."] }), _jsxs("div", { className: "actions", children: [_jsx(Act, { label: "Check now", primary: true, run: async () => { await api('/api/platform/license/refresh', { method: 'POST' }); lic.reload(); act.reload(); } }), _jsx(Act, { label: "Release this installation", danger: true, confirm: "Release the licence key from this installation? Use this before moving DeskAsk to another server. The current licence keeps working until it ends, but renewals stop arriving here.", run: async () => { await api('/api/platform/license/release', { method: 'POST' }); act.reload(); } })] })] }) : _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "Type the licence key from your order (DKA-\u2026). The installation fetches its licence from the licence server and keeps it up to date." }), _jsx(Field, { label: "Licence key", children: _jsx("input", { value: key, onChange: (e) => setKey(e.target.value), placeholder: "DKA-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX", spellCheck: false, autoComplete: "off" }) }), _jsx(Err, { text: keyErr }), keyOk && _jsx("p", { role: "status", children: keyOk }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy || key.trim().length < 10, onClick: activate, children: "Activate" }) })] }), act.data?.installation && _jsxs("p", { className: "muted small", children: ["No internet on this server? Give your supplier the customer id ", _jsx(Copy, { text: String(l?.tenant ?? '') }), " and installation id ", _jsx(Copy, { text: String(act.data.installation) }), "; they can send a licence file to paste below."] })] }), _jsx(Card, { title: "Install a new licence", children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Licence token", children: _jsx("textarea", { rows: 5, value: token, onChange: (e) => setToken(e.target.value), spellCheck: false, placeholder: "Paste the whole licence text here" }) }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy || !token.trim(), onClick: install, children: "Install licence" }) })] }) })] });
}
