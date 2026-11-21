import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useLoad } from '../http';
import { Empty, fmtDate, Loading, Modal } from '../ui';
import * as S from './session';
import { KIND_WORDS, ROLE_WORDS } from './types';
function Members({ v, onDone }) {
    const m = useLoad(`/api/vault/vaults/${v.id}/members`);
    const dir = useLoad('/api/vault/users');
    const [list, setList] = useState(null);
    const [add, setAdd] = useState('');
    const [err, setErr] = useState('');
    const [msg, setMsg] = useState('');
    useEffect(() => { if (m.data && !list)
        setList(m.data.grants.map((g) => ({ type: g.type, id: g.id, role: g.role, name: g.name }))); }, [m.data, list]);
    const users = (dir.data?.users ?? []);
    const groups = (dir.data?.groups ?? []);
    const people = (m.data?.people ?? []);
    const canManage = !!m.data?.canManage || S.current().me?.settings?.role === 'admin';
    if (!list)
        return _jsx(Loading, { loading: true, error: m.error });
    const options = [...groups.map((g) => ({ key: 'group:' + g.id, label: 'Group: ' + g.name })), ...users.map((u) => ({ key: 'user:' + u.id, label: `${u.name} (${u.email})${u.setUp ? '' : ' – has not set up the vault yet'}` }))]
        .filter((o) => !list.some((l) => l.type + ':' + l.id === o.key));
    const save = async () => {
        setErr('');
        setMsg('');
        try {
            const r = await api(`/api/vault/vaults/${v.id}/members`, { method: 'PUT', body: { members: list.map(({ type, id, role }) => ({ type, id, role })) } });
            const n = await S.givePending();
            setMsg(r.needsRotation ? 'Saved. Someone who held the vault\'s key lost access: change the vault\'s keys so a copy they kept stops working.' : `Saved.${n ? ` ${n} people received the vault's key.` : ''}`);
            m.reload();
            onDone();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    return _jsxs("div", { className: "form", children: [_jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Who" }), _jsx("th", { children: "May" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((l, i) => _jsxs("tr", { children: [_jsxs("td", { children: [l.type === 'group' ? 'Group: ' : '', l.name ?? (l.type === 'group' ? groups.find((g) => g.id === l.id)?.name : users.find((u) => u.id === l.id)?.name) ?? l.id] }), _jsx("td", { children: _jsx("select", { disabled: !canManage, value: l.role, onChange: (e) => setList(list.map((x, j) => j === i ? { ...x, role: e.target.value } : x)), "aria-label": "Role", children: Object.entries(ROLE_WORDS).map(([k, w]) => _jsx("option", { value: k, children: w }, k)) }) }), _jsx("td", { children: canManage && _jsx("button", { className: "small", onClick: () => setList(list.filter((_, j) => j !== i)), children: "Remove" }) })] }, l.type + l.id)) })] }), canManage && _jsxs("div", { className: "tools", children: [_jsxs("select", { value: add, onChange: (e) => setAdd(e.target.value), "aria-label": "Add someone", children: [_jsx("option", { value: "", children: "Add a person or group\u2026" }), options.map((o) => _jsx("option", { value: o.key, children: o.label }, o.key))] }), _jsx("button", { disabled: !add, onClick: () => { const [type, id] = add.split(':'); setList([...list, { type: type, id, role: 'view' }]); setAdd(''); }, children: "Add" })] }), _jsx("p", { className: "muted small", children: "May ask for access: sees item names only and asks to open one. Can view: opens items. Can edit: also changes items and answers requests. Manages: also changes who has access." }), people.length > 0 && _jsxs("details", { open: true, children: [_jsxs("summary", { children: ["Who has the key (", people.filter((p) => p.state === 'has_key').length, " of ", people.length, ")"] }), _jsx("ul", { className: "plain", children: people.map((p) => _jsxs("li", { children: [p.name, ": ", _jsx("span", { className: 'badge ' + (p.state === 'has_key' ? 'ok' : 'warn'), children: p.state === 'has_key' ? 'has the key' : p.state === 'waiting' ? 'gets the key when a manager next opens the Vault' : 'has not set up the vault yet' })] }, p.id)) })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), msg && _jsx("p", { className: "note", role: "status", children: msg }), canManage && _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", onClick: () => void save(), children: "Save members" }) })] });
}
function Folders({ v }) {
    const f = useLoad(`/api/vault/vaults/${v.id}/folders`);
    const [names, setNames] = useState([]);
    const [n, setN] = useState('');
    const [err, setErr] = useState('');
    useEffect(() => { void (async () => { const o = []; for (const x of (f.data?.items ?? []))
        o.push({ id: x.id, name: await S.folderName(v.id, x.nameEnc) }); setNames(o); })(); }, [f.data, v.id]);
    const run = async (fn) => { setErr(''); try {
        await fn();
        f.reload();
    }
    catch (e) {
        setErr(e.message);
    } };
    return _jsxs("div", { className: "form", children: [names.length === 0 ? _jsx(Empty, { children: "No folders." }) : _jsx("ul", { className: "plain", children: names.map((x) => _jsxs("li", { children: [x.name, " ", _jsx("button", { className: "small", onClick: () => void run(async () => { const nn = window.prompt('New name', x.name); if (nn)
                                await api(`/api/vault/folders/${x.id}`, { method: 'PUT', body: { nameEnc: await S.encFolder(v.id, nn) } }); }), children: "Rename" }), _jsx("button", { className: "small", onClick: () => void run(async () => { if (window.confirm(`Delete the folder "${x.name}"? Its items stay in the vault.`))
                                await api(`/api/vault/folders/${x.id}`, { method: 'DELETE' }); }), children: "Delete" })] }, x.id)) }), _jsxs("div", { className: "tools", children: [_jsx("input", { value: n, onChange: (e) => setN(e.target.value), placeholder: "New folder", "aria-label": "Folder name" }), _jsx("button", { disabled: !n.trim(), onClick: () => void run(async () => { await api(`/api/vault/vaults/${v.id}/folders`, { method: 'POST', body: { nameEnc: await S.encFolder(v.id, n.trim()) } }); setN(''); }), children: "Add folder" })] }), err && _jsx("p", { className: "err", role: "alert", children: err })] });
}
function Log({ v }) {
    const e = useLoad(`/api/vault/vaults/${v.id}/events`);
    const list = (e.data?.items ?? []);
    return list.length ? _jsx("ul", { className: "plain log", children: list.map((x) => _jsxs("li", { children: [_jsx("time", { children: fmtDate(x.at) }), " ", x.user, ": ", String(x.action).replace(/_/g, ' '), x.detail ? _jsxs("span", { className: "muted", children: [" \u00B7 ", x.detail] }) : null] }, x.id)) }) : _jsx(Empty, { children: "Nothing yet." });
}
function NewVault({ onDone }) {
    const companies = useLoad('/api/assets/companies');
    const cos = (Array.isArray(companies.data) ? companies.data : (companies.data?.items ?? []));
    const taken = new Set(S.current().vaults.filter((v) => v.kind === 'customer').map((v) => v.companyId));
    const [kind, setKind] = useState('customer');
    const [co, setCo] = useState('');
    const [name, setName] = useState('');
    const [err, setErr] = useState('');
    return _jsxs("form", { className: "form", onSubmit: async (e) => { e.preventDefault(); setErr(''); try {
            await S.createVault(kind, name, kind === 'customer' ? Number(co) : undefined);
            onDone();
        }
        catch (x) {
            setErr(x.message);
        } }, children: [_jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: kind === 'customer', onChange: () => setKind('customer') }), " For a customer (their servers, network and cloud accounts)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: kind === 'internal', onChange: () => setKind('internal') }), " Internal (your own company's accounts)"] }), kind === 'customer' && _jsxs("label", { children: [_jsx("span", { children: "Customer" }), _jsxs("select", { value: co, onChange: (e) => { setCo(e.target.value); setName(cos.find((c) => String(c.id) === e.target.value)?.name ?? ''); }, children: [_jsx("option", { value: "", children: "Choose\u2026" }), cos.map((c) => _jsxs("option", { value: c.id, disabled: taken.has(c.id), children: [c.name, taken.has(c.id) ? ' (has a vault)' : ''] }, c.id))] })] }), _jsxs("label", { children: [_jsx("span", { children: "Name" }), _jsx("input", { value: name, onChange: (e) => setName(e.target.value), placeholder: kind === 'internal' ? 'Internal IT' : 'Customer name' })] }), _jsx("p", { className: "muted small", children: "You become the vault's first manager. Add people or groups afterwards." }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: kind === 'customer' ? !co : !name.trim(), children: "Create vault" }) })] });
}
export default function Vaults() {
    const session = S.useSession();
    const [open, setOpen] = useState(null);
    const [creating, setCreating] = useState(false);
    const [step, setStep] = useState('');
    const [err, setErr] = useState('');
    const me = session.me?.settings ?? {};
    const reload = () => void S.refresh().catch((e) => setErr(e.message));
    const personal = session.vaults.find((v) => v.kind === 'personal');
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "dash-head", children: [_jsx("h1", { children: "Vaults" }), _jsxs("div", { className: "tools", children: [me.canCreate && _jsx("button", { className: "primary", onClick: () => setCreating(true), children: "New vault" }), !personal && _jsx("button", { onClick: () => void S.createVault('personal', 'My vault').catch((e) => setErr(e.message)), children: "Create my personal vault" })] })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), step && _jsx("p", { className: "note", role: "status", children: step }), session.vaults.length === 0 && _jsx(Empty, { children: "No vaults yet." }), _jsx("div", { className: "vault-cards", children: session.vaults.map((v) => {
                    const k = S.vaultKeys(v.id);
                    return _jsxs("section", { className: 'vault-card k-' + v.kind, children: [_jsxs("header", { children: [_jsx("h2", { children: v.name }), _jsxs("span", { className: "muted small", children: [KIND_WORDS[v.kind], v.company && v.company !== v.name ? ' · ' + v.company : ''] })] }), _jsxs("p", { className: "figs", children: [_jsx("b", { children: v.items }), " items \u00B7 ", v.kind === 'personal' ? 'only you' : ROLE_WORDS[v.role].toLowerCase()] }), !k?.dir && _jsx("p", { className: "badge warn", children: "Waiting for a manager to give you the key" }), v.needsRotation && S.rank(v.role) === 4 && _jsxs("div", { className: "note rotate-note", children: [_jsx("span", { children: "Someone who held this vault's key no longer has access." }), _jsx("button", { className: "primary small", onClick: async () => { setErr(''); try {
                                            await S.rotateVault(v.id, setStep);
                                            setStep('The vault has new keys.');
                                        }
                                        catch (e) {
                                            setStep('');
                                            setErr(e.message);
                                        } }, children: "Change the vault's keys" })] }), _jsxs("div", { className: "tools", children: [_jsx("a", { href: `#/vault?vault=${v.id}`, children: "Open" }), v.kind !== 'personal' && (S.rank(v.role) === 4 || me.role === 'admin') && _jsx("button", { className: "small", onClick: () => setOpen({ v, tab: 'members' }), children: "Members" }), S.rank(v.role) >= 3 && k?.item && _jsx("button", { className: "small", onClick: () => setOpen({ v, tab: 'folders' }), children: "Folders" }), S.rank(v.role) >= 3 && _jsx("button", { className: "small", onClick: () => setOpen({ v, tab: 'log' }), children: "Activity" }), v.kind !== 'personal' && S.rank(v.role) === 4 && _jsx("button", { className: "small", onClick: async () => { const n = window.prompt('New name', v.name); if (n) {
                                            await api(`/api/vault/vaults/${v.id}`, { method: 'PUT', body: { name: n } });
                                            reload();
                                        } }, children: "Rename" }), v.kind !== 'personal' && (S.rank(v.role) === 4 || me.role === 'admin') && _jsx("button", { className: "small danger", onClick: async () => { if (window.prompt(`Delete the vault "${v.name}" and its ${v.items} items for good? Type the vault's name to confirm.`) === v.name) {
                                            await api(`/api/vault/vaults/${v.id}`, { method: 'DELETE' });
                                            reload();
                                        } }, children: "Delete" })] })] }, v.id);
                }) }), creating && _jsx(Modal, { title: "New vault", onClose: () => setCreating(false), children: _jsx(NewVault, { onDone: () => setCreating(false) }) }), open && _jsxs(Modal, { title: `${open.v.name}: ${open.tab === 'members' ? 'members' : open.tab === 'folders' ? 'folders' : 'activity'}`, onClose: () => setOpen(null), children: [open.tab === 'members' ? _jsx(Members, { v: open.v, onDone: reload }) : open.tab === 'folders' ? _jsx(Folders, { v: open.v }) : _jsx(Log, { v: open.v }), _jsx("div", { className: "tools", children: _jsx("button", { onClick: () => setOpen(null), children: "Close" }) })] })] });
}
