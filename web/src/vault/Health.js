import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useState } from 'react';
import { api, apiText } from '../http';
import { Empty, Loading } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { passwordFields, typeDef } from './types';
const DAY = 86_400_000;
/** Everything here is worked out in this browser: the server never sees the passwords. The breach check sends only 5 characters of each password's SHA-1 hash. */
export default function Health() {
    const session = S.useSession();
    const [rows, setRows] = useState(null);
    const [err, setErr] = useState('');
    const [vault, setVault] = useState('');
    const [breach, setBreach] = useState({});
    const [checking, setChecking] = useState('');
    const [breachOff, setBreachOff] = useState('');
    useEffect(() => {
        void (async () => {
            try {
                const items = (await api('/api/vault/items')).items.filter((i) => i.canRead && i.dataEnc);
                const out = [];
                for (const i of items) {
                    const d = await S.data(i);
                    if (!d)
                        continue;
                    const t = await S.title(i);
                    const since = new Date(String(i.pwChangedAt ?? i.createdAt)).getTime();
                    for (const f of passwordFields(i.type))
                        if (d[f])
                            out.push({ id: i.id, vaultId: i.vaultId, title: t, type: i.type, field: typeDef(i.type).fields.find((x) => x.k === f).label, pw: d[f], age: Math.floor((Date.now() - since) / DAY), managed: i.managed });
                }
                setRows(out);
            }
            catch (e) {
                setErr(e.message);
            }
        })();
    }, [session.vaults.length]);
    const shown = useMemo(() => (rows ?? []).filter((r) => !vault || String(r.vaultId) === vault), [rows, vault]);
    const counts = useMemo(() => { const m = new Map(); for (const r of rows ?? [])
        m.set(r.pw, (m.get(r.pw) ?? 0) + 1); return m; }, [rows]);
    const weak = shown.filter((r) => C.strength(r.pw).score <= 1);
    const reused = shown.filter((r) => (counts.get(r.pw) ?? 0) > 1);
    const old = shown.filter((r) => r.age > 365 && !r.managed);
    const breached = shown.filter((r) => (breach[r.pw] ?? 0) > 0);
    const bad = new Set([...weak, ...reused, ...old, ...breached].map((r) => r.id + r.field));
    const score = shown.length ? Math.round(100 * (1 - bad.size / shown.length)) : 100;
    const check = async () => {
        setBreachOff('');
        const found = { ...breach };
        const todo = [...new Set(shown.map((r) => r.pw))].filter((p) => !(p in found));
        let n = 0;
        try {
            for (const p of todo) {
                const h = await C.sha1Hex(p);
                found[p] = C.breachCount(await apiText(`/api/vault/breach/${h.slice(0, 5)}`), h);
                n++;
                setChecking(`Checked ${n} of ${todo.length}…`);
            }
            setBreach(found);
            setChecking('');
        }
        catch (e) {
            setChecking('');
            setBreachOff(e.message);
        }
    };
    const List = ({ title, items, why }) => _jsxs("section", { className: "card health-list", children: [_jsxs("h2", { children: [title, " ", _jsx("span", { className: "count", children: items.length })] }), items.length === 0 ? _jsx(Empty, { children: "None." }) : _jsx("ul", { className: "plain", children: items.map((r) => _jsxs("li", { children: [_jsx("a", { href: `#/vault?item=${r.id}`, children: r.title }), " ", _jsxs("span", { className: "muted small", children: [r.field, " \u00B7 ", S.vaultById(r.vaultId)?.name, " \u00B7 ", why(r)] })] }, r.id + r.field)) })] });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Password health" }), _jsx(Loading, { loading: !rows && !err, error: err }), rows && _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsxs("select", { value: vault, onChange: (e) => setVault(e.target.value), "aria-label": "Vault", children: [_jsx("option", { value: "", children: "Every vault I can open" }), session.vaults.map((v) => _jsx("option", { value: v.id, children: v.name }, v.id))] }), session.me?.settings?.breachCheck !== false && _jsx("button", { onClick: () => void check(), disabled: !!checking, children: checking || 'Check against known breaches' })] }), breachOff && _jsx("p", { className: "err", role: "alert", children: breachOff }), _jsxs("div", { className: "health-head", children: [_jsxs("p", { className: "health-score", children: [_jsx("b", { children: score }), _jsx("span", { children: "out of 100" })] }), _jsxs("dl", { className: "stats", children: [_jsxs("div", { children: [_jsx("dt", { children: "Passwords" }), _jsx("dd", { children: shown.length })] }), _jsxs("div", { children: [_jsx("dt", { children: "Weak" }), _jsx("dd", { children: weak.length })] }), _jsxs("div", { children: [_jsx("dt", { children: "Used more than once" }), _jsx("dd", { children: reused.length })] }), _jsxs("div", { children: [_jsx("dt", { children: "Older than a year" }), _jsx("dd", { children: old.length })] }), _jsxs("div", { children: [_jsx("dt", { children: "Found in breaches" }), _jsx("dd", { children: Object.keys(breach).length ? breached.length : '–' })] })] })] }), _jsxs("div", { className: "grid", children: [Object.keys(breach).length > 0 && _jsx(List, { title: "Found in known breaches", items: breached, why: (r) => `seen ${breach[r.pw].toLocaleString()} times` }), _jsx(List, { title: "Weak", items: weak, why: (r) => C.strength(r.pw).label.toLowerCase() }), _jsx(List, { title: "Used more than once", items: reused, why: (r) => `in ${counts.get(r.pw)} places` }), _jsx(List, { title: "Not changed for over a year", items: old, why: (r) => `${Math.floor(r.age / 30)} months old` })] }), _jsx("p", { className: "muted small", children: "Only items you can open are counted. Managed accounts are changed by DeskAsk on schedule and are not counted as old." })] })] });
}
