import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { usePerms } from '../perms';
import { Act, ago, Empty, fmtDate, Loading } from '../ui';
import { METRIC_WORDS } from './Dashboard';
const dur = (a, b) => {
    const m = Math.max(0, Math.round(((b ? new Date(b).getTime() : Date.now()) - new Date(a).getTime()) / 60000));
    return m < 60 ? `${m} min` : m < 1440 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${Math.floor(m / 1440)} d ${Math.floor((m % 1440) / 60)} h`;
};
/** Alert rows: what is wrong, since when, who is on it, and the ticket. Open alerts can be acknowledged or get a ticket. */
export function AlertRows({ items, device, onChange, canAct }) {
    const [msg, setMsg] = useState('');
    return _jsxs(_Fragment, { children: [msg && _jsx("p", { className: "err", role: "alert", children: msg }), _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Severity" }), !device && _jsx("th", { children: "Device" }), _jsx("th", { children: "What is wrong" }), _jsx("th", { children: "Since" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Ticket" })] }) }), _jsx("tbody", { children: items.map((a) => _jsxs("tr", { className: a.closedAt ? 'closed' : '', children: [_jsx("td", { children: _jsx("span", { className: `badge ${a.closedAt ? '' : a.severity === 'critical' ? 'bad' : 'warn'}`, children: a.severity === 'critical' ? 'Critical' : 'Warning' }) }), !device && _jsxs("td", { children: [_jsx("a", { href: `#/monitoring/device/${a.assetId}`, children: a.asset }), _jsx("div", { className: "muted small for-customers", children: a.company })] }), _jsxs("td", { children: [a.text, _jsxs("div", { className: "muted small", children: [a.rule, " \u00B7 ", METRIC_WORDS[a.metric] ?? a.metric, a.escalatedAt ? ' · escalated' : ''] }), a.actionNote && _jsx("div", { className: "muted small", children: a.actionNote })] }), _jsxs("td", { title: fmtDate(a.openedAt), children: [ago(a.openedAt), _jsx("div", { className: "muted small", children: a.closedAt ? 'lasted ' + dur(a.openedAt, a.closedAt) : 'for ' + dur(a.openedAt) })] }), _jsx("td", { children: a.closedAt ? _jsxs("span", { className: "muted", children: [a.closeReason, " ", ago(a.closedAt)] })
                                            : a.ackAt ? _jsxs("span", { children: ["Acknowledged by ", a.ackBy] })
                                                : canAct ? _jsx(Act, { label: "Acknowledge", run: async () => { try {
                                                        await api(`/api/monitoring/alerts/${a.id}/ack`, { method: 'POST', body: {} });
                                                        setMsg('');
                                                    }
                                                    catch (e) {
                                                        setMsg(e.message);
                                                    } onChange(); } }) : _jsx("span", { className: "muted", children: "Open" }) }), _jsx("td", { children: a.ticket ? _jsx("a", { href: `#/servicedesk/${a.ticketId}`, children: a.ticket })
                                            : !a.closedAt && canAct ? _jsx(Act, { label: "Open ticket", run: async () => { try {
                                                    await api(`/api/monitoring/alerts/${a.id}/ticket`, { method: 'POST', body: {} });
                                                    setMsg('');
                                                }
                                                catch (e) {
                                                    setMsg(e.message);
                                                } onChange(); } }) : _jsx("span", { className: "muted", children: "\u2013" }) })] }, a.id)) })] }) })] });
}
export default function Alerts() {
    const perms = usePerms('monitoring');
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const company = qs.get('company') ?? '';
    const [state, setState] = useState(qs.get('state') ?? 'open');
    const [severity, setSeverity] = useState(qs.get('severity') ?? '');
    const [metric, setMetric] = useState(qs.get('metric') ?? '');
    const [search, setSearch] = useState('');
    const [skip, setSkip] = useState(0);
    const take = 50;
    const co = useLoad(company ? `/api/monitoring/dashboard?company=${encodeURIComponent(company)}` : null);
    const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
    const q = `/api/monitoring/alerts?state=${state}&severity=${severity}&metric=${metric}&search=${encodeURIComponent(search)}` + (company ? `&company=${encodeURIComponent(company)}` : '') + `&skip=${skip}&take=${take}`;
    const { data, error, loading, reload } = useLoad(q);
    useInterval(reload, 60_000);
    const items = (data?.items ?? []);
    const total = Number(data?.total ?? 0);
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/monitoring", children: "Monitoring" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), company ? _jsxs(_Fragment, { children: [_jsx("a", { href: `#/monitoring/customer/${company}`, children: coName }), _jsx("span", { "aria-hidden": "true", children: "\u203A" })] }) : null, _jsx("span", { "aria-current": "page", children: "Alerts" })] }), _jsx("h1", { children: company ? `Alerts for ${coName}` : 'Alerts' }), company && _jsx("div", { className: "filter-chips", children: _jsxs("a", { href: "#/monitoring/alerts", title: "Show every customer", children: [coName, " \u2715"] }) }), _jsxs("div", { className: "tools", children: [_jsxs("select", { value: state, onChange: (e) => { setState(e.target.value); setSkip(0); }, "aria-label": "Which alerts", children: [_jsx("option", { value: "open", children: "Open" }), _jsx("option", { value: "closed", children: "Cleared" }), _jsx("option", { value: "all", children: "All" })] }), _jsxs("select", { value: severity, onChange: (e) => { setSeverity(e.target.value); setSkip(0); }, "aria-label": "Severity", children: [_jsx("option", { value: "", children: "Any severity" }), _jsx("option", { value: "critical", children: "Critical" }), _jsx("option", { value: "warning", children: "Warning" })] }), _jsxs("select", { value: metric, onChange: (e) => { setMetric(e.target.value); setSkip(0); }, "aria-label": "Kind", children: [_jsx("option", { value: "", children: "Any kind" }), Object.entries(METRIC_WORDS).map(([k, l]) => _jsx("option", { value: k, children: l }, k))] }), _jsx("input", { placeholder: "Search device, customer or text", value: search, onChange: (e) => { setSearch(e.target.value); setSkip(0); }, "aria-label": "Search alerts" })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: state === 'open' ? 'No open alerts. All clear.' : 'No alerts match.' }) : _jsx(AlertRows, { items: items, onChange: reload, canAct: perms.modify || perms.admin })), total > take && _jsxs("div", { className: "tools", children: [_jsx("button", { disabled: skip === 0, onClick: () => setSkip(Math.max(0, skip - take)), children: "Previous" }), _jsxs("span", { className: "muted", children: [skip + 1, "\u2013", Math.min(skip + take, total), " of ", total] }), _jsx("button", { disabled: skip + take >= total, onClick: () => setSkip(skip + take), children: "Next" })] })] });
}
