import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, go, useLoad } from '../http';
import { Err, Field, Card, useSubmit } from '../ui';
import { KIND, LEVEL, PRIORITY } from './List';
import { useSingleCompany } from '../mode';
import { Suggestions } from './Knowledge';
import { FieldInput, missingRequired, sendable, useFields } from './Fields';
/** The same table the server uses (ITIL): how many people it hurts and how soon it must be fixed. */
const PRIORITY_OF = { high: { high: 'critical', medium: 'high', low: 'medium' }, medium: { high: 'high', medium: 'medium', low: 'low' }, low: { high: 'medium', medium: 'low', low: 'low' } };
export default function NewTicket({ me }) {
    const staff = !!me?.staff;
    const one = useSingleCompany();
    const settings = useLoad('/api/servicedesk/settings');
    const companies = useLoad(staff ? '/api/assets/companies' : null);
    const mine = useLoad(staff ? null : '/api/servicedesk/my-devices');
    const devices = Array.isArray(mine.data) ? mine.data : [];
    const [device, setDevice] = useState('');
    const wanted = new URLSearchParams((location.hash.split('?')[1] ?? '')).get('kind') === 'request' ? 'request' : 'incident';
    const [v, setV] = useState({ kind: wanted, impact: '', urgency: '', priorityChosen: false, subject: '', description: '', priority: 'medium', category: '', companyId: '', requesterName: '', requesterEmail: '' });
    const set = (k, x) => setV({ ...v, [k]: x });
    // impact and urgency choose the priority until someone picks one by hand
    const levels = (k, x) => { const n = { ...v, [k]: x }; if (!n.priorityChosen && n.impact && n.urgency)
        n.priority = PRIORITY_OF[n.impact]?.[n.urgency] ?? n.priority; setV(n); };
    const f = useSubmit(() => undefined);
    const defs = useFields();
    const [cv, setCv] = useState({});
    const cats = (settings.data?.categories ?? []);
    const cos = Array.isArray(companies.data) ? companies.data : [];
    if (me && !me.canRaise)
        return _jsxs(_Fragment, { children: [_jsx("h1", { children: "New ticket" }), _jsx("p", { className: "muted", children: me.customer && !me.companyId ? 'Your account is not linked to a company yet. Ask your administrator.' : 'Your account cannot open tickets.' })] });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: v.kind === 'request' ? 'New service request' : 'New incident' }), _jsx(Card, { children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Type", children: _jsx("select", { value: v.kind, onChange: (e) => set('kind', e.target.value), children: Object.entries(KIND).map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) }), _jsx(Field, { label: "Subject", children: _jsx("input", { value: v.subject, onChange: (e) => set('subject', e.target.value), maxLength: 200, autoFocus: true }) }), !staff && _jsx(Suggestions, { subject: v.subject }), _jsx(Field, { label: "What is happening?", children: _jsx("textarea", { rows: 6, value: v.description, onChange: (e) => set('description', e.target.value) }) }), v.kind === 'incident' && _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Impact (how many people or services are hit)", children: _jsxs("select", { value: v.impact, onChange: (e) => levels('impact', e.target.value), children: [_jsx("option", { value: "", children: "Not sure" }), Object.entries(LEVEL).map(([k, n]) => _jsx("option", { value: k, children: n }, k))] }) }), _jsx(Field, { label: "Urgency (how soon it must be fixed)", children: _jsxs("select", { value: v.urgency, onChange: (e) => levels('urgency', e.target.value), children: [_jsx("option", { value: "", children: "Not sure" }), Object.entries(LEVEL).map(([k, n]) => _jsx("option", { value: k, children: n }, k))] }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Priority", children: _jsx("select", { value: v.priority, onChange: (e) => setV({ ...v, priority: e.target.value, priorityChosen: true }), children: Object.entries(PRIORITY).map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) }), _jsx(Field, { label: "Category", children: _jsxs("select", { value: v.category, onChange: (e) => set('category', e.target.value), children: [_jsx("option", { value: "", children: "Not sure" }), cats.map((c) => _jsx("option", { children: c }, c))] }) })] }), !staff && devices.length > 0 && _jsx(Field, { label: "Which device is it about?", children: _jsxs("select", { value: device, onChange: (e) => setDevice(e.target.value), children: [_jsx("option", { value: "", children: "Not about a device" }), devices.map((d) => _jsxs("option", { value: d.id, children: [d.name, " (", d.tag, ")"] }, d.id))] }) }), defs.map((d) => _jsx(FieldInput, { def: d, value: cv[d.key], onChange: (x) => setCv({ ...cv, [d.key]: x }) }, d.key)), staff && _jsxs(_Fragment, { children: [!one && _jsx(Field, { label: "Company", children: _jsxs("select", { value: v.companyId, onChange: (e) => set('companyId', e.target.value), children: [_jsx("option", { value: "", children: "None" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Raised by (name)", children: _jsx("input", { value: v.requesterName, onChange: (e) => set('requesterName', e.target.value) }) }), _jsx(Field, { label: "Raised by (email)", children: _jsx("input", { type: "email", value: v.requesterEmail, onChange: (e) => set('requesterEmail', e.target.value) }) })] })] }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => go('servicedesk'), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !v.subject.trim() || !!missingRequired(defs, cv, staff), onClick: () => f.submit(async () => {
                                        const body = { subject: v.subject, description: v.description, category: v.category, kind: v.kind, priority: v.priority };
                                        if (v.kind === 'incident') {
                                            if (v.impact)
                                                body.impact = v.impact;
                                            if (v.urgency)
                                                body.urgency = v.urgency;
                                        }
                                        const cu = sendable(defs, cv);
                                        if (Object.keys(cu).length)
                                            body.custom = cu;
                                        if (!staff && device)
                                            body.assetId = Number(device);
                                        if (staff) {
                                            if (v.companyId)
                                                body.companyId = Number(v.companyId);
                                            if (v.requesterName || v.requesterEmail) {
                                                body.requesterName = v.requesterName;
                                                body.requesterEmail = v.requesterEmail;
                                            }
                                        }
                                        const r = await api('/api/servicedesk/tickets', { body });
                                        go('servicedesk/' + r.id);
                                    }), children: "Open ticket" })] })] }) })] });
}
