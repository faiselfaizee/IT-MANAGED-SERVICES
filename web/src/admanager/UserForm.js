import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useMemo, useState } from 'react';
import { api, useLoad } from '../http';
import { Card, Err, Field, Modal, useSubmit } from '../ui';
import { ALL_FIELDS, DRIVES, TABS, applyName, cleanSam } from './fields';
import { ObjectPicker, OuPicker, ouPath } from './pickers';
export function FieldInput({ f, value, onChange }) {
    return _jsx(Field, { label: f.l, children: f.type === 'drive'
            ? _jsxs("select", { value: value, onChange: (e) => onChange(e.target.value), children: [_jsx("option", { value: "", children: "None" }), DRIVES.map((d) => _jsx("option", { children: d }, d))] })
            : _jsx("input", { value: value, onChange: (e) => onChange(e.target.value) }) });
}
/** The new-user form: every field of the account, its OU, groups and manager from the directory, a template to start from. */
export function NewUser({ domain, onDone }) {
    const info = useLoad(`/api/adsuite/domains/${domain.id}/info`);
    const templates = useLoad(`/api/adsuite/domains/${domain.id}/templates`);
    const [tab, setTab] = useState('account');
    const [tid, setTid] = useState(0);
    const [first, setFirst] = useState('');
    const [initials, setInitials] = useState('');
    const [last, setLast] = useState('');
    const [display, setDisplay] = useState('');
    const [displayTouched, setDT] = useState(false);
    const [sam, setSam] = useState('');
    const [samTouched, setST] = useState(false);
    const [suffix, setSuffix] = useState('');
    const [email, setEmail] = useState('');
    const [emailTouched, setET] = useState(false);
    const [ou, setOu] = useState('');
    const [groups, setGroups] = useState([]);
    const [manager, setManager] = useState([]);
    const [attrs, setAttrs] = useState({});
    const [pwMode, setPwMode] = useState('generate');
    const [pw, setPw] = useState('');
    const [pw2, setPw2] = useState('');
    const [mustChange, setMustChange] = useState(true);
    const [neverExpires, setNeverExpires] = useState(false);
    const [startDisabled, setStartDisabled] = useState(false);
    const [expires, setExpires] = useState('');
    const [done, setDone] = useState(null);
    const tpl = (templates.data ?? []).find((t) => t.id === tid);
    const naming = (tpl?.naming ?? {});
    const suffixes = (info.data?.upnSuffixes ?? [domain.dnsName]).filter(Boolean);
    useEffect(() => { if (!suffix && suffixes.length)
        setSuffix(suffixes[0]); }, [suffixes.join(','), suffix]); // eslint-disable-line react-hooks/exhaustive-deps
    // the names follow the person's name until someone types their own
    useEffect(() => { if (!displayTouched)
        setDisplay(applyName(String(naming.display || '{first} {last}'), first, last, initials)); }, [first, last, initials, naming.display, displayTouched]);
    useEffect(() => { if (!samTouched)
        setSam(cleanSam(applyName(String(naming.sam || '{first}.{last}'), first, last, initials)).toLowerCase()); }, [first, last, initials, naming.sam, samTouched]);
    useEffect(() => { if (!emailTouched && naming.email)
        setEmail(applyName(String(naming.email), first, last, initials).toLowerCase().replace(/\s+/g, '')); }, [first, last, initials, naming.email, emailTouched]);
    const applyTemplate = (id) => {
        setTid(id);
        const t = (templates.data ?? []).find((x) => x.id === id);
        if (!t)
            return;
        if (t.ou)
            setOu(String(t.ou));
        setGroups((t.groups ?? []).map((dn) => ({ dn, name: ouPath(dn).split(' › ').pop() ?? dn })));
        setAttrs({ ...t.attrs });
        const o = (t.options ?? {});
        if (typeof o.mustChange === 'boolean')
            setMustChange(o.mustChange);
        if (typeof o.pwdNeverExpires === 'boolean')
            setNeverExpires(o.pwdNeverExpires);
        if (t.naming?.upnSuffix)
            setSuffix(String(t.naming.upnSuffix));
        if (o.expiresDays) {
            const d = new Date(Date.now() + Number(o.expiresDays) * 86400000);
            setExpires(d.toISOString().slice(0, 10));
        }
        setDT(false);
        setST(false);
        setET(false);
    };
    const policy = info.data?.password;
    const pwProblem = pwMode === 'type' ? (pw === '' ? 'Type the password.' : pw !== pw2 ? 'The two passwords differ.' : policy?.minLength && pw.length < Number(policy.minLength) ? `At least ${policy.minLength} characters.` : '') : '';
    const missing = !ou ? 'Choose the OU.' : !display ? 'Give a name.' : !sam ? 'Give a sign-in name.' : pwProblem;
    const f = useSubmit(() => undefined);
    const submit = () => f.submit(async () => {
        const extra = {};
        for (const fd of ALL_FIELDS)
            if ((attrs[fd.k] ?? '') !== '')
                extra[fd.k] = attrs[fd.k];
        const r = await api(`/api/adsuite/domains/${domain.id}/actions`, { method: 'POST', body: {
                action: 'create-user', ou, first, last, display, sam, upn: `${sam}@${suffix}`, email, manager: manager[0]?.dn ?? '', groups: groups.map((g) => g.dn), attrs: { ...extra, initials },
                password: pwMode === 'type' ? pw : '', mustChange: mustChange && !neverExpires, pwdNeverExpires: neverExpires, disabled: startDisabled, expires
            } });
        setDone(r);
    });
    const tabs = useMemo(() => [{ key: 'account', title: 'Account' }, { key: 'groups', title: `Member of (${groups.length})` }, ...TABS.map((t) => ({ key: t.key, title: t.title }))], [groups.length]);
    if (done)
        return _jsxs(Card, { title: "Account created", children: [_jsxs("p", { children: [_jsx("strong", { children: display }), " was created in ", _jsx("code", { children: ouPath(ou, domain.baseDn) }), groups.length ? ` and added to ${groups.length} group(s)` : '', "."] }), done.password && _jsxs(_Fragment, { children: [_jsx("p", { children: "The password is shown once. Give it to the person safely." }), _jsx("p", { children: _jsx("code", { className: "copy", onClick: () => void navigator.clipboard?.writeText(String(done.password)), title: "Click to copy", children: String(done.password) }) })] }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", onClick: () => { onDone?.(String(done.dn)); }, children: "Open the account" }), _jsx("button", { onClick: () => { setDone(null); setFirst(''); setLast(''); setInitials(''); setDT(false); setST(false); setET(false); setEmail(''); setManager([]); setPw(''); setPw2(''); }, children: "Create another" })] })] });
    return _jsxs("div", { className: "userform", children: [_jsxs("div", { className: "formhead", children: [_jsxs("label", { className: "inline", children: ["Template ", _jsxs("select", { value: tid, onChange: (e) => applyTemplate(Number(e.target.value)), children: [_jsx("option", { value: 0, children: "None" }), (templates.data ?? []).map((t) => _jsx("option", { value: t.id, children: t.name }, t.id))] })] }), tpl?.description && _jsx("span", { className: "muted small", children: tpl.description })] }), _jsx("div", { className: "tabs", role: "tablist", children: tabs.map((t) => _jsx("button", { role: "tab", "aria-selected": tab === t.key, className: tab === t.key ? 'on' : '', onClick: () => setTab(t.key), children: t.title }, t.key)) }), _jsxs("div", { className: "tabbody", children: [tab === 'account' && _jsxs("div", { className: "grid2", children: [_jsx(Field, { label: "First name", children: _jsx("input", { value: first, onChange: (e) => setFirst(e.target.value), autoFocus: true }) }), _jsx(Field, { label: "Initials", children: _jsx("input", { value: initials, maxLength: 6, onChange: (e) => setInitials(e.target.value) }) }), _jsx(Field, { label: "Last name", children: _jsx("input", { value: last, onChange: (e) => setLast(e.target.value) }) }), _jsx(Field, { label: "Display name (also the account's name in the OU)", children: _jsx("input", { value: display, onChange: (e) => { setDisplay(e.target.value); setDT(true); } }) }), _jsx(Field, { label: "Sign-in name (pre-Windows 2000)", children: _jsx("input", { value: sam, maxLength: 20, onChange: (e) => { setSam(cleanSam(e.target.value)); setST(true); } }) }), _jsx(Field, { label: "User principal name", children: _jsxs("div", { className: "joined", children: [_jsx("input", { value: sam, readOnly: true, "aria-label": "UPN name" }), _jsx("span", { children: "@" }), _jsx("select", { "aria-label": "UPN suffix", value: suffix, onChange: (e) => setSuffix(e.target.value), children: suffixes.map((s) => _jsx("option", { children: s }, s)) })] }) }), _jsx(Field, { label: "Email", children: _jsx("input", { value: email, type: "email", onChange: (e) => { setEmail(e.target.value); setET(true); } }) }), _jsxs("div", { className: "wide", children: [_jsx("span", { className: "lbl", children: "OU" }), _jsx(OuPicker, { domain: domain, value: ou, onChange: setOu })] }), _jsxs("div", { className: "wide", children: [_jsx("span", { className: "lbl", children: "Manager" }), _jsx(ObjectPicker, { domain: domain, kind: "user", value: manager, onChange: setManager, label: "Manager", placeholder: "Search for the manager\u2026" })] }), _jsxs("fieldset", { className: "wide", children: [_jsx("legend", { children: "Password" }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: pwMode === 'generate', onChange: () => setPwMode('generate') }), " Make a strong password (shown once after creating)"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "radio", checked: pwMode === 'type', onChange: () => setPwMode('type') }), " Type a password"] }), pwMode === 'type' && _jsxs("div", { className: "grid2", children: [_jsx(Field, { label: "Password", children: _jsx("input", { type: "password", autoComplete: "new-password", value: pw, onChange: (e) => setPw(e.target.value) }) }), _jsx(Field, { label: "Again", children: _jsx("input", { type: "password", autoComplete: "new-password", value: pw2, onChange: (e) => setPw2(e.target.value) }) })] }), policy && _jsxs("p", { className: "muted small", children: ["Domain rules: at least ", String(policy.minLength), " characters", policy.complex ? ', three of upper case, lower case, digits and symbols, not containing the name' : '', policy.maxAgeDays ? `; expires after ${policy.maxAgeDays} days` : '', "."] }), !domain.tls && _jsx("p", { className: "muted small", children: "LDAPS is off for this domain: setting the password works only when the site controller runs on Windows." })] }), _jsxs("fieldset", { className: "wide", children: [_jsx("legend", { children: "Account options" }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: mustChange && !neverExpires, disabled: neverExpires, onChange: (e) => setMustChange(e.target.checked) }), " Must change password at next sign-in"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: neverExpires, onChange: (e) => setNeverExpires(e.target.checked) }), " Password never expires"] }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: startDisabled, onChange: (e) => setStartDisabled(e.target.checked) }), " Leave the account disabled"] }), _jsx(Field, { label: "Account expires (last day; empty = never)", children: _jsx("input", { type: "date", value: expires, onChange: (e) => setExpires(e.target.value) }) })] })] }), tab === 'groups' && _jsxs("div", { children: [_jsx("p", { className: "muted small", children: "Every account is in Domain Users. Add the groups this person needs:" }), _jsx(ObjectPicker, { domain: domain, kind: "group", multiple: true, value: groups, onChange: setGroups, label: "Groups", placeholder: "Search for a group\u2026" })] }), TABS.filter((t) => t.key === tab).map((t) => _jsx("div", { className: "grid2", children: t.fields.map((fd) => _jsx("div", { className: fd.wide ? 'wide' : '', children: _jsx(FieldInput, { f: fd, value: String(attrs[fd.k] ?? ''), onChange: (v) => setAttrs({ ...attrs, [fd.k]: v }) }) }, fd.k)) }, t.key))] }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions sticky", children: [_jsx("button", { className: "primary", disabled: f.busy || !!missing, onClick: () => void submit(), children: f.busy ? 'Creating…' : 'Create account' }), missing && _jsx("span", { className: "muted small", children: missing })] })] });
}
/** Edit an existing account: the same groups of fields. */
export function EditUser({ x, onClose, onSave }) {
    const ob = x.obj;
    const user = ob.kind === 'user';
    const start = { displayName: ob.name, givenName: '', sn: '', initials: '', mail: ob.mail ?? '', ...x.values };
    const [v, setV] = useState(start);
    const [tab, setTab] = useState(user ? 'names' : 'general');
    const { err, busy, submit } = useSubmit(onClose);
    const names = [{ k: 'givenName', l: 'First name' }, { k: 'initials', l: 'Initials' }, { k: 'sn', l: 'Last name' }, { k: 'displayName', l: 'Display name' }, { k: 'mail', l: 'Email' }];
    const tabs = user ? [{ key: 'names', title: 'Name', fields: names }, ...TABS] : [{ key: 'general', title: 'General', fields: [{ k: 'description', l: 'Description', wide: true }, { k: 'mail', l: 'Email' }, { k: 'info', l: 'Notes', wide: true }] }];
    return _jsxs(Modal, { title: 'Edit ' + String(ob.name), onClose: onClose, children: [_jsx("div", { className: "tabs", role: "tablist", children: tabs.map((t) => _jsx("button", { role: "tab", "aria-selected": tab === t.key, className: tab === t.key ? 'on' : '', onClick: () => setTab(t.key), children: t.title }, t.key)) }), tabs.filter((t) => t.key === tab).map((t) => _jsx("div", { className: "grid2", children: t.fields.map((fd) => _jsx("div", { className: fd.wide ? 'wide' : '', children: _jsx(FieldInput, { f: fd, value: String(v[fd.k] ?? ''), onChange: (val) => setV({ ...v, [fd.k]: val }) }) }, fd.k)) }, t.key)), _jsx(Err, { text: err }), _jsxs("div", { className: "actions", children: [_jsx("button", { className: "primary", disabled: busy, onClick: () => void submit(() => {
                            const changed = {};
                            for (const k of Object.keys(v))
                                if (k !== 'manager' && (v[k] ?? '') !== (start[k] ?? ''))
                                    changed[k] = v[k] ?? '';
                            return Object.keys(changed).length ? onSave(changed) : Promise.resolve();
                        }), children: "Save" }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] });
}
