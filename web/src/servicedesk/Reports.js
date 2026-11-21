import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { useSingleCompany } from '../mode';
import { saveFile, useLoad } from '../http';
import { Card, Empty, Err, Loading, fmtDate, useSubmit } from '../ui';
import { PRIORITY } from './List';
const thisMonth = () => new Date().toISOString().slice(0, 7);
const hours = (h) => (h == null ? 'No data' : h < 48 ? `${h} h` : `${Math.round(h / 24)} days`);
const pct = (p) => (p == null ? 'No data' : `${p}%`);
/** Monthly service report for one company (or all). The PDF is made by the server. */
export default function Reports({ staff }) {
    const one = useSingleCompany();
    const [month, setMonth] = useState(thisMonth());
    const [company, setCompany] = useState('');
    const companies = useLoad(staff ? '/api/assets/companies' : null);
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const r = useLoad(`/api/servicedesk/reports/monthly?month=${month}${company ? `&companyId=${company}` : ''}`);
    const pdf = useSubmit(() => undefined);
    const d = r.data;
    const t = d?.totals;
    return _jsxs("div", { className: "report", children: [_jsx("h1", { children: "Monthly report" }), _jsxs("div", { className: "tools no-print", children: [_jsxs("label", { children: ["Month ", _jsx("input", { type: "month", value: month, max: thisMonth(), onChange: (e) => e.target.value && setMonth(e.target.value) })] }), staff && _jsxs("label", { className: "for-customers", children: ["Company ", _jsxs("select", { value: company, onChange: (e) => setCompany(e.target.value), children: [_jsx("option", { value: "", children: "All companies" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] })] }), _jsx("button", { className: "primary", onClick: () => pdf.submit(() => saveFile(`/api/servicedesk/reports/monthly.pdf?month=${month}${company ? `&companyId=${company}` : ''}`, `service-report-${month}.pdf`)), disabled: !d || pdf.busy, children: "Download PDF" }), _jsx("button", { onClick: () => window.print(), disabled: !d, children: "Print" })] }), _jsx(Err, { text: pdf.err }), _jsx(Loading, { loading: r.loading && !d, error: r.error }), d && t && _jsxs(_Fragment, { children: [_jsxs("h2", { children: [d.company, " \u00B7 ", new Date(d.from).toLocaleString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' })] }), _jsx("div", { className: "tiles", children: [['Tickets opened', t.opened], ['Tickets solved', t.resolved], ['Open at start', t.backlogStart], ['Open at end', t.backlogEnd], ['Average first reply', hours(t.avgFirstResponseHours)], ['Average time to solve', hours(t.avgResolutionHours)], ['Replied in time', pct(t.respondMetPercent)], ['Solved in time', pct(t.resolveMetPercent)]]
                            .map(([k, v]) => _jsxs("div", { className: "tile", children: [_jsx("div", { className: "muted", children: k }), _jsx("b", { style: { fontSize: 22 }, children: String(v) })] }, String(k))) }), d.changes && _jsxs("p", { className: "muted", children: ["Changes finished this month: ", d.changes.completed, " completed, ", d.changes.failed, " failed."] }), _jsxs("div", { className: "grid2", children: [_jsx(Breakdown, { title: "By priority", rows: d.byPriority.map((x) => ({ ...x, name: PRIORITY[x.name] ?? x.name })) }), _jsx(Breakdown, { title: "By category", rows: d.byCategory }), _jsx(Breakdown, { title: "How tickets arrived", rows: d.bySource }), _jsx(Breakdown, { title: "Most active people", rows: d.byRequester }), _jsx(Breakdown, { title: "Devices with most tickets", rows: d.byAsset }), staff && !d.companyId && !one && _jsx(Breakdown, { title: "By company", rows: d.byCompany })] }), _jsxs(Card, { title: "Tickets opened this month", children: [d.tickets.length === 0 ? _jsx(Empty, { children: "No tickets were opened this month." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Ticket" }), _jsx("th", { children: "Subject" }), _jsx("th", { children: "Priority" }), _jsx("th", { children: "Status" }), _jsx("th", { children: "Opened" }), _jsx("th", { children: "Solved" })] }) }), _jsx("tbody", { children: d.tickets.map((x) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: x.number }), _jsxs("td", { children: [x.subject, _jsx("div", { className: "muted", children: x.requester })] }), _jsx("td", { children: PRIORITY[x.priority] ?? x.priority }), _jsx("td", { children: x.status }), _jsx("td", { children: fmtDate(x.createdAt) }), _jsx("td", { children: x.doneAt ? fmtDate(x.doneAt) : '' })] }, x.id)) })] }), d.truncated && _jsx("p", { className: "muted", children: "Only the first 300 tickets are listed." })] })] })] });
}
function Breakdown({ title, rows }) {
    return _jsx(Card, { title: title, children: rows.length === 0 ? _jsx("p", { className: "muted", children: "Nothing this month." }) : _jsx("table", { children: _jsx("tbody", { children: rows.map((x) => _jsxs("tr", { children: [_jsx("td", { children: x.name }), _jsx("td", { style: { textAlign: 'right' }, children: x.count })] }, x.name)) }) }) });
}
