import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useSingleCompany } from '../mode';
import { useMemo, useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp, MiniRing, Ring } from '../Rings';
const n = (v) => Number(v ?? 0);
const coKey = (m) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
/** The device list, limited to a customer (unless all) and one ring part. */
export const devHref = (co, filter = '') => '#/monitoring/devices' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
export const alertHref = (co, filter = '') => '#/monitoring/alerts' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
const attention = (m) => 3 * n(m.critical) + n(m.warning);
export const METRIC_WORDS = { offline: 'Device down', cpu: 'Processor', memory: 'Memory', disk: 'Disk space', check: 'Service checks', cert: 'Certificates', hardware: 'Hardware', traffic: 'Port traffic', port: 'Port down', service: 'Services', autoservice: 'Automatic services', process: 'Programs', log: 'Device messages', supply: 'Printer supplies', printer: 'Printer faults' };
export function statusParts(m, co) {
    return [
        { key: 'ok', label: 'OK', value: n(m.ok), tone: 'ok', href: devHref(co, 'status=ok') },
        { key: 'warning', label: 'Warning', value: n(m.warning), tone: 'warn', href: devHref(co, 'status=warning') },
        { key: 'critical', label: 'Critical', value: n(m.critical), tone: 'bad', href: devHref(co, 'status=critical') },
        { key: 'muted', label: 'Muted', value: n(m.muted), tone: 'info', href: devHref(co, 'status=muted') },
        { key: 'unwatched', label: 'Not watched', value: n(m.unwatched), tone: 'none', href: devHref(co, 'status=unwatched') },
    ];
}
/** Monitoring home: one tile per customer, worst first, each with a small ring of its device states and its open alerts. */
export default function Dashboard() {
    const one = useSingleCompany();
    const { data, error, loading, reload } = useLoad('/api/monitoring/dashboard');
    useInterval(reload, 60_000);
    const me = useLoad('/api/platform/me');
    const [q, setQ] = useState('');
    const [sort, setSort] = useState('attention');
    const [shown, setShown] = useState(60);
    const [checking, setChecking] = useState('');
    const customers = (data?.customers ?? []);
    const list = useMemo(() => customers
        .filter((c) => !q || String(c.company).toLowerCase().includes(q.toLowerCase()))
        .sort((a, b) => sort === 'name' ? String(a.company).localeCompare(String(b.company)) : sort === 'devices' ? n(b.devices) - n(a.devices) : attention(b) - attention(a) || String(a.company).localeCompare(String(b.company))), [customers, q, sort]);
    if (one)
        return _jsx(CustomerDash, { id: "all" }); // one company: its own dashboard is the home page
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    if (data?.restricted && customers.length === 1)
        return _jsx(CustomerDash, { id: coKey(customers[0]) });
    const all = (data?.all ?? {});
    const checkNow = async () => {
        setChecking('Checking…');
        try {
            const r = await api('/api/monitoring/evaluate', { method: 'POST', body: {} });
            setChecking(`Checked: ${r.opened} new, ${r.closed} cleared, ${r.open} open.`);
            reload();
        }
        catch (e) {
            setChecking(e.message);
        }
    };
    return _jsxs(_Fragment, { children: [_jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: "Monitoring" }), _jsxs("p", { className: "muted", children: [customers.length, " customers \u00B7 ", n(all.watched), " of ", n(all.devices), " devices watched \u00B7 ", n(all.alertsCritical), " critical and ", n(all.alertsWarning), " warning alerts open"] })] }), data?.licence && n(data.licence.over) > 0 && _jsxs("p", { className: "note", role: "status", children: ["The licence allows ", n(data.licence.allowed), " monitored devices and ", n(data.licence.monitored), " are switched on: the ", n(data.licence.over), " added last are not watched. Switch monitoring off for devices that do not need it (Assets \u203A All assets), or ask your supplier for a licence with more."] }), _jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "Find a customer", value: q, onChange: (e) => { setQ(e.target.value); setShown(60); }, "aria-label": "Find a customer" }), _jsxs("select", { value: sort, onChange: (e) => setSort(e.target.value), "aria-label": "Sort customers", children: [_jsx("option", { value: "attention", children: "Needs attention first" }), _jsx("option", { value: "name", children: "By name" }), _jsx("option", { value: "devices", children: "Most devices first" })] }), _jsx("a", { className: "button", href: "#/monitoring/alerts", children: "Open alerts" }), me.data?.role === 'admin' && _jsx("button", { onClick: () => void checkNow(), children: "Check now" }), checking && _jsx("span", { className: "muted", role: "status", children: checking })] }), customers.length === 0 ? _jsxs("div", { className: "note", role: "status", children: [_jsxs("p", { children: [_jsx("b", { children: "No devices yet." }), " Monitoring watches the devices you add under Administration \u203A Devices setup. Start there:"] }), _jsxs("ol", { children: [_jsxs("li", { children: [_jsx("a", { href: "#/setup/companies", children: "Add the customer" }), " (Administration \u203A Devices setup \u203A Customers)."] }), _jsxs("li", { children: [_jsx("a", { href: "#/setup/credentials", children: "Save the logins" }), " for that customer: Windows, Linux SSH, SNMP, VMware vCenter or ESXi, server management (Devices setup \u203A Credentials)."] }), _jsxs("li", { children: ["Install the agent on computers and servers, or a site controller on the customer's network for printers, switches, firewalls and hypervisors (", _jsx("a", { href: "#/setup/fleet", children: "Devices setup \u203A Agents & collectors" }), ")."] }), _jsxs("li", { children: [_jsx("a", { href: "#/monitoring/settings", children: "Choose who gets the alert mails" }), " (Monitoring \u203A Settings); mail must be set up under Mail."] })] })] }) :
                _jsxs("div", { className: "co-tiles", children: [!q && _jsx(Tile, { m: all, index: 0 }), list.slice(0, shown).map((c, i) => _jsx(Tile, { m: c, index: i + 1 }, coKey(c)))] }), list.length > shown && _jsx("div", { className: "tools", style: { justifyContent: 'center' }, children: _jsxs("button", { onClick: () => setShown(shown + 60), children: ["Show ", Math.min(60, list.length - shown), " more customers"] }) })] });
}
function Tile({ m, index }) {
    const co = coKey(m);
    const delay = Math.min(index, 12) * 70;
    const figs = [['Down', n(m.down)], ['Watched ports down', n(m.portsDown)], ['Disks nearly full', n(m.diskCritical)], ['Checks failing', n(m.checksFailing)], ['Services stopped', n(m.servicesDown)]];
    return _jsxs("a", { className: `co-tile${co === 'all' ? ' all' : ''}${n(m.critical) > 0 ? ' attn' : n(m.warning) > 0 ? ' warn' : ''}`, href: `#/monitoring/customer/${co}`, children: [_jsxs("div", { className: "co-tile-head", children: [_jsx("b", { className: "co-name", children: m.company }), _jsxs("span", { className: "muted", children: [_jsx(CountUp, { value: n(m.devices), delay: delay }), " devices"] })] }), _jsxs("div", { className: "co-tile-body", children: [_jsx(MiniRing, { parts: statusParts(m, co), label: `Devices of ${m.company}`, delay: delay }), _jsxs("ul", { className: "co-figs", children: [_jsxs("li", { className: n(m.alertsCritical) > 0 ? 'bad' : '', children: [_jsx("i", { className: "dot tone-bad", "aria-hidden": "true" }), _jsx("span", { children: "Critical alerts" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.alertsCritical), delay: delay }) })] }), _jsxs("li", { className: n(m.alertsWarning) > 0 ? 'warn' : '', children: [_jsx("i", { className: "dot tone-warn", "aria-hidden": "true" }), _jsx("span", { children: "Warning alerts" }), _jsx("b", { children: _jsx(CountUp, { value: n(m.alertsWarning), delay: delay }) })] }), figs.map(([l, v]) => _jsxs("li", { className: v > 0 ? 'bad' : '', children: [_jsx("span", { children: l }), _jsx("b", { children: _jsx(CountUp, { value: v, delay: delay }) })] }, l))] })] })] });
}
/** One customer's monitoring rings; every part opens the device or alert list filtered to it. */
export function CustomerDash({ id }) {
    const url = id === 'all' ? '/api/monitoring/dashboard' : `/api/monitoring/dashboard?company=${encodeURIComponent(id)}`;
    const { data, error, loading, reload } = useLoad(url);
    useInterval(reload, 60_000);
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    if (error)
        return _jsx(Loading, { loading: false, error: error });
    const m = (id === 'all' ? data?.all : data?.customer);
    if (!m)
        return _jsx(Empty, { children: "This customer has no devices." });
    const co = id;
    const bm = (m.byMetric ?? {});
    return _jsxs(_Fragment, { children: [_jsxs("nav", { className: "crumbs", "aria-label": "Breadcrumb", children: [_jsx("a", { href: "#/monitoring", children: "Monitoring" }), _jsx("span", { "aria-hidden": "true", children: "\u203A" }), _jsx("span", { "aria-current": "page", children: m.company })] }), _jsxs("header", { className: "dash-head", children: [_jsx("h1", { children: m.company }), _jsxs("p", { className: "muted", children: [n(m.devices), " devices, ", n(m.watched), " watched, ", n(m.withMetrics), " sending performance data"] }), _jsx("a", { className: "button", href: `#/monitoring/reports/uptime?company=${co}`, children: "Uptime report" }), _jsx("a", { className: "button primary", href: devHref(co), children: "View all devices" })] }), _jsxs("div", { className: "rings dash-rings", children: [_jsx(Ring, { title: "Device status", unit: "devices", delay: 0, empty: "No devices in service.", parts: statusParts(m, co) }), _jsx(Ring, { title: "Open alerts", unit: "alerts", delay: 120, empty: "No open alerts. All clear.", parts: [
                            { key: 'c', label: 'Critical', value: n(m.alertsCritical), tone: 'bad', href: alertHref(co, 'severity=critical') },
                            { key: 'w', label: 'Warning', value: n(m.alertsWarning), tone: 'warn', href: alertHref(co, 'severity=warning') }
                        ] }), _jsx(BarList, { title: "Open alerts by kind", empty: "No open alerts.", rows: Object.keys(METRIC_WORDS).map((k) => ({ key: k, label: METRIC_WORDS[k], value: n(bm[k]), href: alertHref(co, 'metric=' + k) })) }), _jsx(Ring, { title: "Disk space", unit: "devices", delay: 360, empty: "No disk data yet. Agents 3.0 and later send it.", parts: [
                            { key: 'ok', label: '20% or more free', value: n(m.diskOk), tone: 'ok', href: devHref(co, 'disk=ok') },
                            { key: 'low', label: '10 to 20% free', value: n(m.diskLow), tone: 'warn', href: devHref(co, 'disk=low') },
                            { key: 'crit', label: 'Under 10% free', value: n(m.diskCritical), tone: 'bad', href: devHref(co, 'disk=critical') }
                        ] }), _jsx(Ring, { title: "Service checks", unit: "checks", delay: 480, empty: "No service checks. Add them on an asset's Checks tab.", parts: [
                            { key: 'ok', label: 'Passing', value: n(m.checksOk), tone: 'ok', href: devHref(co, 'check=ok') },
                            { key: 'f', label: 'Failing', value: n(m.checksFailing), tone: 'bad', href: devHref(co, 'check=failing') },
                            { key: 'n', label: 'Not run yet', value: n(m.checksNotRun), tone: 'none', href: devHref(co, 'check=notrun') }
                        ] }), _jsx(Ring, { title: "Watched ports", unit: "ports", delay: 720, empty: "No watched ports. Ports with a description are watched; tick others on a device's Ports list.", parts: [
                            { key: 'up', label: 'Up', value: Math.max(0, n(m.portsWatched) - n(m.portsDown)), tone: 'ok', href: devHref(co, 'kind=network') },
                            { key: 'down', label: 'Down', value: n(m.portsDown), tone: 'bad', href: devHref(co, 'port=down') }
                        ] }), _jsx(Ring, { title: "Watched services and programs", unit: "watched", delay: 840, empty: "Nothing watched. Add a rule of the kind Service or Program not running.", parts: [
                            { key: 'ok', label: 'Running', value: Math.max(0, n(m.servicesWatched) - n(m.servicesDown)), tone: 'ok', href: devHref(co) },
                            { key: 'down', label: 'Stopped or missing', value: n(m.servicesDown), tone: 'bad', href: devHref(co, 'service=down') }
                        ] }), _jsx(Ring, { title: "Certificates", unit: "checked", delay: 600, empty: "No certificate checks yet.", parts: [
                            { key: 'ok', label: 'More than 30 days left', value: n(m.certOk), tone: 'ok', href: devHref(co, 'cert=ok') },
                            { key: 's', label: '8 to 30 days left', value: n(m.certSoon), tone: 'warn', href: devHref(co, 'cert=soon') },
                            { key: 'u', label: '7 days or less, or expired', value: n(m.certUrgent), tone: 'bad', href: devHref(co, 'cert=urgent') }
                        ] })] }), _jsx("p", { className: "muted tile-foot", children: "Counts are for devices in service. A device is watched when it has an agent, a site controller pings it, or it has service checks. Rules decide when an alert opens; it clears by itself once the problem has gone for two checks in a row." })] });
}
/** Counts per category as bars, largest first: one colour, the number written beside each bar, each row opening its list. */
export function BarList({ title, rows, empty }) {
    const max = Math.max(1, ...rows.map((r) => r.value));
    const sorted = [...rows].sort((a, b) => b.value - a.value);
    const total = rows.reduce((s, r) => s + r.value, 0);
    return _jsxs("figure", { className: "ring bar-list", children: [_jsx("figcaption", { children: title }), total === 0 ? _jsx("p", { className: "muted", children: empty }) : _jsx("ul", { children: sorted.map((r) => _jsx("li", { children: _jsxs("a", { href: r.href, children: [_jsx("span", { className: "name", children: r.label }), _jsx("span", { className: "track", "aria-hidden": "true", children: _jsx("i", { style: { width: (r.value / max) * 100 + '%' } }) }), _jsx("b", { className: "num", children: _jsx(CountUp, { value: r.value }) })] }) }, r.key)) })] });
}
