import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api } from '../http';
import { copyText, Empty } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { customOf, typeDef } from './types';
import { Strength } from './Gate';
/** The password generator; with onUse it fills a field, on its own it copies. */
export function Generator({ onUse }) {
    const [o, setO] = useState(C.DEFAULT_GEN);
    const [pw, setPw] = useState(() => C.generate(C.DEFAULT_GEN));
    const next = (x) => { setO(x); setPw(C.generate(x)); };
    return _jsxs("div", { className: "generator", children: [_jsx("p", { className: "gen-out", children: _jsx("code", { children: pw }) }), _jsx(Strength, { pw: pw }), _jsxs("label", { children: [_jsxs("span", { children: ["Length: ", o.length] }), _jsx("input", { type: "range", min: 8, max: 64, value: o.length, onChange: (e) => next({ ...o, length: Number(e.target.value) }) })] }), _jsx("div", { className: "checks", children: [['upper', 'A–Z'], ['lower', 'a–z'], ['digits', '0–9'], ['symbols', '!#$%…'], ['avoidAmbiguous', 'Avoid look-alikes (O 0 l 1)']].map(([k, l]) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: !!o[k], onChange: (e) => next({ ...o, [k]: e.target.checked }) }), " ", l] }, k)) }), _jsxs("div", { className: "tools", children: [_jsx("button", { type: "button", onClick: () => setPw(C.generate(o)), children: "Another one" }), onUse ? _jsx("button", { type: "button", className: "primary", onClick: () => onUse(pw), children: "Use this password" }) : _jsx("button", { type: "button", className: "primary", onClick: () => copyText(pw), children: "Copy" })] })] });
}
/** Splits CSV text into rows (quotes, doubled quotes and line breaks inside quotes). */
export function csv(text) {
    const rows = [];
    let row = [];
    let cell = '';
    let q = false;
    for (let i = 0; i < text.length; i++) {
        const ch = text[i];
        if (q) {
            if (ch === '"' && text[i + 1] === '"') {
                cell += '"';
                i++;
            }
            else if (ch === '"')
                q = false;
            else
                cell += ch;
        }
        else if (ch === '"')
            q = true;
        else if (ch === ',') {
            row.push(cell);
            cell = '';
        }
        else if (ch === '\n' || ch === '\r') {
            if (ch === '\r' && text[i + 1] === '\n')
                i++;
            row.push(cell);
            cell = '';
            if (row.some((c) => c !== ''))
                rows.push(row);
            row = [];
        }
        else
            cell += ch;
    }
    row.push(cell);
    if (row.some((c) => c !== ''))
        rows.push(row);
    return rows;
}
/** Bitwarden JSON export (not the encrypted kind), Bitwarden CSV, or any CSV with name/url/username/password columns (Chrome, Edge, KeePass, LastPass). */
export function parseImport(text) {
    const t = text.trim();
    if (t.startsWith('{')) {
        const j = JSON.parse(t);
        if (j.encrypted)
            throw new Error('This is an encrypted Bitwarden export. Export again as "JSON" (not "JSON (Encrypted)").');
        const folders = new Map((j.folders ?? []).map((f) => [String(f.id), String(f.name)]));
        return (j.items ?? []).map((it) => {
            const custom = (it.fields ?? []).map((f) => ({ name: String(f.name ?? ''), value: String(f.value ?? ''), hidden: f.type === 1 }));
            const d = {};
            let type = 'login';
            if (it.type === 1) {
                d.username = it.login?.username ?? '';
                d.password = it.login?.password ?? '';
                d.totp = it.login?.totp ?? '';
                d.url = it.login?.uris?.[0]?.uri ?? '';
                const more = (it.login?.uris ?? []).slice(1);
                more.forEach((u, i) => custom.push({ name: `Web address ${i + 2}`, value: String(u.uri), hidden: false }));
            }
            else if (it.type === 2) {
                type = 'note';
            }
            else if (it.type === 3) {
                type = 'card';
                const c = it.card ?? {};
                Object.assign(d, { cardholder: c.cardholderName ?? '', brand: c.brand ?? '', number: c.number ?? '', expiry: c.expMonth ? `${String(c.expMonth).padStart(2, '0')}/${String(c.expYear ?? '').slice(-2)}` : '', cvv: c.code ?? '' });
            }
            else if (it.type === 4) {
                type = 'identity';
                const c = it.identity ?? {};
                Object.assign(d, { fullName: [c.firstName, c.middleName, c.lastName].filter(Boolean).join(' '), email: c.email ?? '', phone: c.phone ?? '', company: c.company ?? '', address: [c.address1, c.address2, c.city, c.state, c.postalCode, c.country].filter(Boolean).join('\n') });
            }
            else if (it.type === 5) {
                type = 'ssh';
                d.privateKey = it.sshKey?.privateKey ?? '';
                d.publicKey = it.sshKey?.publicKey ?? '';
            }
            if (it.notes)
                d.notes = String(it.notes);
            if (custom.length)
                d.custom = JSON.stringify(custom);
            for (const k of Object.keys(d))
                if (!d[k])
                    delete d[k];
            return { title: String(it.name || 'Imported item'), type, data: d, folder: it.folderId ? folders.get(String(it.folderId)) : undefined };
        });
    }
    const rows = csv(t);
    if (rows.length < 2)
        throw new Error('The file has no rows.');
    const head = rows[0].map((h) => h.trim().toLowerCase());
    const col = (...names) => { for (const n of names) {
        const i = head.indexOf(n);
        if (i >= 0)
            return i;
    } return -1; };
    const c = { name: col('name', 'title', 'account'), url: col('login_uri', 'url', 'website', 'web site'), user: col('login_username', 'username', 'user name', 'login', 'email'), pw: col('login_password', 'password'), totp: col('login_totp', 'totp', 'otpauth'), notes: col('notes', 'note', 'extra', 'comments'), folder: col('folder', 'group', 'grouping'), type: col('type') };
    if (c.name < 0 && c.url < 0)
        throw new Error('No "name" or "url" column found. The first line must name the columns.');
    return rows.slice(1).map((r) => {
        const g = (i) => (i >= 0 ? (r[i] ?? '').trim() : '');
        const note = g(c.type) === 'note';
        const d = note ? { notes: g(c.notes) } : { username: g(c.user), password: g(c.pw), url: g(c.url), totp: g(c.totp), notes: g(c.notes) };
        for (const k of Object.keys(d))
            if (!d[k])
                delete d[k];
        let title = g(c.name);
        if (!title && d.url) {
            try {
                title = new URL(/^https?:/i.test(d.url) ? d.url : 'https://' + d.url).hostname;
            }
            catch {
                title = d.url;
            }
        }
        return { title: title || 'Imported item', type: note ? 'note' : 'login', data: d, folder: g(c.folder).replace(/^\/+/, '') || undefined };
    });
}
function Import() {
    const writable = S.current().vaults.filter((v) => S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item).sort((a, b) => Number(a.kind === 'personal') - Number(b.kind === 'personal'));
    const [vid, setVid] = useState(writable[0]?.id ?? 0);
    const [drafts, setDrafts] = useState(null);
    const [err, setErr] = useState('');
    const [msg, setMsg] = useState('');
    const [busy, setBusy] = useState(false);
    if (!writable.length)
        return _jsx(Empty, { children: "You cannot add items to any vault." });
    const run = async () => {
        if (!drafts)
            return;
        setBusy(true);
        setErr('');
        try {
            const folderIds = new Map();
            const existing = (await api(`/api/vault/vaults/${vid}/folders`)).items;
            for (const f of existing)
                folderIds.set((await S.folderName(vid, f.nameEnc)).toLowerCase(), Number(f.id));
            let n = 0;
            for (const d of drafts) {
                let folderId = null;
                if (d.folder) {
                    const key = d.folder.toLowerCase();
                    if (!folderIds.has(key))
                        folderIds.set(key, Number((await api(`/api/vault/vaults/${vid}/folders`, { method: 'POST', body: { nameEnc: await S.encFolder(vid, d.folder) } })).id));
                    folderId = folderIds.get(key);
                }
                const enc = await S.encItem(vid, d.title, d.data);
                await api(`/api/vault/vaults/${vid}/items`, { method: 'POST', body: { ...enc, type: d.type, folderId, hasPassword: !!d.data.password } });
                n++;
                setMsg(`Imported ${n} of ${drafts.length}…`);
            }
            setMsg(`Imported ${n} items.`);
            setDrafts(null);
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsxs("section", { className: "card", children: [_jsx("h2", { children: "Import" }), _jsx("p", { className: "muted", children: "From Bitwarden (Tools \u203A Export vault, format \"JSON\" or \"CSV\"), or a CSV export of Chrome, Edge, KeePass or LastPass. The file is read and encrypted on this computer; delete the export file afterwards." }), _jsxs("div", { className: "tools", children: [_jsx("select", { value: vid, onChange: (e) => setVid(Number(e.target.value)), "aria-label": "Into vault", children: writable.map((v) => _jsx("option", { value: v.id, children: v.name }, v.id)) }), _jsx("input", { type: "file", accept: ".json,.csv,text/csv,application/json", "aria-label": "Export file", onChange: async (e) => { setErr(''); setMsg(''); const f = e.target.files?.[0]; if (!f)
                            return; try {
                            setDrafts(parseImport(await f.text()));
                        }
                        catch (x) {
                            setDrafts(null);
                            setErr(x.message);
                        } } })] }), drafts && _jsxs(_Fragment, { children: [_jsxs("p", { children: [drafts.length, " items found: ", Object.entries(drafts.reduce((a, d) => ({ ...a, [d.type]: (a[d.type] ?? 0) + 1 }), {})).map(([k, n]) => `${n} ${typeDef(k).plural.toLowerCase()}`).join(', '), "."] }), _jsxs("ul", { className: "plain small", children: [drafts.slice(0, 8).map((d, i) => _jsxs("li", { children: [d.title, d.folder ? _jsxs("span", { className: "muted", children: [" \u00B7 ", d.folder] }) : null] }, i)), drafts.length > 8 && _jsxs("li", { className: "muted", children: ["and ", drafts.length - 8, " more"] })] }), _jsxs("div", { className: "tools", children: [_jsxs("button", { className: "primary", disabled: busy, onClick: () => void run(), children: ["Import ", drafts.length, " items"] }), _jsx("button", { onClick: () => setDrafts(null), children: "Cancel" })] })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), msg && _jsx("p", { className: "muted", role: "status", children: msg })] });
}
function Export() {
    const [pw, setPw] = useState('');
    const [fmt, setFmt] = useState('enc');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const run = async () => {
        setBusy(true);
        setErr('');
        try {
            const items = (await api('/api/vault/items')).items.filter((i) => i.canRead && i.dataEnc);
            const out = [];
            for (const i of items) {
                const d = await S.data(i);
                if (!d)
                    continue;
                out.push({ vault: S.vaultById(i.vaultId)?.name, type: i.type, title: await S.title(i), ...d });
                S.note(i.id, 'exported');
            }
            let blob;
            let name;
            if (fmt === 'enc') {
                if (pw.length < 12)
                    throw new Error('Use a password of at least 12 characters for the export.');
                const kdf = C.newKdf();
                blob = new Blob([JSON.stringify({ format: 'deskask-vault-export', v: 1, kdf, data: await C.encryptJson(await C.masterKey(pw, kdf), out) })], { type: 'application/json' });
                name = 'deskask-vault-export.json';
            }
            else {
                const esc = (s) => `"${String(s ?? '').replace(/"/g, '""')}"`;
                const rows = [['folder', 'type', 'name', 'login_uri', 'login_username', 'login_password', 'login_totp', 'notes'].join(',')];
                for (const o of out)
                    rows.push([o.vault, o.type === 'note' ? 'note' : 'login', o.title, o.url ?? o.host ?? '', o.username ?? '', o.password ?? o.secret ?? '', o.totp ?? '', [o.notes, ...customOf(o).map((c) => `${c.name}: ${c.value}`)].filter(Boolean).join('\n')].map(esc).join(','));
                blob = new Blob([rows.join('\n')], { type: 'text/csv' });
                name = 'deskask-vault-export.csv';
            }
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = name;
            a.click();
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    return _jsxs("section", { className: "card form", children: [_jsx("h2", { children: "Export" }), _jsx("p", { className: "muted", children: "Everything you can open, in one file. The export is written to the item logs." }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: fmt === 'enc', onChange: () => setFmt('enc') }), " Encrypted with a password of your choice (keep for a backup)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: fmt === 'csv', onChange: () => setFmt('csv') }), " Plain CSV (anyone with the file can read every password)"] }), fmt === 'enc' && _jsxs("label", { children: [_jsx("span", { children: "Export password" }), _jsx("input", { type: "password", value: pw, onChange: (e) => setPw(e.target.value), autoComplete: "new-password" })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: busy, onClick: () => void run(), children: "Export" }) })] });
}
export default function Tools() {
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Generator, import and export" }), _jsxs("div", { className: "grid", children: [_jsxs("section", { className: "card", children: [_jsx("h2", { children: "Password generator" }), _jsx(Generator, {})] }), _jsx(Import, {}), _jsx(Export, {})] })] });
}
