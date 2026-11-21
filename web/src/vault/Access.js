import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { useLoad } from '../http';
import { Empty, Loading } from '../ui';
import * as S from './session';
const ACTIONS = { viewed: 'Opened', revealed: 'Showed a secret', copied: 'Copied', opened: 'Opened the address', exported: 'Exported' };
/** Vault › Reports › Access report: who used which item, in the vaults this person edits or manages. Titles are decrypted in the browser. */
export default function Access() {
    const [days, setDays] = useState('30');
    const [action, setAction] = useState('');
    const [user, setUser] = useState('');
    const [typed, setTyped] = useState('');
    const qs = `?days=${days}&action=${action}&user=${encodeURIComponent(user)}`;
    const { data, error, loading } = useLoad('/api/vault/reports/access' + qs, [qs]);
    const items = (data?.items ?? []);
    const titles = S.useTitles(items.filter((x) => x.titleEnc).map((x) => ({ id: Number(x.itemId), vaultId: Number(x.vaultId), titleEnc: String(x.titleEnc) })));
    const name = (x) => titles[Number(x.itemId)] ?? (x.titleEnc ? '…' : '(deleted item)');
    const csv = () => {
        const cell = (v) => /^[=+\-@\t\r]/.test(v) ? `'${v}` : /[",\r\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
        const lines = [['When', 'Who', 'What', 'Field', 'Item', 'Vault'].join(',')].concat(items.map((x) => [new Date(String(x.at)).toLocaleString(), String(x.user), ACTIONS[String(x.action)] ?? String(x.action), String(x.field), name(x), String(x.vault)].map(cell).join(',')));
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv' }));
        a.download = 'vault-access-report.csv';
        a.click();
        URL.revokeObjectURL(a.href);
    };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Access report" }), _jsx("p", { className: "muted", children: "Who opened, showed, copied or exported which item, in the vaults you edit or manage. Item names are decrypted in your browser and never sent to the server." }), _jsxs("form", { className: "tools", onSubmit: (e) => { e.preventDefault(); setUser(typed.trim()); }, children: [_jsxs("select", { value: days, onChange: (e) => setDays(e.target.value), "aria-label": "Period", children: [_jsx("option", { value: "1", children: "Last 24 hours" }), _jsx("option", { value: "7", children: "Last 7 days" }), _jsx("option", { value: "30", children: "Last 30 days" }), _jsx("option", { value: "90", children: "Last 90 days" }), _jsx("option", { value: "366", children: "Last year" })] }), _jsxs("select", { value: action, onChange: (e) => setAction(e.target.value), "aria-label": "What was done", children: [_jsx("option", { value: "", children: "Everything" }), Object.entries(ACTIONS).map(([k, l]) => _jsx("option", { value: k, children: l }, k))] }), _jsx("input", { type: "search", value: typed, onChange: (e) => setTyped(e.target.value), placeholder: "Person", "aria-label": "Person" }), _jsx("button", { type: "submit", children: "Find" }), _jsx("button", { type: "button", onClick: csv, disabled: items.length === 0, children: "Export (CSV)" })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (Number(data.vaults) === 0 ? _jsx(Empty, { children: "You do not edit or manage any vault, so there is nothing to show." })
                : items.length === 0 ? _jsx(Empty, { children: "No one used an item in this period." })
                    : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "When" }), _jsx("th", { children: "Who" }), _jsx("th", { children: "What" }), _jsx("th", { children: "Item" }), _jsx("th", { children: "Vault" })] }) }), _jsx("tbody", { children: items.map((x) => _jsxs("tr", { children: [_jsx("td", { children: new Date(String(x.at)).toLocaleString() }), _jsx("td", { children: x.user }), _jsxs("td", { children: [ACTIONS[String(x.action)] ?? x.action, x.field ? _jsxs("span", { className: "muted", children: [" (", x.field, ")"] }) : null] }), _jsx("td", { children: x.deleted ? _jsxs("span", { className: "muted", children: [name(x), " (deleted)"] }) : _jsx("a", { href: `#/vault?item=${x.itemId}`, children: name(x) }) }), _jsx("td", { children: x.vault })] }, x.id)) })] })), items.length >= 2000 && _jsx("p", { className: "muted", children: "Showing the latest 2,000. Narrow the period or the person to see older ones." })] });
}
