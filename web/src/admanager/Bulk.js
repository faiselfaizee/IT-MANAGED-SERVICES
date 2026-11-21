import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { csv } from '../vault/Tools';
import { Badge, Card, Empty, Err } from '../ui';
import { ALL_FIELDS, applyName, cleanSam } from './fields';
import { OuPicker } from './pickers';
import { act } from './Objects';
const NAME_COLS = ['first', 'last', 'initials', 'display', 'sam', 'upn', 'email', 'ou', 'password', 'groups', 'manager', 'expires'];
const ATTR_COLS = ALL_FIELDS.map((f) => f.k);
const known = new Map([...NAME_COLS, ...ATTR_COLS].map((c) => [c.toLowerCase(), c]));
// the column names people use in their sheets
const ALIAS = { firstname: 'first', 'first name': 'first', givenname: 'first', lastname: 'last', 'last name': 'last', surname: 'last', sn: 'last',
    displayname: 'display', samaccountname: 'sam', username: 'sam', 'user name': 'sam', login: 'sam', mail: 'email', userprincipalname: 'upn', jobtitle: 'title', phone: 'telephoneNumber', city: 'l', country: 'co' };
function readSheet(text) {
    const t = csv(text).filter((r) => r.some((c) => c.trim() !== ''));
    if (t.length < 2)
        return { rows: [], unknown: [] };
    const head = t[0].map((h) => { const k = h.trim().toLowerCase(); return ALIAS[k] ?? known.get(k) ?? ''; });
    const unknown = t[0].filter((_, i) => head[i] === '').map((h) => h.trim()).filter(Boolean);
    return { unknown, rows: t.slice(1).map((r) => { const o = {}; head.forEach((k, i) => { if (k && (r[i] ?? '').trim() !== '')
            o[k] = r[i].trim(); }); return o; }) };
}
function saveCsv(name, rows) {
    const text = rows.map((r) => r.map((c) => (/[",\n\r]/.test(c) ? '"' + c.replace(/"/g, '""') + '"' : /^[=+\-@]/.test(c) ? "'" + c : c)).join(',')).join('\r\n');
    const a = document.createElement('a');
    a.href = URL.createObjectURL(new Blob(['﻿' + text], { type: 'text/csv' }));
    a.download = name;
    a.click();
}
/** Create many accounts from a sheet: read it, check every row with the directory, then create them one by one. */
export function BulkCreate({ domain }) {
    const templates = useLoad(`/api/adsuite/domains/${domain.id}/templates`);
    const [rows, setRows] = useState([]);
    const [unknown, setUnknown] = useState([]);
    const [tid, setTid] = useState(0);
    const [ou, setOu] = useState('');
    const [check, setCheck] = useState(null);
    const [out, setOut] = useState([]);
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState('');
    const tpl = (templates.data ?? []).find((t) => t.id === tid);
    // a row as it will be sent: the sheet's values first, then the template's, then the defaults
    const shape = (r) => {
        const n = (tpl?.naming ?? {});
        const first = r.first ?? '', last = r.last ?? '', ini = r.initials ?? '';
        const sam = r.sam ?? cleanSam(applyName(String(n.sam || '{first}.{last}'), first, last, ini)).toLowerCase();
        const attrs = { ...(tpl?.attrs ?? {}) };
        for (const k of ATTR_COLS)
            if (r[k])
                attrs[k] = r[k];
        if (ini)
            attrs.initials = ini;
        return { action: 'create-user', ou: r.ou || ou || tpl?.ou || '', first, last, sam, display: r.display ?? applyName(String(n.display || '{first} {last}'), first, last, ini),
            upn: r.upn ?? (n.upnSuffix ? `${sam}@${n.upnSuffix}` : ''), email: r.email ?? (n.email ? applyName(String(n.email), first, last, ini).toLowerCase() : ''),
            password: r.password ?? '', expires: r.expires ?? '', mustChange: tpl?.options?.mustChange ?? true, pwdNeverExpires: !!tpl?.options?.pwdNeverExpires,
            groups: [...(tpl?.groups ?? []), ...String(r.groups ?? '').split(';').map((g) => g.trim()).filter((g) => g.includes('='))], attrs };
    };
    const load = async (f) => { const s = readSheet(await f.text()); setRows(s.rows); setUnknown(s.unknown); setCheck(null); setOut([]); setErr(s.rows.length ? '' : 'The file has no rows under its header.'); };
    const doCheck = async () => { setBusy(true); setErr(''); try {
        setCheck(await api(`/api/adsuite/domains/${domain.id}/bulk/check`, { method: 'POST', body: { mode: 'create', rows: rows.map(shape) } }));
    }
    catch (e) {
        setErr(e instanceof Error ? e.message : String(e));
    }
    finally {
        setBusy(false);
    } };
    const run = async () => {
        setBusy(true);
        const res = [];
        const good = (check?.rows ?? []).filter((r) => r.ok).map((r) => Number(r.row) - 1);
        for (const i of good) {
            const b = shape(rows[i]);
            try {
                const r = await act(domain.id, b);
                res.push({ row: i + 1, sam: b.sam, ok: true, dn: r.dn, password: r.password ?? '' });
            }
            catch (e) {
                res.push({ row: i + 1, sam: b.sam, ok: false, error: e instanceof Error ? e.message : String(e) });
            }
            setOut([...res]);
        }
        setBusy(false);
    };
    const bad = (check?.rows ?? []).filter((r) => !r.ok);
    return _jsxs(Card, { title: "Create users from a file", children: [_jsxs("p", { className: "muted", children: ["A CSV file with a header row. Columns: ", _jsx("code", { children: "first, last, sam, email, ou, password, groups, expires" }), " and any field such as ", _jsx("code", { children: "title, department, telephoneNumber, l, company" }), ". Empty values come from the template. ", _jsx("code", { children: "groups" }), " holds group DNs separated by ", _jsx("code", { children: ";" }), ". A row without a password gets a generated one, listed at the end."] }), _jsx("p", { children: _jsx("button", { className: "link", onClick: () => saveCsv('new-users-sample.csv', [['first', 'last', 'sam', 'email', 'title', 'department', 'ou', 'groups'], ['Asha', 'Nair', 'asha.nair', 'asha.nair@' + String(domain.dnsName), 'Accountant', 'Finance', '', '']]), children: "Download a sample file" }) }), _jsxs("div", { className: "filters", children: [_jsxs("label", { className: "inline", children: ["Template ", _jsxs("select", { value: tid, onChange: (e) => { setTid(Number(e.target.value)); setCheck(null); }, children: [_jsx("option", { value: 0, children: "None" }), (templates.data ?? []).map((t) => _jsx("option", { value: t.id, children: t.name }, t.id))] })] }), _jsxs("div", { className: "scope", children: [_jsx("span", { className: "lbl", children: "OU for rows without one" }), _jsx(OuPicker, { domain: domain, value: ou, onChange: (v) => { setOu(v); setCheck(null); } })] }), _jsx("input", { type: "file", accept: ".csv,text/csv", "aria-label": "CSV file", onChange: (e) => e.target.files?.[0] && void load(e.target.files[0]) })] }), unknown.length > 0 && _jsxs("p", { className: "muted small", children: ["Columns not used: ", unknown.join(', ')] }), _jsx(Err, { text: err }), rows.length > 0 && !check && _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy, onClick: () => void doCheck(), children: busy ? 'Checking…' : `Check ${rows.length} row(s)` }) }), check && _jsxs(_Fragment, { children: [_jsxs("p", { children: [_jsxs(Badge, { tone: "ok", children: [String(check.ok), " ready"] }), " ", bad.length > 0 && _jsxs(Badge, { tone: "bad", children: [bad.length, " with problems"] })] }), bad.length > 0 && _jsxs("table", { className: "adtable small", children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Row" }), _jsx("th", { children: "Sign-in" }), _jsx("th", { children: "Problem" })] }) }), _jsx("tbody", { children: bad.map((r) => _jsxs("tr", { children: [_jsx("td", { children: String(r.row) }), _jsx("td", { children: String(r.sam) }), _jsx("td", { className: "err", children: r.problems.join('; ') })] }, String(r.row))) })] }), out.length === 0 && Number(check.ok) > 0 && _jsxs("div", { className: "actions", children: [_jsxs("button", { className: "primary", disabled: busy, onClick: () => void run(), children: ["Create ", String(check.ok), " account(s)"] }), bad.length > 0 && _jsx("span", { className: "muted small", children: "Rows with problems are skipped." })] })] }), out.length > 0 && _jsxs(_Fragment, { children: [_jsx("h3", { children: busy ? `Creating… ${out.length} of ${String(check?.ok)}` : 'Finished' }), _jsxs("p", { children: [out.filter((o) => o.ok).length, " created, ", out.filter((o) => !o.ok).length, " failed."] }), out.some((o) => !o.ok) && _jsx("ul", { className: "plain small", children: out.filter((o) => !o.ok).map((o) => _jsxs("li", { children: ["Row ", String(o.row), " (", String(o.sam), "): ", _jsx("span", { className: "err", children: String(o.error) })] }, String(o.row))) }), !busy && _jsx("button", { className: "primary", onClick: () => saveCsv('created-users.csv', [['row', 'sign-in name', 'result', 'password (shown once)'], ...out.map((o) => [String(o.row), String(o.sam), o.ok ? 'created' : String(o.error), String(o.password ?? '')])]), children: "Download the results with passwords" })] })] });
}
/** Change many accounts from a file: the sign-in name finds the account, the other columns are the new values. */
export function BulkModify({ domain }) {
    const [rows, setRows] = useState([]);
    const [check, setCheck] = useState(null);
    const [out, setOut] = useState([]);
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState('');
    const attrsOf = (r) => { const a = {}; for (const k of [...ATTR_COLS, 'email', 'first', 'last', 'display'])
        if (r[k] !== undefined)
            a[k === 'email' ? 'mail' : k === 'first' ? 'givenName' : k === 'last' ? 'sn' : k === 'display' ? 'displayName' : k] = r[k] === '-' ? '' : r[k]; return a; };
    const load = async (f) => { const s = readSheet(await f.text()); setRows(s.rows); setCheck(null); setOut([]); setErr(s.rows.length ? '' : 'The file has no rows under its header.'); };
    const doCheck = async () => { setBusy(true); setErr(''); try {
        setCheck(await api(`/api/adsuite/domains/${domain.id}/bulk/check`, { method: 'POST', body: { mode: 'modify', rows: rows.map((r) => ({ sam: r.sam ?? '', attrs: Object.fromEntries(Object.keys(attrsOf(r)).filter((k) => !['givenName', 'sn', 'displayName', 'mail'].includes(k)).map((k) => [k, ''])) })) } }));
    }
    catch (e) {
        setErr(e instanceof Error ? e.message : String(e));
    }
    finally {
        setBusy(false);
    } };
    const run = async () => {
        setBusy(true);
        const res = [];
        for (const c of (check?.rows ?? []).filter((r) => r.ok)) {
            const r = rows[Number(c.row) - 1];
            const attrs = attrsOf(r);
            try {
                if (Object.keys(attrs).length)
                    await act(domain.id, { action: 'update', dn: c.dn, attrs });
                res.push({ row: c.row, sam: c.sam, ok: true });
            }
            catch (e) {
                res.push({ row: c.row, sam: c.sam, ok: false, error: e instanceof Error ? e.message : String(e) });
            }
            setOut([...res]);
        }
        setBusy(false);
    };
    const bad = (check?.rows ?? []).filter((r) => !r.ok);
    return _jsxs(Card, { title: "Change users from a file", children: [_jsxs("p", { className: "muted", children: ["A CSV file with ", _jsx("code", { children: "sam" }), " (the sign-in name) and the columns to change, for example ", _jsx("code", { children: "sam, title, department, telephoneNumber, l, company" }), ". An empty cell leaves the value alone; ", _jsx("code", { children: "-" }), " clears it."] }), _jsx("input", { type: "file", accept: ".csv,text/csv", "aria-label": "CSV file", onChange: (e) => e.target.files?.[0] && void load(e.target.files[0]) }), _jsx(Err, { text: err }), rows.length > 0 && !check && _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy, onClick: () => void doCheck(), children: busy ? 'Checking…' : `Check ${rows.length} row(s)` }) }), check && _jsxs(_Fragment, { children: [_jsxs("p", { children: [_jsxs(Badge, { tone: "ok", children: [String(check.ok), " found"] }), " ", bad.length > 0 && _jsxs(Badge, { tone: "bad", children: [bad.length, " with problems"] })] }), bad.length > 0 && _jsx("table", { className: "adtable small", children: _jsx("tbody", { children: bad.map((r) => _jsxs("tr", { children: [_jsxs("td", { children: ["Row ", String(r.row)] }), _jsx("td", { children: String(r.sam) }), _jsx("td", { className: "err", children: r.problems.join('; ') })] }, String(r.row))) }) }), out.length === 0 && Number(check.ok) > 0 && _jsx("div", { className: "actions", children: _jsxs("button", { className: "primary", disabled: busy, onClick: () => void run(), children: ["Change ", String(check.ok), " account(s)"] }) })] }), out.length > 0 && _jsxs("p", { children: [busy ? 'Changing… ' : 'Finished: ', out.filter((o) => o.ok).length, " changed, ", out.filter((o) => !o.ok).length, " failed."] }), out.some((o) => !o.ok) && _jsx("ul", { className: "plain small", children: out.filter((o) => !o.ok).map((o) => _jsxs("li", { children: ["Row ", String(o.row), " (", String(o.sam), "): ", _jsx("span", { className: "err", children: String(o.error) })] }, String(o.row))) }), rows.length === 0 && _jsx(Empty, { children: "Choose a file to begin." })] });
}
