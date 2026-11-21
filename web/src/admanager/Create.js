import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { Card, Err, Field, useSubmit } from '../ui';
import { OuPicker, ouPath } from './pickers';
import { act } from './Objects';
/** A new group (with its scope and type) or a new OU. */
export function CreateOther({ domain }) {
    const [what, setWhat] = useState('group');
    const [ou, setOu] = useState('');
    const [name, setName] = useState('');
    const [sam, setSam] = useState('');
    const [desc, setDesc] = useState('');
    const [scope, setScope] = useState('global');
    const [type, setType] = useState('security');
    const [done, setDone] = useState('');
    const f = useSubmit(() => undefined);
    return _jsxs(Card, { children: [_jsx("div", { className: "tabs", role: "tablist", children: ['group', 'ou'].map((k) => _jsx("button", { role: "tab", "aria-selected": what === k, className: what === k ? 'on' : '', onClick: () => { setWhat(k); setDone(''); }, children: k === 'group' ? 'Group' : 'Organisational unit' }, k)) }), _jsxs("div", { className: "form", children: [_jsxs("div", { children: [_jsx("span", { className: "lbl", children: "Create in" }), _jsx(OuPicker, { domain: domain, value: ou, onChange: setOu, allowDomain: what === 'ou' })] }), _jsx(Field, { label: "Name", children: _jsx("input", { value: name, onChange: (e) => { setName(e.target.value); if (what === 'group')
                                setSam(e.target.value.slice(0, 64)); } }) }), what === 'group' && _jsxs(_Fragment, { children: [_jsx(Field, { label: "Group name (pre-Windows 2000)", children: _jsx("input", { value: sam, maxLength: 64, onChange: (e) => setSam(e.target.value) }) }), _jsxs("fieldset", { children: [_jsx("legend", { children: "Scope" }), [['domainlocal', 'Domain local'], ['global', 'Global'], ['universal', 'Universal']].map(([k, l]) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: scope === k, onChange: () => setScope(k) }), " ", l] }, k))] }), _jsxs("fieldset", { children: [_jsx("legend", { children: "Type" }), [['security', 'Security'], ['distribution', 'Distribution']].map(([k, l]) => _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: type === k, onChange: () => setType(k) }), " ", l] }, k))] })] }), _jsx(Field, { label: "Description", children: _jsx("input", { value: desc, onChange: (e) => setDesc(e.target.value) }) }), _jsx(Err, { text: f.err }), done && _jsxs("p", { className: "ok", children: ["Created ", _jsx("code", { children: done }), "."] }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: f.busy || !ou || !name, onClick: () => void f.submit(async () => {
                                const r = await act(domain.id, what === 'group' ? { action: 'create-group', ou, name, sam, description: desc, scope, type } : { action: 'create-ou', ou, name, description: desc });
                                setDone(ouPath(String(r.dn), domain.baseDn));
                                setName('');
                                setSam('');
                                setDesc('');
                            }), children: "Create" }) })] })] });
}
