import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';
import * as S from './session';
const STATE = { ok: ['Up to date', 'ok'], rotating: ['Changing now', ''], failed: ['Change failed', 'bad'], unknown: ['Unsure', 'warn'] };
/** Every account DeskAsk changes the password of, with when it last and next happens. */
export default function Managed() {
    const list = useLoad('/api/vault/managed');
    const items = (list.data?.items ?? []);
    useInterval(list.reload, items.some((i) => i.state === 'rotating') ? 4000 : 60_000);
    const titles = S.useTitles(items.map((i) => ({ id: Number(i.itemId), vaultId: Number(i.vaultId), titleEnc: String(i.titleEnc) })));
    const [err, setErr] = useState('');
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Managed accounts" }), _jsx("p", { className: "muted", children: "Passwords DeskAsk changes through the agent on each computer: Windows local accounts, Active Directory accounts (with the agent on a domain controller) and Linux accounts. To add one, open a server account linked to its asset and choose \"Let DeskAsk change this password\"." }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && (items.length === 0 ? _jsx(Empty, { children: "No managed accounts yet." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Account" }), _jsx("th", { children: "Computer" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Last changed" }), _jsx("th", { children: "Next" }), _jsx("th", {})] }) }), _jsx("tbody", { children: items.map((m) => {
                                const [w, tone] = STATE[m.state] ?? [m.state, ''];
                                const v = S.vaultById(Number(m.vaultId));
                                return _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("a", { href: `#/vault?item=${m.itemId}`, children: titles[Number(m.itemId)] ?? '…' }), _jsxs("div", { className: "muted small", children: [m.account, " \u00B7 ", m.target === 'domain' ? 'Active Directory' : m.target === 'linux' ? 'Linux' : 'Windows local', " \u00B7 ", v?.name] })] }), _jsx("td", { children: _jsx("a", { href: `#/assets/${m.assetId}`, children: m.asset || 'asset ' + m.assetId }) }), _jsxs("td", { children: [_jsx("span", { className: 'badge ' + tone, children: w }), m.lastError ? _jsx("div", { className: "muted small", children: m.lastError }) : null] }), _jsx("td", { children: m.lastRotatedAt ? _jsx("span", { title: fmtDate(m.lastRotatedAt), children: ago(m.lastRotatedAt) }) : _jsx("span", { className: "muted", children: "not yet" }) }), _jsxs("td", { children: [m.nextAt ? fmtDate(m.nextAt) : _jsx("span", { className: "muted", children: "when asked" }), m.resetAfterUse ? _jsx("div", { className: "muted small", children: "and after each use" }) : null] }), _jsx("td", { children: v && S.rank(v.role) >= 3 && _jsx("button", { className: "small", disabled: m.state === 'rotating', onClick: async () => { setErr(''); try {
                                                    await api(`/api/vault/items/${m.itemId}/rotate`, { method: 'POST', body: {} });
                                                    list.reload();
                                                }
                                                catch (e) {
                                                    setErr(e.message);
                                                } }, children: "Change now" }) })] }, m.itemId);
                            }) })] }) }))] });
}
