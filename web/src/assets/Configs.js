import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, saveFile, useLoad } from '../http';
import { Act, Badge, Card, Empty, Err, Loading, ago, fmtBytes, fmtDate } from '../ui';
/** Lines removed from a and added in b, in order; a plain longest-common-subsequence diff, enough for device configurations. */
export function diffLines(a, b) {
    const x = a.split('\n'), y = b.split('\n');
    if (x.length * y.length > 25_000_000) { // too big for the table: show what is new and what is gone
        const sx = new Set(x), sy = new Set(y);
        return [...x.filter((l) => !sy.has(l)).map((text) => ({ kind: '-', text })), ...y.filter((l) => !sx.has(l)).map((text) => ({ kind: '+', text }))];
    }
    const n = x.length, m = y.length;
    const L = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--)
        for (let j = m - 1; j >= 0; j--)
            L[i][j] = x[i] === y[j] ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
    const out = [];
    let i = 0, j = 0;
    while (i < n && j < m) {
        if (x[i] === y[j]) {
            out.push({ kind: ' ', text: x[i] });
            i++;
            j++;
        }
        else if (L[i + 1][j] >= L[i][j + 1])
            out.push({ kind: '-', text: x[i++] });
        else
            out.push({ kind: '+', text: y[j++] });
    }
    while (i < n)
        out.push({ kind: '-', text: x[i++] });
    while (j < m)
        out.push({ kind: '+', text: y[j++] });
    return out;
}
/** The configuration backups of a network device: the latest text, every kept version, what changed, and a download. */
export function Configs({ id }) {
    const { data, error, loading } = useLoad(`/api/assets/${id}/configs`);
    const items = (data?.items ?? []);
    const [sel, setSel] = useState(null);
    const [text, setText] = useState({});
    const [mode, setMode] = useState('text');
    const [err, setErr] = useState('');
    const cur = sel ?? items[0]?.id ?? null;
    const idx = items.findIndex((c) => c.id === cur);
    const prev = idx >= 0 ? items[idx + 1] : undefined;
    useEffect(() => {
        const need = [cur, mode === 'diff' ? prev?.id : null].filter((x) => typeof x === 'number' && !(x in text));
        if (!need.length)
            return;
        Promise.all(need.map((c) => api(`/api/assets/${id}/configs/${c}`))).then((rs) => setText((t) => ({ ...t, ...Object.fromEntries(rs.map((r) => [Number(r.id), String(r.text)])) }))).catch((e) => setErr(e.message));
    }, [cur, mode, prev?.id, id, text]);
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const c = items[idx];
    return _jsxs(_Fragment, { children: [_jsxs("p", { className: "muted", children: ["Backed up by the controller with the device's other details (every six hours, and on Read now). A new version is kept only when the configuration changed; the latest ", data?.keep, " are kept. Only administrators can see configurations, and each view is written to the audit log."] }), data?.checkedAt && _jsxs("p", { className: "muted", children: ["Last checked ", ago(String(data.checkedAt)), "."] }), data?.error && _jsxs("p", { className: "note", children: ["The last backup failed: ", String(data.error)] }), items.length === 0 ? _jsx(Empty, { children: "No configuration yet. Set this device to be read as \"Network device\" under Overview and save a network device login (SSH or API) under Credentials." }) : _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("select", { value: cur ?? '', onChange: (e) => setSel(Number(e.target.value)), "aria-label": "Version", children: items.map((v, i) => _jsxs("option", { value: v.id, children: [fmtDate(String(v.at)), i === 0 ? ' (latest)' : '', " \u00B7 ", v.lines, " lines"] }, v.id)) }), _jsx("button", { className: mode === 'text' ? 'primary' : '', onClick: () => setMode('text'), children: "Configuration" }), _jsx("button", { className: mode === 'diff' ? 'primary' : '', disabled: !prev, onClick: () => setMode('diff'), children: "Changes from the version before" }), c && _jsx(Act, { label: "Download", run: () => saveFile(`/api/assets/${id}/configs/${c.id}/download`, `config-${c.id}.txt`) })] }), c && _jsxs("p", { className: "muted", children: [c.vendor && _jsx(Badge, { children: String(c.vendor) }), " Read by ", c.source === 'api' ? 'the vendor API' : 'SSH', " \u00B7 ", fmtBytes(Number(c.size))] }), _jsx(Err, { text: err }), cur != null && mode === 'text' && (text[cur] == null ? _jsx(Loading, { loading: true, error: "" }) : _jsx(Card, { title: "Configuration", children: _jsx("pre", { className: "config", children: text[cur] }) })), cur != null && mode === 'diff' && prev && (text[cur] == null || text[prev.id] == null ? _jsx(Loading, { loading: true, error: "" }) : _jsx(Diff, { a: text[prev.id], b: text[cur] }))] })] });
}
function Diff({ a, b }) {
    const d = diffLines(a, b);
    const changed = d.filter((l) => l.kind !== ' ');
    const plus = changed.filter((l) => l.kind === '+').length, minus = changed.length - plus;
    // show changed lines with three lines of context
    const keep = new Set();
    d.forEach((l, i) => { if (l.kind !== ' ')
        for (let k = i - 3; k <= i + 3; k++)
            keep.add(k); });
    return _jsx(Card, { title: `Changes: ${plus} lines added, ${minus} removed`, children: changed.length === 0 ? _jsx("p", { className: "muted", children: "No changes." }) :
            _jsx("pre", { className: "config diff", children: d.map((l, i) => keep.has(i) ? _jsxs("div", { className: l.kind === '+' ? 'add' : l.kind === '-' ? 'del' : '', children: [l.kind, " ", l.text] }, i) : (keep.has(i - 1) ? _jsx("div", { className: "muted", children: "\u2026" }, i) : null)) }) });
}
