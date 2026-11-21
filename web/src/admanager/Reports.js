import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { download, useLoad } from '../http';
import { Act, Badge, Card, Empty, Loading, fmtDate } from '../ui';
import { DomainPick, useDomain } from './AdApp';
export default function Reports({ domains }) {
    const [domain, pick] = useDomain(domains);
    const cat = useLoad('/api/adsuite/reports');
    const [key, setKey] = useState('inactive-users');
    const [days, setDays] = useState(90);
    const [run, setRun] = useState(0);
    const def = (cat.data ?? []).find((c) => c.key === key);
    const q = `report=${key}${def?.days ? '&days=' + days : ''}`;
    const rep = useLoad(domain && run > 0 ? `/api/adsuite/domains/${domain.id}/report?${q}` : null, [run]);
    if (!domain)
        return null;
    const rows = (rep.data?.items ?? []);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx(DomainPick, { domains: domains, value: domain, onPick: pick }), _jsx("select", { "aria-label": "Report", value: key, onChange: (e) => { setKey(e.target.value); const d = (cat.data ?? []).find((c) => c.key === e.target.value); if (d?.defaultDays)
                            setDays(Number(d.defaultDays)); }, children: (cat.data ?? []).map((c) => _jsx("option", { value: c.key, children: c.title }, c.key)) }), def?.days ? _jsxs("label", { className: "inline", children: ["Days ", _jsx("input", { "aria-label": "Days", type: "number", min: 1, max: 3650, value: days, onChange: (e) => setDays(Number(e.target.value)), style: { width: 80 } })] }) : null, _jsx("button", { className: "primary", onClick: () => setRun(run + 1), children: "Run" }), rep.data && _jsx(Act, { label: "Download CSV", run: () => download(`/api/adsuite/domains/${domain.id}/report?${q}&format=csv`, `${key}.csv`) })] }), def && _jsx("p", { className: "muted", children: String(def.description) }), _jsx(Loading, { loading: rep.loading, error: rep.error }), rep.data && _jsxs(Card, { title: `${String(rep.data.title)}: ${rows.length}${rep.data.truncated ? '+' : ''}`, children: [rows.length === 0 ? _jsx(Empty, { children: "Nothing to report. That is good news." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Sign-in" }), _jsx("th", { children: "Last sign-in" }), _jsx("th", { children: "Expires" }), _jsx("th", {})] }) }), _jsx("tbody", { children: rows.map((r) => _jsxs("tr", { children: [_jsxs("td", { children: [r.name, _jsx("div", { className: "muted small", children: r.description || r.dns })] }), _jsx("td", { children: r.sam }), _jsx("td", { children: r.lastLogon ? fmtDate(r.lastLogon) : '–' }), _jsx("td", { children: r.accountExpires ? fmtDate(r.accountExpires) : '–' }), _jsxs("td", { children: [r.disabled && _jsx(Badge, { tone: "bad", children: "Disabled" }), r.locked && _jsx(Badge, { tone: "bad", children: "Locked" })] })] }, r.dn)) })] }), rep.data.truncated ? _jsx("p", { className: "muted small", children: "The list was cut short. Narrow the days or download what is here." }) : null] })] });
}
