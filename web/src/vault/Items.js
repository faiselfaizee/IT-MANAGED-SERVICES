import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { ago, copyText, Empty, fmtDate, Loading, Modal } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { customOf, KIND_WORDS, TYPES, typeDef } from './types';
import { Strength } from './Gate';
import { Generator } from './Tools';
const GLYPH = { login: 'L', server: 'S', network: 'N', database: 'D', wifi: 'W', ssh: 'K', apikey: 'A', card: 'C', identity: 'I', note: '¶' };
/** A secret value: hidden until shown, copied without showing; both are written to the item's log. */
function Secret({ itemId, field, value, multiline }) {
    const [shown, setShown] = useState(false);
    const [copied, setCopied] = useState(false);
    return _jsxs("span", { className: "secret", children: [shown ? (multiline ? _jsx("pre", { children: value }) : _jsx("code", { children: value })) : _jsx("code", { "aria-label": "Hidden", children: '•'.repeat(Math.min(Math.max(value.length, 8), 16)) }), _jsx("button", { type: "button", className: "small", onClick: () => { if (!shown)
                    S.note(itemId, 'revealed', field); setShown(!shown); }, children: shown ? 'Hide' : 'Show' }), _jsx("button", { type: "button", className: "small", onClick: () => { copyText(value); S.note(itemId, 'copied', field); setCopied(true); setTimeout(() => setCopied(false), 1500); clearLater(value); }, children: copied ? 'Copied' : 'Copy' })] });
}
/** Clears the clipboard after 30 seconds if it still holds the secret. */
function clearLater(v) {
    setTimeout(async () => { try {
        if ((await navigator.clipboard.readText()) === v)
            await navigator.clipboard.writeText('');
    }
    catch { /* not allowed: leave it */ } }, 30_000);
}
function Totp({ secret, itemId }) {
    const [c, setC] = useState(null);
    useEffect(() => { let live = true; const tick = () => void C.totp(secret).then((x) => live && setC(x)); tick(); const t = setInterval(tick, 1000); return () => { live = false; clearInterval(t); }; }, [secret]);
    if (!c)
        return _jsx("span", { className: "muted", children: "Not a valid one-time code key" });
    return _jsxs("span", { className: "secret", children: [_jsx("code", { className: "totp", children: c.code.replace(/(\d{3})(\d+)/, '$1 $2') }), _jsxs("span", { className: "muted small", children: [c.left, "s"] }), _jsx("button", { type: "button", className: "small", onClick: () => { copyText(c.code); S.note(itemId, 'copied', 'one-time code'); }, children: "Copy" })] });
}
function FieldValue({ f, v, itemId }) {
    if (f.kind === 'totp')
        return _jsx(Totp, { secret: v, itemId: itemId });
    if (f.secret)
        return _jsx(Secret, { itemId: itemId, field: f.label, value: v, multiline: f.multiline });
    if (f.kind === 'url') {
        const href = /^https?:\/\//i.test(v) ? v : 'https://' + v;
        return _jsxs("span", { className: "secret", children: [_jsx("a", { href: href, target: "_blank", rel: "noreferrer noopener", onClick: () => S.note(itemId, 'opened', f.label), children: v }), _jsx("button", { type: "button", className: "small", onClick: () => copyText(v), children: "Copy" })] });
    }
    return _jsxs("span", { className: "secret", children: [f.multiline ? _jsx("span", { className: "multi", children: v }) : _jsx("span", { children: v }), _jsx("button", { type: "button", className: "small", onClick: () => { copyText(v); }, children: "Copy" })] });
}
/** The decrypted fields of an item. */
export function Fields({ type, data, itemId }) {
    const t = typeDef(type);
    const rows = t.fields.filter((f) => data[f.k]);
    const custom = customOf(data);
    return _jsxs("dl", { className: "kv vault-fields", children: [rows.map((f) => _jsxs("div", { className: "kv-row", children: [_jsx("dt", { children: f.label }), _jsx("dd", { children: _jsx(FieldValue, { f: f, v: data[f.k], itemId: itemId }) })] }, f.k)), custom.map((c, i) => _jsxs("div", { className: "kv-row", children: [_jsx("dt", { children: c.name }), _jsx("dd", { children: _jsx(FieldValue, { f: { k: c.name, label: c.name, secret: c.hidden }, v: c.value, itemId: itemId }) })] }, 'c' + i))] });
}
function Managed({ item, data, onChange }) {
    const m = useLoad(item.managed ? `/api/vault/items/${item.id}/managed` : null, [item.id, item.managed]);
    const [secret, setSecret] = useState(null);
    const [err, setErr] = useState('');
    const [form, setForm] = useState(false);
    const [f, setF] = useState({ account: data?.username ?? '', target: 'local', rotateDays: 30, resetAfterUse: false, rotateNow: false });
    const acc = m.data?.account;
    useInterval(() => { if (acc?.state === 'rotating')
        m.reload(); }, acc?.state === 'rotating' ? 3000 : null);
    const run = async (fn) => { setErr(''); try {
        await fn();
    }
    catch (e) {
        setErr(e.message);
    } };
    if (!item.managed) {
        if (!item.canEdit || !item.assetId || !['server', 'login', 'database', 'network'].includes(item.type) || !data?.password)
            return null;
        return _jsxs("section", { className: "managed-box", children: [_jsx("h3", { children: "Password changes" }), !form ? _jsxs(_Fragment, { children: [_jsx("p", { className: "muted", children: "DeskAsk can change this password on a schedule through the agent on the linked asset. The server then holds the password so the agent can set it; reading it is always logged." }), _jsx("button", { onClick: () => setForm(true), children: "Let DeskAsk change this password" })] }) : _jsxs("form", { className: "form", onSubmit: (e) => {
                        e.preventDefault();
                        void run(async () => {
                            await api(`/api/vault/items/${item.id}/managed`, { method: 'POST', body: { ...f, rotateDays: Number(f.rotateDays), password: data.password } });
                            // the password now lives sealed on the server: take it out of the encrypted item
                            const { password: _drop, ...rest } = data;
                            void _drop;
                            const enc = await S.encItem(item.vaultId, await S.title(item), rest);
                            await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: item.revision } });
                            setForm(false);
                            onChange();
                        });
                    }, children: [_jsxs("label", { children: [_jsx("span", { children: "Account name (as the computer knows it, no domain prefix)" }), _jsx("input", { value: f.account, onChange: (e) => setF({ ...f, account: e.target.value }) })] }), _jsxs("label", { children: [_jsx("span", { children: "Kind of account" }), _jsxs("select", { value: f.target, onChange: (e) => setF({ ...f, target: e.target.value }), children: [_jsx("option", { value: "local", children: "Windows local account" }), _jsx("option", { value: "domain", children: "Active Directory account (agent on a domain controller)" }), _jsx("option", { value: "linux", children: "Linux account" })] })] }), _jsxs("label", { children: [_jsx("span", { children: "Change it every (days; 0 = only when asked)" }), _jsx("input", { type: "number", min: 0, max: 365, value: f.rotateDays, onChange: (e) => setF({ ...f, rotateDays: e.target.value }) })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: f.resetAfterUse, onChange: (e) => setF({ ...f, resetAfterUse: e.target.checked }) }), " Change it after each use (an hour after someone reads it, or when an approved request ends)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: f.rotateNow, onChange: (e) => setF({ ...f, rotateNow: e.target.checked }) }), " Change it now"] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", children: "Start managing" }), _jsx("button", { type: "button", onClick: () => setForm(false), children: "Cancel" })] })] })] });
    }
    const stateWords = { ok: 'Up to date', rotating: 'Changing now…', failed: 'Last change failed', unknown: 'Unsure: the agent did not report back' };
    return _jsxs("section", { className: "managed-box", children: [_jsx("h3", { children: "Managed by DeskAsk" }), _jsx(Loading, { loading: m.loading && !m.data, error: m.error }), acc && _jsxs(_Fragment, { children: [_jsxs("dl", { className: "kv", children: [_jsx("dt", { children: "Account" }), _jsxs("dd", { children: [String(acc.account), " ", _jsxs("span", { className: "muted", children: ["(", acc.target === 'domain' ? 'Active Directory' : acc.target === 'linux' ? 'Linux' : 'Windows local', ") on ", _jsx("a", { href: `#/assets/${acc.assetId}`, children: String(acc.asset || 'asset ' + acc.assetId) })] })] }), _jsx("dt", { children: "Status" }), _jsxs("dd", { children: [_jsx("span", { className: 'badge ' + (acc.state === 'ok' ? 'ok' : acc.state === 'rotating' ? '' : 'bad'), children: stateWords[acc.state] ?? acc.state }), acc.lastError ? _jsx("div", { className: "muted small", children: String(acc.lastError) }) : null] }), _jsx("dt", { children: "Last changed" }), _jsx("dd", { children: acc.lastRotatedAt ? ago(acc.lastRotatedAt) : 'Not by DeskAsk yet' }), _jsx("dt", { children: "Next change" }), _jsxs("dd", { children: [acc.nextAt ? fmtDate(acc.nextAt) : 'Only when asked', acc.resetAfterUse ? ' · and after each use' : ''] }), _jsx("dt", { children: "Password" }), _jsx("dd", { children: secret ? _jsxs(_Fragment, { children: [_jsx(Secret, { itemId: item.id, field: "managed password", value: String(secret.password) }), secret.possiblePassword ? _jsxs("div", { className: "muted small", children: ["Or possibly: ", _jsx(Secret, { itemId: item.id, field: "possible password", value: String(secret.possiblePassword) })] }) : null] })
                                    : _jsx("button", { onClick: () => void run(async () => setSecret(await api(`/api/vault/items/${item.id}/secret`))), children: "Show password" }) })] }), item.canEdit && _jsxs("div", { className: "tools", children: [_jsx("button", { disabled: acc.state === 'rotating', onClick: () => void run(async () => { await api(`/api/vault/items/${item.id}/rotate`, { method: 'POST', body: {} }); setSecret(null); m.reload(); }), children: "Change the password now" }), _jsx("button", { onClick: () => void run(async () => {
                                    if (!window.confirm('Stop managing this account? The current password goes back into the encrypted item and the server forgets it.'))
                                        return;
                                    const r = await api(`/api/vault/items/${item.id}/managed`, { method: 'DELETE' });
                                    const fresh = await api(`/api/vault/items/${item.id}`);
                                    const enc = await S.encItem(item.vaultId, await S.title(item), { ...((await S.data(fresh)) ?? {}), password: String(r.password) });
                                    await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: fresh.revision, passwordChanged: true } });
                                    onChange();
                                }), children: "Stop managing" })] }), (m.data?.history ?? []).length > 0 && _jsxs("details", { children: [_jsxs("summary", { children: ["Earlier passwords (", m.data.history.length, ")"] }), _jsx("ul", { className: "plain", children: m.data.history.map((h) => _jsxs("li", { children: [fmtDate(h.at), ": ", h.note === 'maybe' ? 'possibly set (the agent did not report)' : 'replaced'] }, h.id)) })] })] }), err && _jsx("p", { className: "err", role: "alert", children: err })] });
}
function RequestAccess({ item, onDone }) {
    const [reason, setReason] = useState('');
    const [ticket, setTicket] = useState('');
    const [minutes, setMinutes] = useState(60);
    const [err, setErr] = useState('');
    const [sent, setSent] = useState(false);
    const max = Number(S.current().me?.settings?.requestMaxMinutes ?? 480);
    if (sent)
        return _jsx("p", { className: "note", children: "Request sent. You get an email when it is answered; approved items appear under Requests." });
    return _jsxs("form", { className: "form", onSubmit: async (e) => { e.preventDefault(); setErr(''); try {
            await api(`/api/vault/items/${item.id}/requests`, { method: 'POST', body: { reason, ticket, minutes } });
            setSent(true);
            onDone();
        }
        catch (x) {
            setErr(x.message);
        } }, children: [_jsxs("p", { children: [item.requiresApproval ? 'This item needs approval before it is opened.' : 'You may see that this item exists, but not open it.', " Ask the vault's editors for access."] }), _jsxs("label", { children: [_jsx("span", { children: "Why do you need it?" }), _jsx("input", { value: reason, onChange: (e) => setReason(e.target.value), placeholder: "Replacing the firewall's certificate tonight" })] }), _jsxs("label", { children: [_jsx("span", { children: "Ticket (optional)" }), _jsx("input", { value: ticket, onChange: (e) => setTicket(e.target.value), placeholder: "SD-1042" })] }), _jsxs("label", { children: [_jsx("span", { children: "For how long" }), _jsx("select", { value: minutes, onChange: (e) => setMinutes(Number(e.target.value)), children: [15, 30, 60, 120, 240, 480, 1440].filter((m) => m <= max).map((m) => _jsx("option", { value: m, children: m < 60 ? `${m} minutes` : `${m / 60} hour${m > 60 ? 's' : ''}` }, m)) })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: reason.trim().length < 3, children: "Ask for access" }) })] });
}
function History({ item }) {
    const h = useLoad(`/api/vault/items/${item.id}/history`);
    const [open, setOpen] = useState({});
    const list = (h.data?.items ?? []);
    if (!list.length)
        return _jsx(Empty, { children: "No earlier versions." });
    return _jsx("ul", { className: "plain", children: list.map((v) => _jsxs("li", { children: ["Version ", v.revision, " by ", v.changedBy || 'someone', ", ", fmtDate(v.at), ' ', open[v.revision] ? _jsx(Fields, { type: item.type, data: open[v.revision], itemId: item.id }) : _jsx("button", { className: "small", onClick: async () => { const k = S.vaultKeys(item.vaultId)?.item; if (k)
                        setOpen({ ...open, [v.revision]: await C.decryptJson(k, v.dataEnc) }); }, children: "Show" })] }, v.revision)) });
}
function Activity({ item }) {
    const e = useLoad(`/api/vault/items/${item.id}/events`);
    const words = { created: 'created it', changed: 'changed it', revealed: 'showed', copied: 'copied', opened: 'opened', viewed: 'viewed', requested: 'asked for access', approved: 'approved access', denied: 'denied access', managed: 'started managing it', unmanaged: 'stopped managing it', rotated: 'changed the password', rotation_failed: 'password change failed', rotation_started: 'started a password change', deleted: 'deleted it', restored: 'restored it', moved_in: 'moved it here', exported: 'exported it' };
    const list = (e.data?.items ?? []);
    return list.length ? _jsx("ul", { className: "plain log", children: list.map((x) => _jsxs("li", { children: [_jsx("time", { children: fmtDate(x.at) }), " ", x.user, " ", words[x.action] ?? x.action, x.detail ? _jsxs("span", { className: "muted", children: [" \u00B7 ", x.detail] }) : null] }, x.id)) }) : _jsx(Empty, { children: "Nothing yet." });
}
/** One item, decrypted. */
function ItemView({ item, onChange, onEdit, folders }) {
    const [data, setData] = useState(null);
    const [t, setT] = useState('');
    const [tab, setTab] = useState('fields');
    const [err, setErr] = useState('');
    const [moving, setMoving] = useState(false);
    useEffect(() => { let live = true; setData(null); setErr(''); void (async () => { setT(await S.title(item)); try {
        const d = await S.data(item);
        if (live) {
            setData(d);
            if (d)
                S.note(item.id, 'viewed');
        }
    }
    catch (e) {
        if (live)
            setErr(e.message);
    } })(); return () => { live = false; }; }, [item.id, item.revision, item.dataEnc]); // eslint-disable-line react-hooks/exhaustive-deps
    const v = S.vaultById(item.vaultId);
    const folder = folders.find((f) => f.id === item.folderId);
    return _jsxs("article", { className: "item-view", children: [_jsxs("header", { children: [_jsx("span", { className: "type-chip", children: typeDef(item.type).label }), _jsx("h2", { children: t }), _jsxs("p", { className: "muted small", children: [v?.name, folder ? ' › ' + folder.name : '', " \u00B7 changed ", ago(item.updatedAt), " by ", item.updatedBy || '–', item.pwChangedAt ? ` · password set ${ago(item.pwChangedAt)}` : ''] })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), !item.canRead ? _jsx(RequestAccess, { item: item, onDone: onChange }) : !data ? _jsx("p", { className: "muted", children: S.vaultKeys(item.vaultId)?.item ? 'Decrypting…' : 'You do not hold this vault\'s key yet. A manager of the vault gives it to you when they next open the Vault.' }) : _jsxs(_Fragment, { children: [_jsxs("nav", { className: "tabs", "aria-label": "Item", children: [_jsx("button", { className: tab === 'fields' ? 'on' : '', onClick: () => setTab('fields'), children: "Details" }), _jsx("button", { className: tab === 'history' ? 'on' : '', onClick: () => setTab('history'), children: "Earlier versions" }), item.canEdit && _jsx("button", { className: tab === 'activity' ? 'on' : '', onClick: () => setTab('activity'), children: "Who used it" })] }), tab === 'fields' && _jsxs(_Fragment, { children: [_jsx(Fields, { type: item.type, data: data, itemId: item.id }), _jsx(Managed, { item: item, data: data, onChange: onChange }), item.assetId ? _jsxs("p", { className: "muted small", children: ["Linked to ", _jsx("a", { href: `#/assets/${item.assetId}`, children: "an asset" }), "."] }) : null, item.requiresApproval && _jsx("p", { className: "muted small", children: "Viewers of this vault must ask before opening it." }), item.canEdit && _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => onEdit(data), children: "Edit" }), _jsx("button", { onClick: () => setMoving(true), children: "Move to another vault" }), _jsx("button", { className: "danger", onClick: async () => { if (!window.confirm('Move this item to the trash? It can be restored for 30 days.'))
                                            return; try {
                                            await api(`/api/vault/items/${item.id}`, { method: 'DELETE' });
                                            onChange();
                                        }
                                        catch (e) {
                                            setErr(e.message);
                                        } }, children: "Delete" })] })] }), tab === 'history' && _jsx(History, { item: item }), tab === 'activity' && _jsx(Activity, { item: item })] }), moving && data && _jsx(Modal, { title: "Move to another vault", onClose: () => setMoving(false), children: _jsx(MoveForm, { item: item, data: data, title: t, onDone: () => { setMoving(false); onChange(); } }) })] });
}
function MoveForm({ item, data, title, onDone }) {
    const targets = S.current().vaults.filter((v) => v.id !== item.vaultId && S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item);
    const [to, setTo] = useState(targets[0]?.id ?? 0);
    const [err, setErr] = useState('');
    return _jsxs("form", { className: "form", onSubmit: async (e) => { e.preventDefault(); setErr(''); try {
            const enc = await S.encItem(to, title, data);
            await api(`/api/vault/items/${item.id}/move`, { method: 'POST', body: { ...enc, vaultId: to } });
            onDone();
        }
        catch (x) {
            setErr(x.message);
        } }, children: [targets.length === 0 ? _jsx(Empty, { children: "There is no other vault you can add items to." }) : _jsxs("label", { children: [_jsx("span", { children: "Vault" }), _jsx("select", { value: to, onChange: (e) => setTo(Number(e.target.value)), children: targets.map((v) => _jsx("option", { value: v.id, children: v.name }, v.id)) })] }), _jsx("p", { className: "muted small", children: "Earlier versions stay behind; the item is encrypted again with the other vault's key. Open requests for it are cancelled." }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: !targets.length, children: "Move" }) })] });
}
function AssetPick({ value, companyId, onChange }) {
    const [q, setQ] = useState('');
    const list = useLoad(`/api/assets?take=20&search=${encodeURIComponent(q)}${companyId ? `&company=${companyId}` : ''}`, [q, companyId]);
    const cur = useLoad(value ? `/api/assets/${value}` : null, [value]);
    const items = (list.data?.items ?? []);
    return _jsxs("div", { className: "asset-pick", children: [value ? _jsxs("p", { children: ["Linked to ", _jsx("strong", { children: String(cur.data?.name ?? cur.data?.asset?.name ?? 'asset ' + value) }), " ", _jsx("button", { type: "button", className: "small", onClick: () => onChange(null), children: "Remove the link" })] }) : null, _jsx("input", { placeholder: "Search assets by name or tag", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Find an asset" }), q && _jsxs("ul", { className: "plain picks", children: [items.slice(0, 8).map((a) => _jsx("li", { children: _jsxs("button", { type: "button", className: "link", onClick: () => { onChange(Number(a.id)); setQ(''); }, children: [a.name, " ", _jsx("span", { className: "muted", children: a.tag })] }) }, a.id)), items.length === 0 && _jsx("li", { className: "muted", children: "No asset found." })] })] });
}
/** Adding or changing an item; everything is encrypted here before it is saved. */
export function ItemForm({ item, data, onDone, defaults }) {
    const writable = S.current().vaults.filter((v) => S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item);
    const [vaultId, setVaultId] = useState(item?.vaultId ?? defaults?.vaultId ?? writable.find((v) => v.kind !== 'personal')?.id ?? writable[0]?.id ?? 0);
    const [type, setType] = useState(item?.type ?? defaults?.type ?? 'login');
    const [title, setTitle] = useState('');
    const [d, setD] = useState(data ? { ...data } : {});
    const [custom, setCustom] = useState(data ? customOf(data) : []);
    const [assetId, setAssetId] = useState(item?.assetId ?? defaults?.assetId ?? null);
    const [folderId, setFolderId] = useState(item?.folderId ?? null);
    const [approval, setApproval] = useState(!!item?.requiresApproval);
    const [gen, setGen] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const folders = useLoad(vaultId ? `/api/vault/vaults/${vaultId}/folders` : null, [vaultId]);
    const [folderNames, setFolderNames] = useState([]);
    useEffect(() => { void (async () => { const o = []; for (const f of (folders.data?.items ?? []))
        o.push({ id: f.id, name: await S.folderName(vaultId, f.nameEnc) }); setFolderNames(o); })(); }, [folders.data, vaultId]);
    useEffect(() => { if (item)
        void S.title(item).then(setTitle); }, [item]);
    const v = S.vaultById(vaultId);
    const t = typeDef(type);
    const set = (k, x) => setD({ ...d, [k]: x });
    const save = async () => {
        setBusy(true);
        setErr('');
        try {
            if (!title.trim())
                throw new Error('Give the item a name.');
            const body = {};
            for (const f of t.fields)
                if (d[f.k])
                    body[f.k] = d[f.k];
            const cs = custom.filter((c) => c.name.trim());
            if (cs.length)
                body.custom = JSON.stringify(cs);
            const enc = await S.encItem(vaultId, title.trim(), body);
            const pwChanged = !!item && (data?.password ?? '') !== (body.password ?? '');
            if (item) {
                await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: item.revision, assetId, folderId, requiresApproval: approval, passwordChanged: pwChanged } });
                onDone(item.id);
            }
            else {
                const r = await api(`/api/vault/vaults/${vaultId}/items`, { method: 'POST', body: { ...enc, type, assetId, folderId, requiresApproval: approval, hasPassword: !!body.password } });
                onDone(Number(r.id));
            }
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    if (writable.length === 0)
        return _jsx(Empty, { children: "You cannot add items to any vault yet. Ask an administrator to create a vault and add you, or create your personal vault under Vaults." });
    return _jsxs("form", { className: "form item-form", onSubmit: (e) => { e.preventDefault(); void save(); }, children: [!item && _jsxs("div", { className: "row", children: [_jsxs("label", { children: [_jsx("span", { children: "Vault" }), _jsx("select", { value: vaultId, onChange: (e) => { setVaultId(Number(e.target.value)); setFolderId(null); }, children: writable.map((x) => _jsxs("option", { value: x.id, children: [x.name, x.kind === 'personal' ? ' (only you)' : ''] }, x.id)) })] }), _jsxs("label", { children: [_jsx("span", { children: "Kind" }), _jsx("select", { value: type, onChange: (e) => setType(e.target.value), children: TYPES.map((x) => _jsx("option", { value: x.key, children: x.label }, x.key)) })] })] }), _jsxs("label", { children: [_jsx("span", { children: "Name" }), _jsx("input", { value: title, onChange: (e) => setTitle(e.target.value), placeholder: type === 'server' ? 'SQL01 local administrator' : type === 'wifi' ? 'Office Wi-Fi' : 'Microsoft 365 admin', autoFocus: true })] }), t.fields.map((f) => _jsxs("label", { children: [_jsx("span", { children: f.label }), f.kind === 'select' ? _jsxs("select", { value: d[f.k] ?? '', onChange: (e) => set(f.k, e.target.value), children: [_jsx("option", { value: "", children: "\u2013" }), f.options.map((o) => _jsx("option", { children: o }, o))] })
                        : f.multiline ? _jsx("textarea", { rows: f.k === 'privateKey' ? 6 : 3, value: d[f.k] ?? '', onChange: (e) => set(f.k, e.target.value), spellCheck: false })
                            : _jsxs("span", { className: "with-btn", children: [_jsx("input", { type: f.secret && f.kind !== 'totp' ? 'password' : 'text', autoComplete: "off", spellCheck: false, value: d[f.k] ?? '', placeholder: f.placeholder, onChange: (e) => set(f.k, e.target.value) }), f.kind === 'password' && _jsx("button", { type: "button", onClick: () => setGen(f.k), children: "Generate" })] }), f.kind === 'password' && d[f.k] ? _jsx(Strength, { pw: d[f.k] }) : null] }, f.k)), item?.managed && _jsx("p", { className: "muted small", children: "The password of this managed account is kept by DeskAsk and not in this form." }), _jsxs("fieldset", { className: "custom", children: [_jsx("legend", { children: "More fields" }), custom.map((c, i) => _jsxs("div", { className: "row", children: [_jsx("input", { "aria-label": "Field name", placeholder: "Name", value: c.name, onChange: (e) => setCustom(custom.map((x, j) => j === i ? { ...x, name: e.target.value } : x)) }), _jsx("input", { "aria-label": "Value", type: c.hidden ? 'password' : 'text', placeholder: "Value", value: c.value, onChange: (e) => setCustom(custom.map((x, j) => j === i ? { ...x, value: e.target.value } : x)) }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: c.hidden, onChange: (e) => setCustom(custom.map((x, j) => j === i ? { ...x, hidden: e.target.checked } : x)) }), " Hidden"] }), _jsx("button", { type: "button", onClick: () => setCustom(custom.filter((_, j) => j !== i)), children: "Remove" })] }, i)), _jsx("button", { type: "button", onClick: () => setCustom([...custom, { name: '', value: '', hidden: false }]), children: "Add a field" })] }), folderNames.length > 0 && _jsxs("label", { children: [_jsx("span", { children: "Folder" }), _jsxs("select", { value: folderId ?? '', onChange: (e) => setFolderId(e.target.value ? Number(e.target.value) : null), children: [_jsx("option", { value: "", children: "No folder" }), folderNames.map((f) => _jsx("option", { value: f.id, children: f.name }, f.id))] })] }), v?.kind !== 'personal' && _jsxs(_Fragment, { children: [_jsx("label", { children: _jsx("span", { children: "Asset" }) }), _jsx(AssetPick, { value: assetId, companyId: v?.kind === 'customer' ? v.companyId : null, onChange: setAssetId }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: approval, onChange: (e) => setApproval(e.target.checked) }), " Viewers must ask before opening it (editors and managers open it directly)"] })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", disabled: busy, children: busy ? 'Saving…' : item ? 'Save changes' : 'Add item' }), _jsx("button", { type: "button", onClick: () => onDone(), children: "Cancel" })] }), gen && _jsx(Modal, { title: "Generate a password", onClose: () => setGen(null), children: _jsx(Generator, { onUse: (p) => { set(gen, p); setGen(null); } }) })] });
}
/** All items: search and filter on the left, the chosen item on the right. */
export default function Items() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const session = S.useSession();
    const [vault, setVault] = useState(qs.get('vault') ?? '');
    const [type, setType] = useState('');
    const [q, setQ] = useState('');
    const [folder, setFolder] = useState('');
    const asset = qs.get('asset');
    const trash = qs.get('trash') === '1';
    const list = useLoad(`/api/vault/items${trash ? '?trash=true' : ''}`, [trash]);
    const assetItems = useLoad(asset ? `/api/vault/assets/${asset}/items` : null, [asset]);
    const all = useMemo(() => ((asset ? assetItems.data : list.data)?.items ?? []), [list.data, assetItems.data, asset]);
    const titles = S.useTitles(all);
    const [sel, setSel] = useState(qs.get('item') ? Number(qs.get('item')) : null);
    const [edit, setEdit] = useState(null);
    const [folders, setFolders] = useState([]);
    useEffect(() => {
        void (async () => {
            const o = [];
            for (const v of session.vaults)
                if (S.rank(v.role) >= 2)
                    for (const f of (await api(`/api/vault/vaults/${v.id}/folders`)).items)
                        o.push({ id: f.id, vaultId: v.id, name: await S.folderName(v.id, f.nameEnc) });
            setFolders(o);
        })().catch(() => undefined);
    }, [session.vaults]);
    const shown = all.filter((i) => (!vault || String(i.vaultId) === vault) && (!type || i.type === type) && (!folder || String(i.folderId) === folder)
        && (!q || (titles[i.id] ?? '').toLowerCase().includes(q.toLowerCase())))
        .sort((a, b) => (titles[a.id] ?? '').localeCompare(titles[b.id] ?? ''));
    const current = all.find((i) => i.id === sel) ?? null;
    const reload = () => { list.reload(); assetItems.reload(); };
    const canAdd = session.vaults.some((v) => S.rank(v.role) >= 3);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "dash-head", children: [_jsx("h1", { children: trash ? 'Trash' : asset ? 'Items for this asset' : 'All items' }), _jsxs("div", { className: "tools", children: [canAdd && !trash && _jsx("button", { className: "primary", onClick: () => setEdit({}), children: "Add item" }), _jsx("a", { href: trash ? '#/vault' : '#/vault?trash=1', children: trash ? 'Back to the items' : 'Trash' })] })] }), asset && _jsx("div", { className: "filter-chips", children: _jsx("a", { href: "#/vault", children: "This asset only \u2715" }) }), _jsxs("div", { className: "tools", children: [_jsx("input", { type: "search", placeholder: "Search by name", value: q, onChange: (e) => setQ(e.target.value), "aria-label": "Search items" }), _jsxs("select", { value: vault, onChange: (e) => { setVault(e.target.value); setFolder(''); }, "aria-label": "Vault", children: [_jsx("option", { value: "", children: "Every vault" }), session.vaults.map((v) => _jsxs("option", { value: v.id, children: [v.name, v.kind === 'personal' ? '' : ` (${KIND_WORDS[v.kind]})`] }, v.id))] }), vault && folders.some((f) => String(f.vaultId) === vault) && _jsxs("select", { value: folder, onChange: (e) => setFolder(e.target.value), "aria-label": "Folder", children: [_jsx("option", { value: "", children: "Every folder" }), folders.filter((f) => String(f.vaultId) === vault).map((f) => _jsx("option", { value: f.id, children: f.name }, f.id))] }), _jsxs("select", { value: type, onChange: (e) => setType(e.target.value), "aria-label": "Kind", children: [_jsx("option", { value: "", children: "Every kind" }), TYPES.map((t) => _jsx("option", { value: t.key, children: t.plural }, t.key))] })] }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), _jsxs("div", { className: "vault-split", children: [_jsxs("ul", { className: "item-list", "aria-label": "Items", children: [shown.map((i) => _jsx("li", { children: _jsxs("button", { className: sel === i.id ? 'on' : '', onClick: () => setSel(i.id), children: [_jsx("span", { className: 'type-dot t-' + i.type, "aria-hidden": "true", children: GLYPH[i.type] ?? '•' }), _jsxs("span", { className: "name", children: [titles[i.id] ?? '…', _jsxs("small", { children: [S.vaultById(i.vaultId)?.name, !i.canRead ? ' · ask to open' : '', i.managed ? ' · managed' : ''] })] })] }) }, i.id)), list.data && shown.length === 0 && _jsx("li", { className: "muted pad", children: all.length ? 'Nothing matches.' : trash ? 'The trash is empty.' : 'No items yet.' })] }), _jsx("div", { className: "item-pane", children: trash && current ? _jsxs("article", { className: "item-view", children: [_jsx("h2", { children: titles[current.id] }), _jsxs("p", { className: "muted", children: ["Deleted ", ago(current.deletedAt), ". Items stay in the trash for 30 days."] }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: async () => { await api(`/api/vault/items/${current.id}/restore`, { method: 'POST', body: {} }); reload(); setSel(null); }, children: "Restore" }), _jsx("button", { className: "danger", onClick: async () => { if (window.confirm('Delete for good? This cannot be undone.')) {
                                                await api(`/api/vault/items/${current.id}?purge=true`, { method: 'DELETE' });
                                                reload();
                                                setSel(null);
                                            } }, children: "Delete for good" })] })] })
                            : current ? _jsx(ItemView, { item: current, folders: folders, onChange: reload, onEdit: (d) => setEdit({ item: current, data: d }) }, current.id)
                                : _jsx("p", { className: "muted pad", children: "Choose an item to see it." }) })] }), edit && _jsx(Modal, { title: edit.item ? 'Change item' : 'Add an item', onClose: () => setEdit(null), children: _jsx(ItemForm, { item: edit.item, data: edit.data, defaults: { vaultId: vault ? Number(vault) : undefined, assetId: asset ? Number(asset) : undefined }, onDone: (id) => { setEdit(null); reload(); if (id)
                        setSel(id); } }) })] });
}
