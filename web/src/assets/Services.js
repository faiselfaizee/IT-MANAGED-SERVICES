import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useState } from 'react';
import { api, useLoad } from '../http';
import { Badge, Empty, Err, Field, Loading, Modal, ago } from '../ui';
import * as S from '../vault/session';
import { Unlock } from '../vault/Gate';
const STATE = {
    running: ['Running', 'ok'], stopped: ['Stopped', undefined], starting: ['Starting', 'warn'], stopping: ['Stopping', 'warn'], paused: ['Paused', 'warn'],
    failed: ['Failed', 'bad'], inactive: ['Not running', undefined], unknown: ['Unknown', undefined],
};
const START = { auto: 'Automatic', delayed: 'Automatic (delayed start)', manual: 'Manual', disabled: 'Disabled', other: 'Other' };
/** Follows an action until the agent reports back, then hands over its result. */
async function follow(assetId, jid, onStep) {
    for (let i = 0; i < 120; i++) {
        const r = await api(`/api/assets/${assetId}/actions/${jid}`);
        if (['done', 'failed', 'timeout', 'cancelled'].includes(String(r.status)))
            return r;
        onStep(r.status === 'running' ? 'The computer is working on it…' : 'Waiting for the agent…');
        await new Promise((ok) => setTimeout(ok, i < 10 ? 1500 : 3000));
    }
    throw new Error('No answer from the agent after six minutes. Check Remote activity later.');
}
/**
 * The services of a computer with the agent, as last reported, with start, stop, restart, startup type and (Windows, administrators) the
 * Log On account. Used on the asset page (Services tab) and the Monitoring device page.
 */
