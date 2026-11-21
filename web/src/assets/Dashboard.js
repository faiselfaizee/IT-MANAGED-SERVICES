import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useSingleCompany } from '../mode';
import { useMemo, useState } from 'react';
import { useLoad } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp, MiniRing, Ring } from '../Rings';
const n = (v) => Number(v ?? 0);
/** The customer part of an asset list address: a customer id, "none" for assets without one, nothing for all. */
const coKey = (m) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
export const listHref = (co, filter = '') => '#/assets/list' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
const attention = (m) => n(m.agentOffline) + n(m.nonCompliant) + n(m.warrantyExpired) + 3 * n(m.hardwareNotOk);
function agentParts(m, co) {
    return [
        { key: 'on', label: 'Live', value: n(m.agentOnline), tone: 'ok', href: listHref(co, 'agent=online') },
        { key: 'due', label: 'Live, update due', value: n(m.agentUpdate), tone: 'warn', href: listHref(co, 'agent=update') },
        { key: 'off', label: 'Not reporting', value: n(m.agentOffline), tone: 'bad', href: listHref(co, 'agent=offline') },
        { key: 'none', label: 'No agent', value: n(m.agentNone), tone: 'none', href: listHref(co, 'agent=none') },
    ];
}
/** Assets home: one tile per customer, worst first, each with a small ring of its agents and its main problems. */
export default function Dashboard() {
    const one = useSingleCompany();
    const { data, error, loading } = useLoad('/api/assets/dashboard');
    const [q, setQ] = useState('');
    const [sort, setSort] = useState('attention');
    const [shown, setShown] = useState(60);
    const customers = (data?.customers ?? []);
    const list = useMemo(() => customers
        .filter((c) => !q || String(c.company).toLowerCase().includes(q.toLowerCase()))
        .sort((a, b) => sort === 'name' ? String(a.company).localeCompare(String(b.company)) : sort === 'assets' ? n(b.total) - n(a.total) : attention(b) - attention(a) || String(a.company).localeCompare(String(b.company))), [customers, q, sort]);
    if (one)
        return _jsx(CustomerDash, { id: "all" }); // one company: its own dashboard is the home page
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    // someone limited to one customer goes straight to that customer's dashboard
    if (data?.restricted && customers.length === 1)
        return _jsx(CustomerDash, { id: coKey(customers[0]) });
    return _jsxs(_Fragment, { children: [_jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: "Assets" }), _jsxs("p", { className: "muted", children: [customers.length, " customers \u00B7 ", n(data?.all?.total), " assets"] })] }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Find a customer", value: q, onChange: (e) => { setQ(e.target.value); setShown(60); }, "aria-label": "Find a customer" }), _jsxs("select", { value: sort, onChange: (e) => setSort(e.target.value), "aria-label": "Sort customers", children: [_jsx("option", { value: "attention", children: "Needs attention first" }), _jsx("option", { value: "name", children: "By name" }), _jsx("option", { value: "assets", children: "Most assets first" })] }), _jsx("a", { className: "button", href: "#/assets/list", children: "All assets" })] }), customers.length === 0 ? _jsx(Empty, { children: "No assets yet. Add assets, or install the agent or a site controller to find them." }) :
                _jsxs("div", { className: "co-tiles", children: [!q && data?.all && _jsx(Tile, { m: data.all, index: 0 }), list.slice(0, shown).map((c, i) => _jsx(Tile, { m: c, index: i + 1 }, coKey(c)))] }), list.length > shown && _jsx("div", { className: "tools", style: { justifyContent: 'center' }, children: _jsxs("button", { onClick: () => setShown(shown + 60), children: ["Show ", Math.min(60, list.length - shown), " more customers"] }) })] });
}
function Tile({ m, index }) {
    const co = coKey(m);
    const delay = Math.min(index, 12) * 70;
    const problems = [
        ['Not compliant', n(m.nonCompliant), 'compliance=noncompliant'],
        ['Warranty expired or ending', n(m.warrantyExpired) + n(m.warrantySoon), 'warranty=expired'],
        ['Hardware not OK', n(m.hardwareNotOk), 'health=bad'],
    ];
    return _jsxs("a", { className: `co-tile${co === 'all' ? ' all' : ''}${attention(m) > 0 ? ' attn' : ''}`, href: `#/assets/customer/${co}`, children: [_jsxs("div", { className: "co-tile-head", children: [_jsx("b", { className: "co-name", children: m.company }), _jsxs("span", { className: "muted", children: [_jsx(CountUp, { value: n(m.total), delay: delay }), " assets"] })] }), _jsxs("div", { className: "co-tile-body", children: [_jsx(MiniRing, { parts: agentParts(m, co), label: `Agents of ${m.company}`, delay: delay }), _jsxs("ul", { className: "co-figs", children: [_jsxs("li", { children: [_jsx("i", { className: "dot tone-ok", "aria-hidden": "true" }), _jsx("span", { children: "Live" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.agentOnline) + n(m.agentUpdate), delay: delay }) })] }), _jsxs("li", { children: [_jsx("i", { className: "dot tone-bad", "aria-hidden": "true" }), _jsx("span", { children: "Not reporting" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.agentOffline), delay: delay }) })] }), problems.map(([l, v]) => _jsxs("li", { className: v > 0 ? 'bad' : '', children: [_jsx("span", { children: l }), _jsx("b", { children: _jsx(CountUp, { value: v, delay: delay }) })] }, l))] })] })] });
}
/** One customer's detailed dashboard; every ring part opens the asset list filtered to it. */
export function CustomerDash({ id }) {
    const url = id === 'all' ? '/api/assets/dashboard' : `/api/assets/dashboard?company=${encodeURIComponent(id)}`;
    const { data, error, loading } = useLoad(url);
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const m = (id === 'all' ? data?.all : data?.customer);
    if (!m)
        return _jsx(Empty, { children: "This customer has no assets." });
    const co = id;
    const ok = Math.max(n(m.inService) - n(m.hardwareNotOk), 0);
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/assets", children: "Assets" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), _jsx("span", { "aria-current": "page", children: m.company })] }), _jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: m.company }), _jsxs("p", { className: "muted", children: [n(m.total), " assets, ", n(m.inService), " in service"] }), _jsx("a", { className: "button primary", href: listHref(co), children: "View all assets" })] }), _jsxs("div", { className: "rings dash-rings", children: [_jsx(Ring, { title: "Agents", unit: "devices", delay: 0, empty: "No devices in service.", parts: agentParts(m, co) }), _jsx(Ring, { title: "Compliance", unit: "with agent", delay: 120, empty: "No device reports through an agent yet.", parts: [
                            { key: 'c', label: 'Compliant', value: n(m.compliant), tone: 'ok', href: listHref(co, 'compliance=compliant') },
                            { key: 'nc', label: 'Not compliant', value: n(m.nonCompliant), tone: 'bad', href: listHref(co, 'compliance=noncompliant') },
                            { key: 'u', label: 'No data yet', value: n(m.noData), tone: 'none', href: listHref(co, 'compliance=nodata') }
                        ] }), _jsx(Ring, { title: "Warranty", unit: "devices", delay: 240, empty: "No devices in service.", parts: [
                            { key: 'ok', label: 'Covered', value: n(m.warrantyCovered), tone: 'ok', href: listHref(co, 'warranty=covered') },
                            { key: 'soon', label: 'Ends within 30 days', value: n(m.warrantySoon), tone: 'warn', href: listHref(co, 'warranty=soon') },
                            { key: 'x', label: 'Expired', value: n(m.warrantyExpired), tone: 'bad', href: listHref(co, 'warranty=expired') },
                            { key: 'u', label: 'Not recorded', value: n(m.warrantyUnknown), tone: 'none', href: listHref(co, 'warranty=unknown') }
                        ] }), _jsx(Ring, { title: "Asset types", unit: "in service", delay: 360, empty: "No devices in service.", parts: [
                            { key: 'pc', label: 'Computers', value: n(m.computers), tone: 'info', href: listHref(co, 'kind=computer') },
                            { key: 'srv', label: 'Servers and hypervisors', value: n(m.servers), tone: 'ok', href: listHref(co, 'kind=server') },
                            { key: 'net', label: 'Network devices', value: n(m.network), tone: 'warn', href: listHref(co, 'kind=network') },
                            { key: 'prn', label: 'Printers', value: n(m.printers), tone: 'bad', href: listHref(co, 'kind=printer') },
                            { key: 'o', label: 'Other', value: n(m.other), tone: 'none', href: listHref(co, 'kind=other') }
                        ] }), _jsx(Ring, { title: "Hardware health", unit: "in service", delay: 480, empty: "No devices in service.", parts: [
                            { key: 'bad', label: 'A part reports a fault', value: n(m.hardwareNotOk), tone: 'bad', href: listHref(co, 'health=bad') },
                            { key: 'ok', label: 'No problem reported', value: ok, tone: 'ok' }
                        ] }), _jsx(Ring, { title: "Network ports", unit: "ports", delay: 600, empty: "No switch or firewall is read yet.", parts: [
                            { key: 'u', label: 'In use', value: n(m.portsUsed), tone: 'ok', href: listHref(co, 'kind=network') },
                            { key: 'f', label: 'Free', value: n(m.portsFree), tone: 'info', href: listHref(co, 'kind=network') },
                            { key: 'o', label: 'Switched off', value: n(m.portsOff), tone: 'none', href: listHref(co, 'kind=network') }
                        ] })] }), _jsx("p", { className: "muted tile-foot", children: "Counts are for assets in service (not retired or lost). Compliant means antivirus on and patches from the last 30 days. Hardware health comes from server management controllers, hypervisors and network devices read by a site controller." })] });
}
