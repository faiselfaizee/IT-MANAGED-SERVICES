import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { Card, Empty, Err, Field, useSubmit } from '../ui';
export const FIELD_KIND = { text: 'Short text', longtext: 'Long text', number: 'Number', date: 'Date', choice: 'Choice from a list', checkbox: 'Yes or no' };
/** The extra fields an administrator added to tickets (only the ones this person may see). */
export const useFields = () => {
    const r = useLoad('/api/servicedesk/fields');
    return (Array.isArray(r.data?.items) ? r.data.items : []);
};
/** One input for one field. An empty value means "not filled in". */
export function FieldInput({ def, value, onChange }) {
    const label = def.label + (def.staffOnly ? ' (team only)' : '');
    const text = value === undefined || value === null ? '' : String(value);
    return _jsx(Field, { label: label + (def.required ? ' *' : ''), children: def.kind === 'longtext' ? _jsx("textarea", { rows: 3, value: text, onChange: (e) => onChange(e.target.value) })
            : def.kind === 'choice' ? _jsxs("select", { value: text, onChange: (e) => onChange(e.target.value), children: [_jsx("option", { value: "", children: "Not set" }), def.options.map((o) => _jsx("option", { children: o }, o))] })
                : def.kind === 'checkbox' ? _jsx("input", { type: "checkbox", checked: value === true, onChange: (e) => onChange(e.target.checked) })
                    : def.kind === 'number' ? _jsx("input", { type: "number", value: text, onChange: (e) => onChange(e.target.value === '' ? '' : Number(e.target.value)) })
                        : def.kind === 'date' ? _jsx("input", { type: "date", value: text, onChange: (e) => onChange(e.target.value) })
                            : _jsx("input", { value: text, maxLength: 500, onChange: (e) => onChange(e.target.value) }) });
}
/** Values to send: only fields that have something in them (checkboxes always count). */
export const sendable = (defs, values) => {
    const o = {};
    for (const d of defs) {
        const v = values[d.key];
        if (v === undefined || v === '' || v === null)
            continue;
        if (d.kind === 'checkbox' && v === false)
            continue;
        o[d.key] = v;
    }
    return o;
};
export const missingRequired = (defs, values, staff) => defs.find((d) => d.required && (staff || !d.staffOnly) && (values[d.key] === undefined || values[d.key] === '' || values[d.key] === null || (d.kind === 'checkbox' && values[d.key] === false)));
/** The ticket screen's card: staff change values (saved when they leave the box), everyone else just reads them. */
export function CustomCard({ defs, values, edit, save }) {
    const [draft, setDraft] = useState({});
    const f = useSubmit(() => undefined);
    const shown = edit ? defs : defs.filter((d) => values[d.key] !== undefined && values[d.key] !== null && values[d.key] !== '');
    if (shown.length === 0)
        return null;
    const now = (d) => (d.key in draft ? draft[d.key] : values[d.key]);
    const commit = (d) => { if (!(d.key in draft))
        return; const v = draft[d.key]; const next = { ...draft }; delete next[d.key]; setDraft(next); f.submit(() => save({ custom: { [d.key]: v === '' ? null : v } })); };
    return _jsx(Card, { title: "Extra details", children: edit ? _jsxs("div", { className: "form", children: [shown.map((d) => _jsx("div", { onBlur: () => commit(d), children: _jsx(FieldInput, { def: d, value: now(d), onChange: (v) => { setDraft({ ...draft, [d.key]: v }); if (d.kind === 'checkbox' || d.kind === 'choice') {
                            f.submit(() => save({ custom: { [d.key]: v === '' ? null : v } }));
                            const n = { ...draft };
                            delete n[d.key];
                            setDraft(n);
                        } } }) }, d.key)), _jsx(Err, { text: f.err })] })
            : _jsx("dl", { className: "dl", children: shown.map((d) => _jsxs("div", { children: [_jsx("dt", { children: d.label }), _jsx("dd", { children: d.kind === 'checkbox' ? (values[d.key] ? 'Yes' : 'No') : String(values[d.key]) })] }, d.key)) }) });
}
/** Settings: add, rename, reorder (by order of creation), switch off and delete fields. */
export function FieldsAdmin() {
    const { data, error, reload } = useLoad('/api/servicedesk/fields?all=true');
    const items = (Array.isArray(data?.items) ? data.items : []);
    const [v, setV] = useState({ label: '', kind: 'text', options: '', required: false, staffOnly: false });
    const f = useSubmit(() => { setV({ label: '', kind: 'text', options: '', required: false, staffOnly: false }); reload(); });
    const upd = (d, b) => f.submit(() => api('/api/servicedesk/fields/' + d.id, { method: 'PUT', body: b }));
    return _jsxs(Card, { title: "Extra ticket fields", children: [_jsx("p", { className: "muted", children: "Add your own questions to every ticket, such as a site code or a cost centre. A required field must be filled in when a ticket is opened. A team-only field is hidden from customers." }), error ? _jsx(Err, { text: error }) : items.length === 0 ? _jsx(Empty, { children: "No extra fields yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Name" }), _jsx("th", { children: "Type" }), _jsx("th", { children: "Required" }), _jsx("th", { children: "Team only" }), _jsx("th", { children: "In use" }), _jsx("th", {})] }) }), _jsx("tbody", { children: items.map((d) => _jsxs("tr", { children: [_jsxs("td", { children: [d.label, d.kind === 'choice' && _jsx("div", { className: "muted", children: d.options.join(', ') })] }), _jsx("td", { children: FIELD_KIND[d.kind] }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: d.required, "aria-label": 'Required: ' + d.label, onChange: (e) => upd(d, { required: e.target.checked }) }) }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: d.staffOnly, "aria-label": 'Team only: ' + d.label, onChange: (e) => upd(d, { staffOnly: e.target.checked }) }) }), _jsx("td", { children: _jsx("input", { type: "checkbox", checked: d.active, "aria-label": 'In use: ' + d.label, onChange: (e) => upd(d, { active: e.target.checked }) }) }), _jsx("td", { style: { textAlign: 'right' }, children: _jsx("button", { onClick: () => { if (confirm('Delete "' + d.label + '"? Values already saved on tickets stay hidden.'))
                                            f.submit(() => api('/api/servicedesk/fields/' + d.id, { method: 'DELETE' })); }, children: "Delete" }) })] }, d.id)) })] }), _jsxs("div", { className: "form", style: { marginTop: 12 }, children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "New field name", children: _jsx("input", { value: v.label, maxLength: 80, onChange: (e) => setV({ ...v, label: e.target.value }) }) }), _jsx(Field, { label: "Type", children: _jsx("select", { value: v.kind, onChange: (e) => setV({ ...v, kind: e.target.value }), children: Object.entries(FIELD_KIND).map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) })] }), v.kind === 'choice' && _jsx(Field, { label: "Choices (one per line)", children: _jsx("textarea", { rows: 3, value: v.options, onChange: (e) => setV({ ...v, options: e.target.value }) }) }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: v.required, onChange: (e) => setV({ ...v, required: e.target.checked }) }), " Required"] }), _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: v.staffOnly, onChange: (e) => setV({ ...v, staffOnly: e.target.checked }) }), " Team only"] }), _jsx(Err, { text: f.err }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: f.busy || !v.label.trim(), onClick: () => f.submit(() => api('/api/servicedesk/fields', { body: v })), children: "Add field" }) })] })] });
}
