import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { saveFile, useLoad } from '../http';
import { Act, Badge, Empty, Loading } from '../ui';
import { Ring } from '../Rings';
import { ReportList, TableReport } from '../ReportHub';
/** Assets › Reports: every asset report, and the report that is open. */
export default function Reports({ name }) {
    const catalog = useLoad('/api/assets/report-catalog');
    const companies = useLoad('/api/assets/companies');
    const cos = Array.isArray(companies.data) ? companies.data : [];
    if (name === 'patches')
        return _jsx(PatchReport, {});
    if (name)
        return _jsx(TableReport, { url: '/api/assets/report/' + encodeURIComponent(name), back: "#/assets/reports", link: (id, d) => d.idKind === 'licence' ? `#/assets/licenses/${id}` : `#/assets/${id}`, filters: (set, f) => _jsxs(_Fragment, { children: [_jsxs("select", { value: f.company ?? '', onChange: (e) => set('company', e.target.value), "aria-label": "Customer", children: [_jsx("option", { value: "", children: "All customers" }), _jsx("option", { value: "none", children: "No customer" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }), name !== 'licences' && _jsxs("select", { value: f.status ?? '', onChange: (e) => set('status', e.target.value), "aria-label": "Status", children: [_jsx("option", { value: "", children: "In use, stock and repair" }), _jsx("option", { value: "in_use", children: "In use" }), _jsx("option", { value: "stock", children: "In stock" }), _jsx("option", { value: "repair", children: "In repair" }), _jsx("option", { value: "retired", children: "Retired" }), _jsx("option", { value: "lost", children: "Lost" })] })] }) }, name);
    const entries = [
        ...(Array.isArray(catalog.data) ? catalog.data : []).map((r) => ({ key: String(r.key), group: String(r.group), title: String(r.title), about: String(r.about), href: '#/assets/reports/' + r.key })),
        { key: 'patches', group: 'Security', title: 'Patch compliance', about: 'Computers with an agent, oldest patches first, with antivirus and compliance.', href: '#/assets/reports/patches' },
        { key: 'backup', group: 'Security', title: 'Backup status', about: 'Success, failed and missed backups per device (in the Backup module).', href: '#/backup/report' },
    ];
    const order = ['Inventory', 'Software', 'Security', 'Contracts', 'Agents'];
    entries.sort((a, b) => order.indexOf(a.group) - order.indexOf(b.group));
    return _jsxs(_Fragment, { children: [_jsx(Loading, { loading: catalog.loading && !catalog.data, error: catalog.error }), _jsx(ReportList, { title: "Asset reports", intro: "Pick a report. Each one can be filtered by customer and status, searched, and exported to CSV (opens in Excel). A PDF of a single asset is on the asset's own page.", entries: entries })] });
}
/** The patch and compliance report: every computer with an agent, oldest patches first, for one customer or all. */
function PatchReport() {
    const [co, setCo] = useState('');
    const [show, setShow] = useState('all');
    const companies = useLoad('/api/assets/companies');
    const qs = `?company=${encodeURIComponent(co)}&show=${show}`;
    const { data, error, loading } = useLoad('/api/assets/reports/patches' + qs);
    const items = (data?.items ?? []);
    const list = Array.isArray(companies.data) ? companies.data : [];
    const total = Number(data?.total ?? 0);
    return _jsxs(_Fragment, { children: [_jsx("p", { className: "crumb-back", children: _jsx("a", { href: "#/assets/reports", children: "All reports" }) }), _jsx("h1", { children: "Patch and compliance report" }), _jsx("p", { className: "muted", children: "Computers that report through an agent. Compliant means antivirus on and patches installed in the last 30 days." }), _jsxs("div", { className: "tools", children: [_jsxs("select", { value: co, onChange: (e) => setCo(e.target.value), "aria-label": "Customer", children: [_jsx("option", { value: "", children: "All customers" }), _jsx("option", { value: "none", children: "No customer" }), list.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }), _jsxs("select", { value: show, onChange: (e) => setShow(e.target.value), "aria-label": "Show", children: [_jsx("option", { value: "all", children: "All computers" }), _jsx("option", { value: "behind", children: "Patches older than 30 days" }), _jsx("option", { value: "noncompliant", children: "Not compliant" })] }), _jsx(Act, { label: "Export (CSV)", run: () => saveFile('/api/assets/reports/patches.csv' + qs, 'patch-report.csv') })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && show === 'all' && total > 0 && _jsxs("div", { className: "rings", style: { marginBottom: 16 }, children: [_jsx(Ring, { title: "Patches", unit: "computers", parts: [{ key: 'ok', label: 'Last 30 days', value: total - Number(data.behind), tone: 'ok' }, { key: 'old', label: 'Older or unknown', value: Number(data.behind), tone: 'bad' }] }), _jsx(Ring, { title: "Compliance", unit: "computers", delay: 120, parts: [{ key: 'c', label: 'Compliant', value: Number(data.compliant), tone: 'ok' }, { key: 'n', label: 'Not compliant', value: Number(data.nonCompliant), tone: 'bad' },
                            { key: 'u', label: 'No data yet', value: total - Number(data.compliant) - Number(data.nonCompliant), tone: 'none' }] })] }), data && (items.length === 0 ? _jsx(Empty, { children: "No computers here." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Computer" }), _jsx("th", { children: "Customer" }), _jsx("th", { children: "Operating system" }), _jsx("th", { children: "Last patch" }), _jsx("th", { className: "num", children: "Days" }), _jsx("th", { children: "Antivirus" }), _jsx("th", { children: "Compliance" })] }) }), _jsx("tbody", { children: items.map((x) => _jsxs("tr", { children: [_jsxs("td", { children: [_jsx("a", { href: `#/assets/${x.id}`, children: x.name }), _jsx("div", { className: "muted mono", children: x.tag })] }), _jsx("td", { children: x.company }), _jsx("td", { children: x.os }), _jsx("td", { children: x.lastPatch || _jsx("span", { className: "muted", children: "unknown" }) }), _jsx("td", { className: "num", children: x.days ?? '' }), _jsx("td", { children: x.antivirus }), _jsx("td", { children: _jsx(Badge, { tone: x.compliance === 'Compliant' ? 'ok' : x.compliance === 'Not compliant' ? 'bad' : undefined, children: x.compliance }) })] }, x.id)) })] })), total > items.length && _jsxs("p", { className: "muted", children: ["Showing ", items.length, " of ", total, ". The export has all of them."] })] });
}
