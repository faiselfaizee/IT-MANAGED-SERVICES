import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { Act, Badge, Dl, Empty, Err, Field, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { ObjectPicker, OuPicker, ouPath } from './pickers';
import { EditUser } from './UserForm';
export const has = (o, r) => !!o && (o.held ?? o.rights ?? []).includes(r);
export async function act(domainId, body) {
    return api(`/api/adsuite/domains/${domainId}/actions`, { method: 'POST', body });
}
function Said({ r, onClose }) {
    return _jsxs(Modal, { title: r.approval ? 'Sent for approval' : 'Done', onClose: onClose, children: [r.message && _jsx("p", { children: String(r.message) }), r.steps && _jsx("ul", { children: r.steps.map((s) => _jsx("li", { children: s }, s)) }), r.password ? _jsxs(_Fragment, { children: [_jsx("p", { children: "The password is shown once. Give it to the person safely." }), _jsx("p", { children: _jsx("code", { className: "copy", onClick: () => void navigator.clipboard?.writeText(String(r.password)), title: "Click to copy", children: String(r.password) }) })] }) : null, !r.message && !r.password && !r.steps && _jsx("p", { children: "The change was made." }), _jsx("button", { className: "primary", onClick: onClose, children: "Close" })] });
}
function Reset({ onGo, onClose, tls }) {
    const [mode, setMode] = useState('generate');
    const [pw, setPw] = useState('');
    const [pw2, setPw2] = useState('');
    const [must, setMust] = useState(true);
    const [unlock, setUnlock] = useState(true);
    const { err, busy, submit } = useSubmit(onClose);
    return _jsx(Modal, { title: "Reset password", onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: mode === 'generate', onChange: () => setMode('generate') }), " Make a strong password (shown once)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: mode === 'type', onChange: () => setMode('type') }), " Type a password"] }), mode === 'type' && _jsxs(_Fragment, { children: [_jsx(Field, { label: "New password", children: _jsx("input", { type: "password", autoComplete: "new-password", value: pw, onChange: (e) => setPw(e.target.value) }) }), _jsx(Field, { label: "Again", children: _jsx("input", { type: "password", autoComplete: "new-password", value: pw2, onChange: (e) => setPw2(e.target.value) }) })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: must, onChange: (e) => setMust(e.target.checked) }), " Must change at next sign-in"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: unlock, onChange: (e) => setUnlock(e.target.checked) }), " Unlock the account too"] }), !tls && _jsx("p", { className: "muted small", children: "LDAPS is off: this works only when the site controller runs on Windows." }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy || (mode === 'type' && (!pw || pw !== pw2)), onClick: () => void submit(() => onGo({ action: 'reset-password', password: mode === 'type' ? pw : '', mustChange: must, unlock })), children: "Reset" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
function Offboard({ domain, onGo, onClose }) {
    const [pw, setPw] = useState(true);
    const [groups, setGroups] = useState(true);
    const [mgr, setMgr] = useState(false);
    const [to, setTo] = useState('');
    const [note, setNote] = useState('');
    const { err, busy, submit } = useSubmit(onClose);
    return _jsx(Modal, { title: "Offboard (leaver)", onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx("p", { className: "muted", children: "Done in one go, in this order. The account is disabled in every case." }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: pw, onChange: (e) => setPw(e.target.checked) }), " Replace the password with an unknown one"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: groups, onChange: (e) => setGroups(e.target.checked) }), " Remove from every group (Domain Users stays)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: mgr, onChange: (e) => setMgr(e.target.checked) }), " Clear the manager"] }), _jsxs("div", { children: [_jsx("span", { className: "lbl", children: "Move to (optional)" }), _jsx(OuPicker, { domain: domain, value: to, onChange: setTo, label: "Move to" })] }), _jsx(Field, { label: "Note (kept in the description)", children: _jsx("input", { value: note, maxLength: 300, onChange: (e) => setNote(e.target.value), placeholder: "Last working day, ticket number\u2026" }) }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary danger", disabled: busy, onClick: () => void submit(() => onGo({ action: 'offboard', resetPassword: pw, removeGroups: groups, clearManager: mgr, newParent: to, note })), children: "Offboard" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
function MoveTo({ domain, onGo, onClose }) {
    const [to, setTo] = useState('');
    const { err, busy, submit } = useSubmit(onClose);
    return _jsx(Modal, { title: "Move", onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx("span", { className: "lbl", children: "To" }), _jsx(OuPicker, { domain: domain, value: to, onChange: setTo, label: "Move to" }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy || !to, onClick: () => void submit(() => onGo(to)), children: "Move" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
function PickObjects({ domain, kind, title, onGo, onClose }) {
    const [v, setV] = useState([]);
    const { err, busy, submit } = useSubmit(onClose);
    return _jsx(Modal, { title: title, onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx(ObjectPicker, { domain: domain, kind: kind, multiple: true, value: v, onChange: setV }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy || v.length === 0, onClick: () => void submit(() => onGo(v)), children: "OK" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
/** One object: what it is, its groups or members, and what this person may do to it. */
export function Panel({ domain, dn, onChanged, onOpen, onClose }) {
    const o = useLoad(`/api/adsuite/domains/${domain.id}/object?dn=${encodeURIComponent(dn)}`, [dn]);
    const [ask, setAsk] = useState(null);
    const [said, setSaid] = useState(null);
    const [tab, setTab] = useState('details');
    if (!o.data)
        return _jsx("div", { className: "panel", children: _jsx(Loading, { loading: o.loading, error: o.error }) });
    const x = o.data;
    const ob = x.obj;
    const run = async (body) => { const r = await act(domain.id, { dn, ...body }); setSaid(r); if (r.dn && r.dn !== dn)
        onOpen(String(r.dn));
    else
        o.reload(); onChanged(); return r; };
    const user = ob.kind === 'user', group = ob.kind === 'group', comp = ob.kind === 'computer';
    const groupsOf = (x.memberOf ?? []);
    const members = (x.members ?? []);
    const v = (x.values ?? {});
    return _jsxs("div", { className: "panel", children: [_jsxs("div", { className: "panelhead", children: [_jsxs("div", { children: [_jsx("h2", { children: String(ob.name) }), _jsx("p", { className: "muted small", children: ouPath(dn.replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn) })] }), onClose && _jsx("button", { className: "link", "aria-label": "Close", onClick: onClose, children: "\u00D7" })] }), _jsxs("p", { className: "badges", children: [ob.kind !== 'ou' && ob.kind !== 'group' && (ob.disabled ? _jsx(Badge, { tone: "bad", children: "Disabled" }) : _jsx(Badge, { tone: "ok", children: "Enabled" })), ob.locked && _jsx(Badge, { tone: "bad", children: "Locked" }), ob.expired && _jsx(Badge, { tone: "warn", children: "Expired" }), ob.pwdExpired && _jsx(Badge, { tone: "warn", children: "Password expired" }), x.privileged && _jsx(Badge, { tone: "warn", children: "Privileged" }), x.protectedObject && _jsx(Badge, { children: "Built in" })] }), _jsxs("div", { className: "toolbar", children: [user && ob.locked && has(x, 'unlock') && _jsx(Act, { label: "Unlock", primary: true, run: () => run({ action: 'unlock' }) }), user && has(x, 'reset') && _jsx("button", { onClick: () => setAsk('reset'), children: "Reset password" }), (user || comp) && has(x, 'enable') && (ob.disabled ? _jsx(Act, { label: "Enable", run: () => run({ action: 'enable' }) }) : _jsx(Act, { label: "Disable", confirm: `Disable ${ob.name}?`, run: () => run({ action: 'disable' }) })), has(x, 'modify') && ob.kind !== 'ou' && _jsx("button", { onClick: () => setAsk('edit'), children: "Edit" }), user && has(x, 'modify') && _jsx("button", { onClick: () => setAsk('expiry'), children: "Expiry" }), user && has(x, 'modify') && _jsx(Act, { label: ob.pwdNeverExpires ? 'Password may expire' : 'Password never expires', run: () => run({ action: 'set-flags', pwdNeverExpires: !ob.pwdNeverExpires }) }), user && has(x, 'modify') && _jsx(Act, { label: "Must change password", run: () => run({ action: 'set-flags', mustChange: true }) }), user && has(x, 'members') && _jsx("button", { onClick: () => setAsk('addto'), children: "Add to group" }), has(x, 'move') && !x.protectedObject && _jsx("button", { onClick: () => setAsk('move'), children: "Move" }), user && has(x, 'enable') && !x.protectedObject && _jsx("button", { className: "danger", onClick: () => setAsk('offboard'), children: "Offboard" }), has(x, 'delete') && !x.protectedObject && _jsx(Act, { label: "Delete", danger: true, confirm: `Delete ${ob.name}? This cannot be undone and needs a second person's approval.`, run: () => run({ action: 'delete' }) })] }), _jsx("div", { className: "tabs", role: "tablist", children: ['details', ...(group ? ['members'] : ob.kind === 'ou' ? [] : ['groups'])].map((t) => _jsx("button", { role: "tab", "aria-selected": tab === t, className: tab === t ? 'on' : '', onClick: () => setTab(t), children: t === 'details' ? 'Details' : t === 'members' ? `Members (${String(x.membersTotal)})` : `Member of (${groupsOf.length})` }, t)) }), tab === 'details' && _jsx(Dl, { rows: [['Sign-in name', ob.sam], ['Principal name', ob.upn], ['Email', ob.mail], ['Title', ob.title], ['Department', ob.department], ['Company', v.company], ['Office', v.physicalDeliveryOfficeName],
                    ['Telephone', v.telephoneNumber], ['Mobile', v.mobile], ['Manager', x.manager ? _jsx("button", { className: "link", onClick: () => onOpen(String(x.manager.dn)), children: String(x.manager.name) }, "m") : ''],
                    ['Description', ob.description], ['Employee ID', v.employeeID], ['City', v.l], ['Home folder', v.homeDirectory ? `${v.homeDrive ?? ''} ${v.homeDirectory}` : ''], ['Logon script', v.scriptPath],
                    ['Operating system', ob.os], ['DNS name', ob.dns], ['Last sign-in', ob.lastLogon ? fmtDate(ob.lastLogon) : ''], ['Password set', x.passwordLastSet ? fmtDate(x.passwordLastSet) : ''],
                    ['Password expires', ob.pwdNeverExpires ? 'Never' : x.passwordExpires ? fmtDate(x.passwordExpires) : ''], ['Account expires', ob.accountExpires ? fmtDate(ob.accountExpires) : ''],
                    ['Created', ob.created ? fmtDate(ob.created) : ''], ['Changed', x.changed ? fmtDate(x.changed) : '']].filter(([, val]) => val !== '' && val !== undefined && val !== null) }), tab === 'groups' && _jsx(_Fragment, { children: groupsOf.length === 0 ? _jsx(Empty, { children: "Only the primary group (usually Domain Users)." }) : _jsx("ul", { className: "plain", children: groupsOf.map((m) => _jsxs("li", { children: [_jsx("button", { className: "link", onClick: () => onOpen(String(m.dn)), children: String(m.name) }), has(x, 'members') && _jsx(Act, { label: "Remove", confirm: `Remove ${ob.name} from ${m.name}?`, run: () => act(domain.id, { action: 'member-remove', dn: m.dn, members: [dn] }).then((r) => { setSaid(r); o.reload(); }) })] }, String(m.dn))) }) }), tab === 'members' && _jsxs(_Fragment, { children: [has(x, 'members') && _jsx("div", { className: "toolbar", children: _jsx("button", { onClick: () => setAsk('addmember'), children: "Add members" }) }), members.length === 0 ? _jsx(Empty, { children: "No members." }) : _jsx("ul", { className: "plain", children: members.map((m) => _jsxs("li", { children: [_jsx("button", { className: "link", onClick: () => onOpen(String(m.dn)), children: String(m.name) }), has(x, 'members') && _jsx(Act, { label: "Remove", confirm: `Remove ${m.name} from ${ob.name}?`, run: () => run({ action: 'member-remove', members: [m.dn] }) })] }, String(m.dn))) })] }), ask === 'reset' && _jsx(Reset, { tls: !!domain.tls, onClose: () => setAsk(null), onGo: run }), ask === 'offboard' && _jsx(Offboard, { domain: domain, onClose: () => setAsk(null), onGo: run }), ask === 'move' && _jsx(MoveTo, { domain: domain, onClose: () => setAsk(null), onGo: (to) => run({ action: 'move', newParent: to }) }), ask === 'addmember' && _jsx(PickObjects, { domain: domain, kind: "all", title: 'Add members to ' + String(ob.name), onClose: () => setAsk(null), onGo: (h) => run({ action: 'member-add', members: h.map((m) => m.dn) }) }), ask === 'addto' && _jsx(PickObjects, { domain: domain, kind: "group", title: 'Add ' + String(ob.name) + ' to groups', onClose: () => setAsk(null), onGo: async (h) => {
                    let last = {};
                    for (const g of h)
                        last = await act(domain.id, { action: 'member-add', dn: g.dn, members: [dn] });
                    setSaid(last.approval ? last : { message: `Added to ${h.length} group(s).` });
                    o.reload();
                    onChanged();
                } }), ask === 'expiry' && _jsx(Expiry, { onClose: () => setAsk(null), onGo: (d) => run({ action: 'set-expiry', expires: d }) }), ask === 'edit' && _jsx(EditUser, { x: x, onClose: () => setAsk(null), onSave: (attrs) => run({ action: 'update', attrs }) }), said && _jsx(Said, { r: said, onClose: () => setSaid(null) })] });
}
function Expiry({ onGo, onClose }) {
    const [d, setD] = useState('');
    const { err, busy, submit } = useSubmit(onClose);
    return _jsx(Modal, { title: "Account expiry", onClose: onClose, children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Last day (empty = never expires)", children: _jsx("input", { type: "date", value: d, onChange: (e) => setD(e.target.value) }) }), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy, onClick: () => void submit(() => onGo(d)), children: "Save" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] }) });
}
const STATUS = {
    user: [['', 'All'], ['enabled', 'Enabled'], ['disabled', 'Disabled'], ['locked', 'Locked'], ['expired', 'Expired'], ['neverexpires', 'Password never expires']],
    computer: [['', 'All'], ['enabled', 'Enabled'], ['disabled', 'Disabled']],
    group: [], ou: [],
};
const TITLES = { user: 'Users', group: 'Groups', computer: 'Computers', ou: 'Organisational units' };
/** A list of users, groups, computers or OUs: search, filter, choose several, act on all of them. */
export function ObjectList({ domain, kind, open, onOpen }) {
    const [q, setQ] = useState('');
    const [go, setGo] = useState('');
    const [status, setStatus] = useState('');
    const [scope, setScope] = useState('');
    const [sel, setSel] = useState([]);
    const [bulk, setBulk] = useState(null);
    const [progress, setProgress] = useState(null);
    const url = `/api/adsuite/domains/${domain.id}/search?kind=${kind}&q=${encodeURIComponent(go)}${status ? '&status=' + status : ''}${scope ? '&base=' + encodeURIComponent(scope) : ''}&limit=1000`;
    const res = useLoad(url, [url]);
    const items = (res.data?.items ?? []);
    const all = items.length > 0 && items.every((i) => sel.includes(String(i.dn)));
    const toggle = (dn) => setSel(sel.includes(dn) ? sel.filter((d) => d !== dn) : [...sel, dn]);
    // a bulk action runs one object at a time so each gets its own result and the person sees the progress
    const runBulk = async (label, body) => {
        const out = [];
        setProgress({ label, done: 0, total: sel.length, out });
        for (const dn of sel) {
            try {
                const r = await act(domain.id, body(dn));
                out.push({ dn, ok: true, approval: !!r.approval, password: r.password });
            }
            catch (e) {
                out.push({ dn, ok: false, error: e instanceof Error ? e.message : String(e) });
            }
            setProgress({ label, done: out.length, total: sel.length, out: [...out] });
        }
        res.reload();
        setSel([]);
    };
    const userish = kind === 'user' || kind === 'computer';
    return _jsxs("div", { className: "listpane", children: [_jsx("h1", { children: TITLES[kind] }), _jsxs("div", { className: "filters", children: [_jsx("input", { "aria-label": "Search", placeholder: kind === 'user' ? 'Name, sign-in name or email' : 'Name', value: q, onChange: (e) => setQ(e.target.value), onKeyDown: (e) => { if (e.key === 'Enter')
                            setGo(q); } }), _jsx("button", { className: "primary", onClick: () => setGo(q), children: "Search" }), STATUS[kind].length > 0 && _jsx("select", { "aria-label": "Status", value: status, onChange: (e) => setStatus(e.target.value), children: STATUS[kind].map(([k, l]) => _jsx("option", { value: k, children: l }, k)) }), _jsxs("div", { className: "scope", children: [_jsx(OuPicker, { domain: domain, value: scope, onChange: setScope, label: "In OU", allowDomain: true }), scope && _jsx("button", { className: "link", onClick: () => setScope(''), children: "Clear" })] })] }), sel.length > 0 && _jsxs("div", { className: "bulkbar", children: [_jsxs("strong", { children: [sel.length, " chosen"] }), userish && _jsx("button", { onClick: () => void runBulk('Enabling', (dn) => ({ action: 'enable', dn })), children: "Enable" }), userish && _jsx("button", { onClick: () => { if (confirm(`Disable ${sel.length} account(s)?`))
                            void runBulk('Disabling', (dn) => ({ action: 'disable', dn })); }, children: "Disable" }), kind === 'user' && _jsx("button", { onClick: () => void runBulk('Unlocking', (dn) => ({ action: 'unlock', dn })), children: "Unlock" }), kind === 'user' && _jsx("button", { onClick: () => { if (confirm(`Give ${sel.length} account(s) new passwords? They are shown once.`))
                            void runBulk('Resetting passwords', (dn) => ({ action: 'reset-password', dn, mustChange: true })); }, children: "Reset passwords" }), kind !== 'ou' && _jsx("button", { onClick: () => setBulk('addto'), children: "Add to group" }), _jsx("button", { onClick: () => setBulk('move'), children: "Move" }), _jsx("button", { className: "danger", onClick: () => { if (confirm(`Delete ${sel.length} object(s)? Each waits for a second person's approval.`))
                            void runBulk('Deleting', (dn) => ({ action: 'delete', dn })); }, children: "Delete" }), _jsx("button", { className: "link", onClick: () => setSel([]), children: "Clear" })] }), _jsx(Loading, { loading: res.loading, error: res.error }), res.data && (items.length === 0 ? _jsx(Empty, { children: "Nothing found." }) : _jsxs(_Fragment, { children: [_jsx("div", { className: "listhead", children: _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", "aria-label": "Choose all", checked: all, onChange: () => setSel(all ? [] : items.map((i) => String(i.dn))) }), " ", items.length, " ", TITLES[kind].toLowerCase()] }) }), _jsx("ul", { className: "objlist", children: items.map((i) => {
                            const dn = String(i.dn);
                            const sub = kind === 'user' ? [i.sam, i.title, i.department].filter(Boolean).join(' · ') : kind === 'computer' ? String(i.os ?? '') : kind === 'group' ? String(i.description ?? '') : ouPath(dn.replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn);
                            return _jsxs("li", { className: open === dn ? 'on' : '', onClick: () => onOpen(dn), children: [_jsx("input", { type: "checkbox", "aria-label": 'Choose ' + i.name, checked: sel.includes(dn), onClick: (e) => e.stopPropagation(), onChange: () => toggle(dn) }), _jsx("span", { className: 'avatar k-' + kind, "aria-hidden": true, children: kind === 'ou' ? '📁' : String(i.name ?? '?').slice(0, 1).toUpperCase() }), _jsxs("span", { className: "who", children: [_jsx("strong", { children: String(i.name) }), _jsx("small", { children: sub })] }), _jsxs("span", { className: "flags", children: [i.disabled && _jsx(Badge, { tone: "bad", children: "Disabled" }), i.locked && _jsx(Badge, { tone: "bad", children: "Locked" }), i.expired && _jsx(Badge, { tone: "warn", children: "Expired" })] })] }, dn);
                        }) })] })), res.data?.truncated ? _jsx("p", { className: "muted small", children: "Only the first 1,000 are shown. Narrow the search or choose an OU." }) : null, bulk === 'move' && _jsx(MoveTo, { domain: domain, onClose: () => setBulk(null), onGo: (to) => runBulk('Moving', (dn) => ({ action: 'move', dn, newParent: to })) }), bulk === 'addto' && _jsx(PickObjects, { domain: domain, kind: "group", title: `Add ${sel.length} to groups`, onClose: () => setBulk(null), onGo: async (h) => {
                    const dns = [...sel];
                    const out = [];
                    setProgress({ label: 'Adding to groups', done: 0, total: h.length, out });
                    for (const g of h) {
                        try {
                            const r = await act(domain.id, { action: 'member-add', dn: g.dn, members: dns });
                            out.push({ dn: g.dn, ok: true, approval: !!r.approval });
                        }
                        catch (e) {
                            out.push({ dn: g.dn, ok: false, error: e instanceof Error ? e.message : String(e) });
                        }
                        setProgress({ label: 'Adding to groups', done: out.length, total: h.length, out: [...out] });
                    }
                    setSel([]);
                } }), progress && _jsx(Progress, { p: progress, onClose: () => setProgress(null) })] });
}
export function Progress({ p, onClose }) {
    const out = (p.out ?? []);
    const finished = p.done >= p.total;
    const pws = out.filter((o) => o.password);
    return _jsxs(Modal, { title: `${p.label}: ${p.done} of ${p.total}`, onClose: () => finished && onClose(), children: [_jsx("div", { className: "bar", children: _jsx("i", { style: { width: `${(100 * p.done) / Math.max(1, p.total)}%` } }) }), _jsxs("p", { children: [out.filter((o) => o.ok && !o.approval).length, " done, ", out.filter((o) => o.approval).length, " sent for approval, ", out.filter((o) => !o.ok).length, " failed."] }), out.some((o) => !o.ok) && _jsx("ul", { className: "plain small", children: out.filter((o) => !o.ok).map((o) => _jsxs("li", { children: [_jsx("code", { children: ouPath(String(o.dn)).split(' › ').pop() }), ": ", _jsx("span", { className: "err", children: String(o.error) })] }, String(o.dn))) }), pws.length > 0 && _jsxs(_Fragment, { children: [_jsx("p", { children: "New passwords, shown once:" }), _jsx("table", { className: "adtable small", children: _jsx("tbody", { children: pws.map((o) => _jsxs("tr", { children: [_jsx("td", { children: ouPath(String(o.dn)).split(' › ').pop() }), _jsx("td", { children: _jsx("code", { children: String(o.password) }) })] }, String(o.dn))) }) })] }), finished && _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", onClick: onClose, children: "Close" }) })] });
}