export function ServicesPanel({ assetId }) {
    const { data, error, loading, reload } = useLoad(`/api/assets/${assetId}/services`, [assetId]);
    const [q, setQ] = useState('');
    const [show, setShow] = useState('all');
    const [over, setOver] = useState({}); // states the computer reported after an action, until the next inventory
    const [busy, setBusy] = useState({});
    const [msg, setMsg] = useState(null);
    const [startup, setStartup] = useState(null);
    const [logon, setLogon] = useState(null);
    const win = data?.platform === 'windows';
    const items = useMemo(() => (Array.isArray(data?.items) ? data.items : []).map((s) => ({ ...s, ...(over[String(s.name)] ?? {}) }))
        .filter((s) => (q === '' || `${s.name} ${s.display ?? ''} ${s.logon ?? ''}`.toLowerCase().includes(q.toLowerCase()))
        && (show === 'all' || (show === 'running' ? s.state === 'running' : show === 'stopped-auto' ? s.state !== 'running' && (s.start === 'auto' || s.start === 'delayed') : s.state !== 'running')))
        .sort((a, b) => String(a.display || a.name).localeCompare(String(b.display || b.name))), [data, over, q, show]);
    const act = async (name, action, extra = {}) => {
        setBusy((b) => ({ ...b, [name]: action }));
        setMsg(null);
        try {
            const { id } = await api(`/api/assets/${assetId}/services/action`, { method: 'POST', body: { name, action, ...extra } });
            const r = await follow(assetId, String(id), (t) => setMsg({ name, text: t }));
            if (r.service)
                setOver((o) => ({ ...o, [name]: r.service }));
            setMsg({ name, text: String(r.output || (r.status === 'done' ? 'Done.' : 'The action did not finish.')), bad: r.status !== 'done' });
        }
        catch (e) {
            setMsg({ name, text: e.message, bad: true });
        }
        finally {
            setBusy((b) => { const n = { ...b }; delete n[name]; return n; });
        }
    };
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: "" });
    if (error)
        return _jsx(Err, { text: error });
    if (!data?.hasAgent)
        return _jsx(Empty, { children: "Services are read and controlled through the agent. Install the agent on this computer to see them." });
    const can = !!data.canControl;
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("input", { type: "search", placeholder: "Find a service", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Find a service" }), _jsxs("select", { value: show, onChange: (e) => setShow(e.target.value), "aria-label": "Show", children: [_jsx("option", { value: "all", children: "All services" }), _jsx("option", { value: "running", children: "Running" }), _jsx("option", { value: "stopped", children: "Not running" }), _jsx("option", { value: "stopped-auto", children: "Automatic but not running" })] }), _jsx("button", { type: "button", onClick: reload, children: "Refresh" }), _jsx("span", { className: "muted", children: data.at ? `Reported ${ago(String(data.at))}. The agent sends the list every few minutes.` : 'The agent has not sent its list of services yet.' })] }), msg && _jsxs("p", { role: "status", className: msg.bad ? 'err' : 'ok-note', children: [_jsxs("b", { children: [msg.name, ":"] }), " ", msg.text] }), items.length === 0 ? _jsx(Empty, { children: "No services match." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { className: "svc-table", children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Service" }), _jsx("th", { children: "State" }), _jsx("th", { children: "Startup type" }), win && _jsx("th", { children: "Log On as" }), can && _jsx("th", { children: _jsx("span", { className: "sr-only", children: "Actions" }) })] }) }), _jsx("tbody", { children: items.map((s) => {
                                const name = String(s.name);
                                const [label, tone] = STATE[String(s.state)] ?? [String(s.state), undefined];
                                const b = busy[name];
                                return _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("b", { children: s.display || name }), s.display ? _jsx("div", { className: "muted mono small", children: name }) : null] }), _jsx("td", { children: _jsx(Badge, { tone: tone, children: b ? 'Working…' : label }) }), _jsx("td", { children: START[String(s.start)] ?? s.start }), win && _jsx("td", { className: "small", children: s.logon || _jsx("span", { className: "muted", children: "unknown" }) }), can && _jsxs("td", { className: "svc-acts", children: [s.state !== 'running' && _jsx("button", { type: "button", disabled: !!b || s.start === 'disabled', title: s.start === 'disabled' ? 'A disabled service cannot start; change its startup type first.' : undefined, onClick: () => void act(name, 'start'), children: "Start" }), s.state === 'running' && _jsx("button", { type: "button", disabled: !!b, onClick: () => { if (confirm(`Stop ${s.display || name}? Programs that need it may stop working.`))
                                                        void act(name, 'stop'); }, children: "Stop" }), s.state === 'running' && _jsx("button", { type: "button", disabled: !!b, onClick: () => { if (confirm(`Restart ${s.display || name}?`))
                                                        void act(name, 'restart'); }, children: "Restart" }), _jsx("button", { type: "button", disabled: !!b, onClick: () => setStartup(s), children: "Startup type" }), data.canLogon && _jsx("button", { type: "button", disabled: !!b, onClick: () => setLogon(s), children: "Log On" })] })] }, name);
                            }) })] }) }), startup && _jsx(StartupForm, { s: startup, win: win, onClose: () => setStartup(null), onPick: (v) => { const n = String(startup.name); setStartup(null); void act(n, 'startup', { startup: v }); } }), logon && _jsx(LogonForm, { assetId: assetId, s: logon, onClose: () => setLogon(null), onDone: (text, bad, svc) => { const n = String(logon.name); setLogon(null); setMsg({ name: n, text, bad }); if (svc)
                    setOver((o) => ({ ...o, [n]: svc })); } })] });
}
function StartupForm({ s, win, onClose, onPick }) {
    const [v, setV] = useState(String(s.start === 'other' ? 'manual' : s.start));
    const opts = win ? ['auto', 'delayed', 'manual', 'disabled'] : ['auto', 'disabled'];
    return _jsx(Modal, { title: `Startup type: ${s.display || s.name}`, onClose: onClose, children: _jsxs("form", { className: "form", onSubmit: (e) => { e.preventDefault(); onPick(v); }, children: [_jsxs("fieldset", { className: "radio-list", children: [_jsx("legend", { className: "sr-only", children: "Startup type" }), opts.map((o) => _jsxs("label", { children: [_jsx("input", { type: "radio", name: "startup", value: o, checked: v === o, onChange: () => setV(o) }), " ", win ? START[o] : o === 'auto' ? 'Start at boot (enabled)' : 'Do not start at boot (disabled)'] }, o))] }), _jsxs("div", { className: "actions", children: [_jsx("button", { type: "button", onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary", type: "submit", disabled: v === s.start, children: "Change startup type" })] })] }) });
}
const BUILT_IN = [['LocalSystem', 'Local System'], ['NT AUTHORITY\\NetworkService', 'Network Service'], ['NT AUTHORITY\\LocalService', 'Local Service']];
/** Log On: a built-in account, or an account typed in or filled from the Vault, with a reason. The password is sent once and never kept. */
function LogonForm({ assetId, s, onClose, onDone }) {
    const current = String(s.logon ?? '');
    const [kind, setKind] = useState(BUILT_IN.some(([k]) => k.toLowerCase() === current.toLowerCase()) ? current : current ? 'account' : 'LocalSystem');
    const [account, setAccount] = useState(BUILT_IN.some(([k]) => k.toLowerCase() === current.toLowerCase()) ? '' : current);
    const [pw, setPw] = useState('');
    const [pw2, setPw2] = useState('');
    const [reason, setReason] = useState('');
    const [picker, setPicker] = useState(false);
    const [err, setErr] = useState('');
    const [step, setStep] = useState('');
    const isAccount = kind === 'account';
    const ready = reason.trim() !== '' && (!isAccount || (account.trim() !== '' && pw !== '' && pw === pw2));
    const send = async () => {
        setErr('');
        setStep('Sending…');
        try {
            const r = await api(`/api/assets/${assetId}/services/logon`, { method: 'POST', body: { name: s.name, account: isAccount ? account.trim() : kind, password: isAccount ? pw : '', reason } });
            setPw('');
            setPw2('');
            if (r.kind === 'job') {
                const done = await follow(assetId, String(r.id), setStep);
                onDone(String(done.output || 'Done.'), done.status !== 'done', done.service);
                return;
            }
            for (let i = 0; i < 80; i++) {
                const h = await api(`/api/assets/${assetId}/services/logon/${r.id}`);
                if (h.state === 'done') {
                    onDone(`${s.display || s.name} now runs as ${account.trim()}. Restart the service for the change to take effect.`, false, { logon: account.trim() });
                    return;
                }
                if (h.state === 'failed' || h.state === 'cancelled') {
                    setErr(String(h.error || 'The agent could not change the account.'));
                    setStep('');
                    return;
                }
                setStep(h.state === 'fetched' ? 'The computer is changing the account…' : 'Waiting for the agent…');
                await new Promise((ok) => setTimeout(ok, 2000));
            }
            setErr('No answer from the agent yet. Agents older than 6.3 cannot change a service account: update the agent and try again.');
            setStep('');
        }
        catch (e) {
            setErr(e.message);
            setStep('');
        }
    };
    return _jsxs(Modal, { title: `Log On: ${s.display || s.name}`, onClose: onClose, children: [_jsxs("form", { className: "form", onSubmit: (e) => { e.preventDefault(); if (ready && !step)
                    void send(); }, children: [_jsxs("p", { className: "muted", children: ["The account this service runs as. ", current ? _jsxs(_Fragment, { children: ["Now: ", _jsx("b", { children: current }), "."] }) : null, " The change takes effect when the service is next restarted."] }), _jsxs("fieldset", { className: "radio-list", children: [_jsx("legend", { className: "sr-only", children: "Log on as" }), BUILT_IN.map(([k, l]) => _jsxs("label", { children: [_jsx("input", { type: "radio", name: "logon", checked: kind === k, onChange: () => setKind(k) }), " ", l] }, k)), _jsxs("label", { children: [_jsx("input", { type: "radio", name: "logon", checked: isAccount, onChange: () => setKind('account') }), " This account"] })] }), isAccount && _jsxs(_Fragment, { children: [_jsx("div", { className: "actions", style: { justifyContent: 'flex-start', marginTop: 0 }, children: _jsx("button", { type: "button", onClick: () => setPicker(true), children: "Fill from Vault" }) }), _jsx(Field, { label: "Account", hint: "DOMAIN\\name, .\\name for an account on this computer, or name@domain", children: _jsx("input", { value: account, onChange: (e) => setAccount(e.target.value), autoComplete: "off", spellCheck: false }) }), _jsx(Field, { label: "Password", children: _jsx("input", { type: "password", value: pw, onChange: (e) => setPw(e.target.value), autoComplete: "new-password" }) }), _jsx(Field, { label: "Confirm the password", children: _jsx("input", { type: "password", value: pw2, onChange: (e) => setPw2(e.target.value), autoComplete: "new-password" }) }), pw2 !== '' && pw !== pw2 && _jsx("p", { className: "err small", children: "The passwords are not the same." }), _jsx("p", { className: "muted small", children: "The password goes to the computer once over the agent's encrypted connection and is not kept by DeskAsk. The account is also given the \"Log on as a service\" right." })] }), _jsx(Field, { label: "Reason", children: _jsx("input", { value: reason, onChange: (e) => setReason(e.target.value), maxLength: 300, placeholder: "For example: SQL Server must run as the new service account (CHG-0042)" }) }), _jsx(Err, { text: err }), step && _jsx("p", { role: "status", className: "muted", children: step }), _jsxs("div", { className: "actions", children: [_jsx("button", { type: "button", onClick: onClose, children: "Cancel" }), _jsx("button", { className: "primary", type: "submit", disabled: !ready || !!step, children: "Change account" })] })] }), picker && _jsx(VaultPicker, { assetId: assetId, onClose: () => setPicker(false), onPick: (u, p) => { setAccount(u); setPw(p); setPw2(p); setPicker(false); } })] });
}
/** Picks a login from the Vault: titles and secrets are decrypted in this browser; using one is recorded on the item. Items linked to this asset come first. */
function VaultPicker({ assetId, onClose, onPick }) {
    const session = S.useSession();
    const [items, setItems] = useState(null);
    const [linked, setLinked] = useState([]);
    const [q, setQ] = useState('');
    const [err, setErr] = useState('');
    useEffect(() => { if (!session.me)
        S.loadMe().catch((e) => setErr(e.message)); }, [session.me]);
    useEffect(() => {
        if (!session.unlocked)
            return;
        void (async () => {
            try {
                const all = ((await api('/api/vault/items')).items ?? []);
                const mine = ((await api(`/api/vault/assets/${assetId}/items`)).items ?? []);
                setLinked(mine.map((m) => Number(m.id)));
                setItems(all.filter((i) => i.canRead && ['login', 'server', 'database', 'network'].includes(i.type)));
            }
            catch (e) {
                setErr(e.message);
            }
        })();
    }, [session.unlocked, assetId]);
    const titles = S.useTitles(items ?? []);
    const list = (items ?? []).filter((i) => q === '' || (titles[i.id] ?? '').toLowerCase().includes(q.toLowerCase()))
        .sort((a, b) => Number(linked.includes(b.id)) - Number(linked.includes(a.id)) || (titles[a.id] ?? '').localeCompare(titles[b.id] ?? ''));
    const pick = async (it) => {
        setErr('');
        try {
            const d = await S.data(it);
            let password = d?.password ?? '';
            if (!password && it.managed)
                password = String((await api(`/api/vault/items/${it.id}/secret`)).password ?? '');
            if (!d?.username || !password) {
                setErr('This item has no user name or no password.');
                return;
            }
            S.note(it.id, 'copied', 'password (service Log On)');
            onPick(String(d.username), password);
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs(Modal, { title: "Fill from Vault", onClose: onClose, children: [!session.me ? _jsx(Loading, { loading: true, error: err }) : !session.me.setUp ? _jsx(Empty, { children: "You have not set up the Vault yet. Open Vault from the menu first." })
                : !session.unlocked ? _jsx(Unlock, { onReset: () => setErr('Reset your Vault key from the Vault itself.') })
                    : _jsxs(_Fragment, { children: [_jsx("input", { type: "search", placeholder: "Find a login", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Find a login", style: { width: '100%', marginBottom: 10 } }), items === null ? _jsx(Loading, { loading: true, error: "" }) : list.length === 0 ? _jsx(Empty, { children: "No logins you can read." })
                                : _jsx("ul", { className: "pick-list", children: list.slice(0, 100).map((i) => _jsx("li", { children: _jsxs("button", { type: "button", onClick: () => void pick(i), children: [_jsx("b", { children: titles[i.id] ?? '…' }), linked.includes(i.id) && _jsx(Badge, { tone: "ok", children: "This asset" }), _jsx("span", { className: "muted small", children: S.vaultById(i.vaultId)?.name })] }) }, i.id)) }), _jsx("p", { className: "muted small", children: "Using an item is recorded on it, as when you copy its password in the Vault." })] }), _jsx(Err, { text: err })] });
}
/** The Uninstall form: a reason, then the agent runs the program's silent uninstaller (Windows) or the package manager (Linux). */
export function UninstallForm({ assetId, app, onClose, onDone }) {
    const [reason, setReason] = useState('');
    const [err, setErr] = useState('');
    const [step, setStep] = useState('');
    const run = async () => {
        setErr('');
        setStep('Sending…');
        try {
            const { id } = await api(`/api/assets/${assetId}/apps/uninstall`, { method: 'POST', body: { name: app.name, version: app.version ?? '', reason } });
            const r = await follow(assetId, String(id), setStep);
            if (r.status === 'done')
                void api(`/api/assets/${assetId}/actions/refresh`, { method: 'POST', body: {} }).catch(() => undefined);
            onDone(String(r.output || (r.status === 'done' ? 'Uninstalled.' : 'The uninstall did not finish.')), r.status === 'done');
        }
        catch (e) {
            setErr(e.message);
            setStep('');
        }
    };
    return _jsx(Modal, { title: `Uninstall ${app.name}`, onClose: onClose, children: _jsxs("form", { className: "form", onSubmit: (e) => { e.preventDefault(); if (reason.trim() && !step)
                void run(); }, children: [_jsxs("p", { children: [app.name, app.version ? ` ${app.version}` : '', app.publisher ? _jsxs("span", { className: "muted", children: [" by ", app.publisher] }) : null, " is removed from this computer without asking the user. Some programs need a restart to finish."] }), _jsx(Field, { label: "Reason", children: _jsx("input", { value: reason, onChange: (e) => setReason(e.target.value), maxLength: 300, placeholder: "For example: not licensed, replaced by the new version", autoFocus: true }) }), _jsx(Err, { text: err }), step && _jsx("p", { role: "status", className: "muted", children: step }), _jsxs("div", { className: "actions", children: [_jsx("button", { type: "button", onClick: onClose, children: "Cancel" }), _jsx("button", { className: "danger", type: "submit", disabled: !reason.trim() || !!step, children: "Uninstall" })] })] }) });
}
/**
 * The action buttons for one service, for lists outside the asset page (the Monitoring device page). Shows its own progress and result,
 * and passes the state the computer reported afterwards to `onState`.
 */
export function ServiceControls({ assetId, s, ctl, onState }) {
    const [busy, setBusy] = useState('');
    const [msg, setMsg] = useState(null);
    const [startup, setStartup] = useState(false);
    const [logon, setLogon] = useState(false);
    const name = String(s.name);
    const win = ctl.platform === 'windows';
    if (!ctl.canControl || !ctl.hasAgent)
        return null;
    const act = async (action, extra = {}) => {
        setBusy(action);
        setMsg(null);
        try {
            const { id } = await api(`/api/assets/${assetId}/services/action`, { method: 'POST', body: { name, action, ...extra } });
            const r = await follow(assetId, String(id), (t) => setMsg({ text: t }));
            if (r.service)
                onState?.(r.service);
            setMsg({ text: String(r.output || (r.status === 'done' ? 'Done.' : 'The action did not finish.')), bad: r.status !== 'done' });
        }
        catch (e) {
            setMsg({ text: e.message, bad: true });
        }
        finally {
            setBusy('');
        }
    };
    return _jsxs("div", { className: "svc-acts", children: [s.state !== 'running' && _jsx("button", { type: "button", disabled: !!busy || s.start === 'disabled', onClick: () => void act('start'), children: "Start" }), s.state === 'running' && _jsx("button", { type: "button", disabled: !!busy, onClick: () => { if (confirm(`Stop ${s.display || name}?`))
                    void act('stop'); }, children: "Stop" }), s.state === 'running' && _jsx("button", { type: "button", disabled: !!busy, onClick: () => { if (confirm(`Restart ${s.display || name}?`))
                    void act('restart'); }, children: "Restart" }), _jsx("button", { type: "button", disabled: !!busy, onClick: () => setStartup(true), children: "Startup type" }), ctl.canLogon && _jsx("button", { type: "button", disabled: !!busy, onClick: () => setLogon(true), children: "Log On" }), msg && _jsx("span", { role: "status", className: 'small ' + (msg.bad ? 'err' : 'muted'), style: { flexBasis: '100%', textAlign: 'right' }, children: msg.text }), startup && _jsx(StartupForm, { s: s, win: win, onClose: () => setStartup(false), onPick: (v) => { setStartup(false); void act('startup', { startup: v }); } }), logon && _jsx(LogonForm, { assetId: assetId, s: s, onClose: () => setLogon(false), onDone: (text, bad, svc) => { setLogon(false); setMsg({ text, bad }); if (svc)
                    onState?.(svc); } })] });
}
