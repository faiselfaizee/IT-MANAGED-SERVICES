import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { saveFile, useLoad } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp } from '../Rings';
import { ReportList, TableReport } from '../ReportHub';
/** Monitoring › Reports: every monitoring report, and the report that is open. */
export default function Reports({ name }) {
    const catalog = useLoad('/api/monitoring/report-catalog');
    const dash = useLoad('/api/monitoring/dashboard');
    const cos = (dash.data?.customers ?? []).filter((c) => c.companyId != null);
    if (name === 'uptime')
        return _jsx(UptimeReport, {});
    if (name)
        return _jsx(TableReport, { url: '/api/monitoring/report/' + encodeURIComponent(name), back: "#/monitoring/reports", extra: { days: name === 'disks' || name === 'services' ? '' : '30' }, link: (id) => `#/monitoring/device/${id}`, filters: (set, f) => _jsxs(_Fragment, { children: [_jsxs("select", { value: f.company ?? '', onChange: (e) => set('company', e.target.value), "aria-label": "Customer", children: [!dash.data?.restricted && _jsx("option", { value: "", children: "All customers" }), cos.map((c) => _jsx("option", { value: c.companyId, children: c.company }, c.companyId))] }), name !== 'disks' && name !== 'services' && _jsxs("select", { value: f.days ?? '30', onChange: (e) => set('days', e.target.value), "aria-label": "Period", children: [_jsx("option", { value: "1", children: "Last 24 hours" }), _jsx("option", { value: "7", children: "Last 7 days" }), _jsx("option", { value: "30", children: "Last 30 days" }), _jsx("option", { value: "90", children: "Last 90 days" })] })] }) }, name);
    const entries = [
        { key: 'uptime', group: 'Availability', title: 'Monthly uptime report', about: 'Availability and downtime per device for a month; the same PDF that is mailed to customers on the 1st.', href: '#/monitoring/reports/uptime' },
        ...(Array.isArray(catalog.data) ? catalog.data : []).map((r) => ({ key: String(r.key), group: String(r.group), title: String(r.title), about: String(r.about), href: '#/monitoring/reports/' + r.key })),
    ];
    return _jsxs(_Fragment, { children: [_jsx(Loading, { loading: catalog.loading && !catalog.data, error: catalog.error }), _jsx(ReportList, { title: "Monitoring reports", intro: "Pick a report. Each one can be filtered by customer and period, searched, and exported to CSV (opens in Excel).", entries: entries })] });
}
const KIND = { offline: 'Device down', cpu: 'Processor', memory: 'Memory', disk: 'Disk space', check: 'Service checks', cert: 'Certificates', hardware: 'Hardware', traffic: 'Port traffic', port: 'Ports down', service: 'Services', process: 'Programs', log: 'Device messages' };
const pct = (v) => (v >= 99.995 ? '100%' : v.toFixed(v >= 99 ? 2 : 1) + '%');
const mins = (m) => (m < 1 ? 'none' : m < 60 ? `${Math.round(m)} min` : m < 1440 ? `${Math.floor(m / 60)} h ${Math.round(m % 60)} min` : `${Math.floor(m / 1440)} d ${Math.floor((m % 1440) / 60)} h`);
function months() {
    const out = [];
    const d = new Date();
    d.setUTCDate(1);
    for (let i = 0; i < 13; i++) {
        const k = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`;
        out.push([k, d.toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' }) + (i === 0 ? ' (so far)' : '')]);
        d.setUTCMonth(d.getUTCMonth() - 1);
    }
    return out;
}
/** The uptime report of a month, for one customer or all; the same as the PDF mailed on the 1st. */
function UptimeReport() {
    const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
    const ms = months();
    const [month, setMonth] = useState(ms[1][0]);
    const [company, setCompany] = useState(qs.get('company') ?? 'all');
    const dash = useLoad('/api/monitoring/dashboard');
    const { data, error, loading } = useLoad(`/api/monitoring/reports/uptime?company=${encodeURIComponent(company)}&month=${month}`);
    const cos = (dash.data?.customers ?? []).filter((c) => c.companyId != null);
    const lines = (data?.lines ?? []);
    const byKind = Object.entries((data?.byKind ?? {})).sort((a, b) => b[1] - a[1]);
    const [err, setErr] = useState('');
    return _jsxs(_Fragment, { children: [_jsx("p", { className: "crumb-back", children: _jsx("a", { href: "#/monitoring/reports", children: "All reports" }) }), _jsx("h1", { children: "Uptime report" }), _jsxs("div", { className: "tools", children: [_jsxs("select", { value: company, onChange: (e) => setCompany(e.target.value), "aria-label": "Customer", children: [!dash.data?.restricted && _jsx("option", { value: "all", children: "All customers" }), cos.map((c) => _jsx("option", { value: c.companyId, children: c.company }, c.companyId))] }), _jsx("select", { value: month, onChange: (e) => setMonth(e.target.value), "aria-label": "Month", children: ms.map(([k, l]) => _jsx("option", { value: k, children: l }, k)) }), _jsx("button", { onClick: async () => { try {
                            setErr('');
                            await saveFile(`/api/monitoring/reports/uptime.pdf?company=${encodeURIComponent(company)}&month=${month}`, `uptime-${month}.pdf`);
                        }
                        catch (e) {
                            setErr(e.message);
                        } }, children: "Download PDF" }), err && _jsx("span", { className: "err", role: "alert", children: err })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && _jsxs(_Fragment, { children: [_jsxs("div", { className: "mon-now", children: [_jsxs("div", { children: [_jsx("span", { className: "muted", children: "Average availability" }), _jsx("b", { children: pct(Number(data.availability)) })] }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "Devices watched" }), _jsx("b", { children: _jsx(CountUp, { value: Number(data.devices) }) })] }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "Outages" }), _jsx("b", { children: _jsx(CountUp, { value: Number(data.outages) }) }), _jsxs("span", { className: "muted small", children: [mins(Number(data.downMinutes)), " down in all"] })] }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "Critical alerts" }), _jsx("b", { children: _jsx(CountUp, { value: Number(data.critical) }) })] }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "Warning alerts" }), _jsx("b", { children: _jsx(CountUp, { value: Number(data.warning) }) })] }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "Tickets from alerts" }), _jsx("b", { children: _jsx(CountUp, { value: Number(data.tickets) }) })] })] }), _jsxs("div", { className: "grid two", children: [_jsxs("section", { children: [_jsx("h2", { children: "Availability by device" }), lines.length === 0 ? _jsx(Empty, { children: "No watched devices." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Device" }), _jsx("th", { children: "Down for" }), _jsx("th", { children: "Availability" }), _jsx("th", { children: "Alerts" })] }) }), _jsx("tbody", { children: lines.slice(0, 100).map((l) => _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("a", { href: `#/monitoring/device/${l.id}`, children: l.name }), _jsx("div", { className: "muted small for-customers", children: l.company })] }), _jsx("td", { children: mins(l.downMinutes) }), _jsx("td", { children: _jsx("span", { className: l.availability < 99 ? 'badge bad' : l.availability < 99.9 ? 'badge warn' : '', children: pct(l.availability) }) }), _jsx("td", { children: l.alerts })] }, l.id)) })] }) })] }), _jsxs("section", { children: [_jsx("h2", { children: "Alerts by kind" }), byKind.length === 0 ? _jsx(Empty, { children: "No alerts this month." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Kind" }), _jsx("th", { children: "Alerts" })] }) }), _jsx("tbody", { children: byKind.map(([k, v]) => _jsxs("tr", { children: [_jsx("td", { children: KIND[k] ?? k }), _jsx("td", { children: v })] }, k)) })] }), _jsx("p", { className: "muted small", children: "Availability is the share of the month a device had no open \"down\" alert. To mail this report on the 1st of every month, switch it on in Monitoring \u203A Settings." })] })] })] })] });
}
