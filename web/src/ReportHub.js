import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { saveFile, useLoad } from './http';
import { Act, Empty, Loading } from './ui';
/** A module's Reports page: every report it has, grouped, each with one line on what it shows. */
export function ReportList({ title, intro, entries }) {
    const groups = [...new Set(entries.map((e) => e.group))];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: title }), _jsx("p", { className: "muted", children: intro }), _jsx("div", { className: "report-list", children: groups.map((g) => _jsxs("section", { "aria-labelledby": 'rg-' + g, children: [_jsx("h2", { id: 'rg-' + g, children: g }), _jsx("div", { className: "report-cards", children: entries.filter((e) => e.group === g).map((e) => _jsxs("a", { href: e.href, className: "report-card", "aria-label": e.title, children: [_jsx("b", { children: e.title }), _jsx("span", { className: "muted", children: e.about })] }, e.key)) })] }, g)) })] });
}
/**
 * A report shown as a table from a server report endpoint: { title, about, columns, rows, total, shown }. The first value of each row is the record id
 * and is not shown; `link` turns it into an address for the first visible column. Filters go on the query string; the CSV button sends the same filters.
 */
export function TableReport({ url, back, filters, link, extra }) {
    const [f, setF] = useState({ q: '', ...(extra ?? {}) });
    const [typed, setTyped] = useState('');
    const qs = '?' + Object.entries(f).filter(([, v]) => v !== '').map(([k, v]) => k + '=' + encodeURIComponent(v)).join('&');
    const { data, error, loading } = useLoad(url + qs, [qs]);
    const set = (k, v) => setF((x) => ({ ...x, [k]: v }));
    const rows = (data?.rows ?? []);
    const cols = (data?.columns ?? []);
    const name = String(data?.key ?? 'report');
    return _jsxs(_Fragment, { children: [_jsx("p", { className: "crumb-back", children: _jsx("a", { href: back, children: "All reports" }) }), _jsx("h1", { children: data?.title ?? 'Report' }), data?.about ? _jsx("p", { className: "muted", children: String(data.about) }) : null, _jsxs("form", { className: "tools", onSubmit: (e) => { e.preventDefault(); set('q', typed.trim()); }, children: [_jsx("input", { type: "search", value: typed, onChange: (e) => setTyped(e.target.value), placeholder: "Find\u2026", "aria-label": "Find in this report", maxLength: 100 }), _jsx("button", { type: "submit", children: "Find" }), filters?.(set, f), _jsx(Act, { label: "Export (CSV)", run: () => saveFile(url + '.csv' + qs, name + '-report.csv') })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (rows.length === 0 ? _jsx(Empty, { children: "Nothing matches." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { className: "report-table", children: [_jsx("thead", { children: _jsx("tr", { children: cols.map((c) => _jsx("th", { scope: "col", children: c }, c)) }) }), _jsx("tbody", { children: rows.map((r, i) => { const href = link?.(r[0], data); return _jsx("tr", { children: r.slice(1).map((v, j) => _jsx("td", { children: j === 0 && href ? _jsx("a", { href: href, children: v || '(no name)' }) : v }, j)) }, r[0] + ':' + i); }) })] }) })), data && Number(data.total) > rows.length && _jsxs("p", { className: "muted", children: ["Showing ", rows.length, " of ", Number(data.total).toLocaleString(), " rows. The export has all of them."] }), data && Number(data.total) <= rows.length && rows.length > 0 && _jsxs("p", { className: "muted", children: [rows.length.toLocaleString(), " ", rows.length === 1 ? 'row' : 'rows', "."] })] });
}
