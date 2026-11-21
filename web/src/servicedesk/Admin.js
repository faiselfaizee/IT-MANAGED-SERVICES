import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useLoad } from '../http';
import { Card, Empty, Err, Loading, useSubmit } from '../ui';
import { PRIORITY } from './List';
import { FieldsAdmin } from './Fields';
/** Categories and service levels. */
export function Settings() {
    const { data, error, loading, reload } = useLoad('/api/servicedesk/settings');
    const [cats, setCats] = useState('');
    const [sla, setSla] = useState(null);
    const [saved, setSaved] = useState(false);
    const f = useSubmit(() => { setSaved(true); reload(); });
    useEffect(() => { if (data) {
        setCats(data.categories.join('\n'));
        setSla(structuredClone(data.sla));
    } }, [data]);
    if (!sla)
        return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Service Desk settings" }), _jsx(Loading, { loading: loading, error: error })] });
    const hours = (g, p, x) => setSla({ ...sla, [g]: { ...sla[g], [p]: Number(x) } });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Service Desk settings" }), _jsxs(Card, { title: "Categories", children: [_jsx("p", { className: "muted", children: "One per line. Tickets are sorted into these." }), _jsx("textarea", { rows: 7, value: cats, onChange: (e) => { setCats(e.target.value); setSaved(false); }, "aria-label": "Categories" })] }), _jsxs(Card, { title: "Service levels", children: [_jsx("p", { className: "muted", children: "Hours from opening a ticket. The clock stops while a ticket waits for the customer." }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Priority" }), _jsx("th", { children: "First reply within (hours)" }), _jsx("th", { children: "Resolved within (hours)" })] }) }), _jsx("tbody", { children: Object.entries(PRIORITY).map(([k, n]) => _jsxs("tr", { children: [_jsx("td", { children: n }), _jsx("td", { children: _jsx("input", { type: "number", min: 0.25, step: 0.25, value: sla.respond[k], onChange: (e) => { hours('respond', k, e.target.value); setSaved(false); }, "aria-label": `${n} first reply` }) }), _jsx("td", { children: _jsx("input", { type: "number", min: 0.25, step: 0.25, value: sla.resolve[k], onChange: (e) => { hours('resolve', k, e.target.value); setSaved(false); }, "aria-label": `${n} resolution` }) })] }, k)) })] }), _jsxs("div", { className: "form", style: { marginTop: 12 }, children: [_jsxs("label", { children: [_jsx("span", { children: "Warn at (percent of the time used)" }), _jsx("input", { type: "number", min: 10, max: 99, value: sla.warnPct, onChange: (e) => { setSla({ ...sla, warnPct: Number(e.target.value) }); setSaved(false); } })] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: sla.pause, onChange: (e) => { setSla({ ...sla, pause: e.target.checked }); setSaved(false); } }), " Stop the clock while waiting for the customer"] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: sla.escalate, onChange: (e) => { setSla({ ...sla, escalate: e.target.checked }); setSaved(false); } }), " Record warnings and missed targets on the ticket"] })] })] }), _jsx(FieldsAdmin, {}), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("span", { className: "muted", children: saved ? 'Saved. Open tickets use the new targets.' : '' }), _jsx("button", { className: "primary", disabled: f.busy, onClick: () => f.submit(() => api('/api/servicedesk/settings', { method: 'PUT', body: { categories: cats.split('\n').map((c) => c.trim()).filter(Boolean), sla } })), children: "Save settings" })] })] });
}
/** Which company each customer user belongs to, and what they may do. */
export function People() {
    const { data, error, loading, reload } = useLoad('/api/servicedesk/people');
    const companies = useLoad('/api/assets/companies');
    const [msg, setMsg] = useState('');
    const list = Array.isArray(data) ? data : [];
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const save = async (p, change) => {
        setMsg('');
        try {
            await api(`/api/servicedesk/people/${p.id}`, { method: 'PUT', body: { companyId: p.companyId, mayRaise: p.mayRaise, mayModify: p.mayModify, ...change } });
            reload();
        }
        catch (e) {
            setMsg(e.message);
        }
    };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Customer users" }), _jsx("p", { className: "muted", children: "Add customer users on the Users page (role Customer or Customer administrator), then link each to their company here." }), _jsx(Loading, { loading: loading && !data, error: error }), _jsx(Err, { text: msg }), data && (list.length === 0 ? _jsx(Empty, { children: "No customer users yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Company" }), _jsx("th", { children: "May open tickets" }), _jsx("th", { children: "May reply and close" })] }) }), _jsx("tbody", { children: list.map((p) => _jsxs("tr", { children: [_jsxs("td", { children: [p.name, _jsxs("div", { className: "muted", children: [p.email, p.active ? '' : ' · disabled'] })] }), _jsx("td", { children: _jsxs("select", { value: p.companyId ?? '', "aria-label": `Company of ${p.name}`, onChange: (e) => save(p, { companyId: e.target.value ? Number(e.target.value) : null }), children: [_jsx("option", { value: "", children: "Not linked" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: p.mayRaise, "aria-label": `${p.name} may open tickets`, onChange: (e) => save(p, { mayRaise: e.target.checked }) }) }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: p.mayModify, "aria-label": `${p.name} may reply`, onChange: (e) => save(p, { mayModify: e.target.checked }) }) })] }, p.id)) })] }))] });
}
