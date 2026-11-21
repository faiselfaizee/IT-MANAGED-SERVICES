import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useLoad } from '../http';
import { Field, Loading } from '../ui';
/** Where alerts go (mail and Teams, with a customer's own where needed), escalation of unacknowledged alerts, and the monthly report. */
export default function Settings() {
    const { data, error, loading, reload } = useLoad('/api/monitoring/settings');
    const companies = useLoad('/api/assets/companies');
    const [v, setV] = useState({});
    const [own, setOwn] = useState([]);
    const [msg, setMsg] = useState('');
    const [err, setErr] = useState('');
    const [test, setTest] = useState({});
    const [month, setMonth] = useState(() => { const d = new Date(); d.setUTCDate(1); d.setUTCMonth(d.getUTCMonth() - 1); return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`; });
    useEffect(() => { if (data) {
        setV({ ...data });
        setOwn((data.companies ?? []).map((c) => ({ companyId: Number(c.companyId), recipients: String(c.recipients ?? ''), teams: String(c.teams ?? '') })));
    } }, [data]);
    const cos = (Array.isArray(companies.data) ? companies.data : (companies.data?.items ?? []));
    const set = (k, x) => setV({ ...v, [k]: x });
    const save = async () => {
        setMsg('');
        setErr('');
        try {
            await api('/api/monitoring/settings', { method: 'PUT', body: { ...v, escalateMinutes: Number(v.escalateMinutes ?? 0), companies: own } });
            setMsg('Saved.');
            reload();
        }
        catch (e) {
            setErr(e.message);
        }
    };
    const tryTeams = async (key, body) => {
        setTest({ ...test, [key]: 'Sending…' });
        try {
            await api('/api/monitoring/settings/teams-test', { method: 'POST', body });
            setTest({ ...test, [key]: 'Sent. Check the channel.' });
        }
        catch (e) {
            setTest({ ...test, [key]: e.message });
        }
    };
    const hookHint = 'Paste the webhook address from Teams (Workflows › "Post to a channel when a webhook request is received"). Saved addresses show only their host.';
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Monitoring settings" }), _jsxs("form", { className: "form", style: { maxWidth: 720 }, onSubmit: (e) => { e.preventDefault(); void save(); }, children: [_jsx("h2", { children: "Where alerts go" }), _jsx(Field, { label: "Mail to (addresses separated by commas)", children: _jsx("textarea", { rows: 2, value: v.recipients ?? '', onChange: (e) => set('recipients', e.target.value), placeholder: "noc@yourcompany.com, oncall@yourcompany.com" }) }), _jsx(Field, { label: "Microsoft Teams channel webhook", children: _jsx("input", { value: v.teams ?? '', onChange: (e) => set('teams', e.target.value), placeholder: "https://\u2026.webhook.office.com/\u2026 or a Workflows address", "aria-describedby": "hook-hint" }) }), _jsxs("p", { className: "muted small", id: "hook-hint", children: [hookHint, " Clear the field to stop posting."] }), _jsxs("div", { className: "tools", children: [_jsx("button", { type: "button", onClick: () => void tryTeams('main', String(v.teams ?? '').includes('/…') ? {} : { url: v.teams }), disabled: !v.teams, children: "Send a test message" }), test.main && _jsx("span", { className: "muted", role: "status", children: test.main })] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: v.mailResolved !== false, onChange: (e) => set('mailResolved', e.target.checked) }), " Also tell them when an alert clears (mail and Teams)"] }), _jsx("h2", { children: "Customers with their own recipients" }), _jsx("p", { className: "muted", children: "Alerts of these customers go to their own addresses or channel instead of the ones above. The wording of alert mail is under Mail \u203A Templates." }), own.map((o, i) => _jsxs("fieldset", { className: "own", children: [_jsxs("select", { value: o.companyId || '', "aria-label": "Customer", onChange: (e) => setOwn(own.map((x, j) => j === i ? { ...x, companyId: Number(e.target.value) } : x)), children: [_jsx("option", { value: "", children: "Choose a customer" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }), _jsx("input", { value: o.recipients, "aria-label": "Addresses", placeholder: "it@customer.com", onChange: (e) => setOwn(own.map((x, j) => j === i ? { ...x, recipients: e.target.value } : x)) }), _jsx("input", { value: o.teams, "aria-label": "Teams webhook", placeholder: "Teams webhook (optional)", onChange: (e) => setOwn(own.map((x, j) => j === i ? { ...x, teams: e.target.value } : x)) }), _jsx("button", { type: "button", onClick: () => setOwn(own.filter((_, j) => j !== i)), children: "Remove" })] }, i)), _jsx("div", { className: "tools", children: _jsx("button", { type: "button", onClick: () => setOwn([...own, { companyId: 0, recipients: '', teams: '' }]), children: "Add a customer" }) }), _jsx("h2", { children: "Escalation" }), _jsx("p", { className: "muted", children: "When nobody acknowledges a critical alert in time, it goes once to these people and this channel as well." }), _jsx(Field, { label: "Escalate after (minutes; 0 means never)", children: _jsx("input", { type: "number", min: 0, max: 1440, value: v.escalateMinutes ?? 0, onChange: (e) => set('escalateMinutes', e.target.value) }) }), _jsx(Field, { label: "Escalation mail to", children: _jsx("input", { value: v.escalateRecipients ?? '', onChange: (e) => set('escalateRecipients', e.target.value), placeholder: "manager@yourcompany.com" }) }), _jsx(Field, { label: "Escalation Teams webhook", children: _jsx("input", { value: v.escalateTeams ?? '', onChange: (e) => set('escalateTeams', e.target.value), placeholder: "Teams webhook of the escalation channel" }) }), _jsxs("div", { className: "tools", children: [_jsx("button", { type: "button", disabled: !v.escalateTeams, onClick: () => void tryTeams('esc', String(v.escalateTeams ?? '').includes('/…') ? { which: 'escalation' } : { url: v.escalateTeams }), children: "Send a test message" }), test.esc && _jsx("span", { className: "muted", role: "status", children: test.esc })] }), _jsx("h2", { children: "Monthly uptime report" }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: !!v.reportEnabled, onChange: (e) => set('reportEnabled', e.target.checked) }), " Mail last month's uptime report as a PDF on the 1st of every month"] }), _jsx(Field, { label: "Report of all customers to", children: _jsx("input", { value: v.reportTo ?? '', onChange: (e) => set('reportTo', e.target.value), placeholder: "management@yourcompany.com" }) }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: v.reportCustomers !== false, onChange: (e) => set('reportCustomers', e.target.checked) }), " Each customer with its own recipients (above) gets its own report"] }), _jsxs("div", { className: "tools", children: [_jsx("input", { type: "month", value: month, onChange: (e) => setMonth(e.target.value), "aria-label": "Report month" }), _jsx("button", { type: "button", onClick: async () => { try {
                                    await api('/api/monitoring/reports/send', { method: 'POST', body: { month } });
                                    setTest({ ...test, rep: 'Queued: the reports go out within a minute.' });
                                }
                                catch (e) {
                                    setTest({ ...test, rep: e.message });
                                } }, children: "Send the reports for this month now" }), test.rep && _jsx("span", { className: "muted", role: "status", children: test.rep })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), msg && _jsx("p", { className: "muted", role: "status", children: msg }), _jsx("div", { className: "tools", children: _jsx("button", { type: "submit", className: "primary", children: "Save settings" }) })] })] });
}
