import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from './http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, useSubmit } from './ui';
const MODULES = [['assets', 'Assets'], ['servicedesk', 'Service Desk'], ['monitoring', 'Monitoring'], ['backup', 'Backup']];
const ACTIONS = [['read', 'View'], ['add', 'Add'], ['modify', 'Change'], ['delete', 'Delete']];
const modName = (m) => MODULES.find(([k]) => k === m)?.[1] ?? m;
const actName = (a) => ACTIONS.find(([k]) => k === a)?.[1] ?? a;
/** Delegation: security groups, and who may view, add, change or delete in each module. */
export default function Access({ tab: fixed } = {}) {
    const [own, setTab] = useState('groups');
    if (fixed)
        return fixed === 'groups' ? _jsx(Groups, {}) : _jsx(Permissions, {}); // shown inside the Users page, which has the tabs
    const tab = own;
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Access" }), _jsxs("nav", { className: "sub", "aria-label": "Access", children: [_jsx("a", { href: "#/access", className: tab === 'groups' ? 'on' : '', onClick: (e) => { e.preventDefault(); setTab('groups'); }, children: "Groups" }), _jsx("a", { href: "#/access", className: tab === 'permissions' ? 'on' : '', onClick: (e) => { e.preventDefault(); setTab('permissions'); }, children: "Permissions" })] }), tab === 'groups' ? _jsx(Groups, {}) : _jsx(Permissions, {})] });
}
function Groups() {
    const { data, error, loading, reload } = useLoad('/api/platform/access/groups');
    const users = useLoad('/api/platform/users');
    const [edit, setEdit] = useState(null);
    const [adding, setAdding] = useState(null);
    const [msg, setMsg] = useState('');
    const list = Array.isArray(data) ? data : [];
    const run = async (f) => { setMsg(''); try {
        await f();
        reload();
    }
    catch (e) {
        setMsg(e.message);
    } };
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setEdit({ name: '', description: '' }), children: "New group" }), _jsx("span", { className: "muted", children: "A group can hold people and other groups. Give permissions to the group, not to each person." })] }), _jsx(Loading, { loading: loading && !data, error: error }), _jsx(Err, { text: msg }), data && (list.length === 0 ? _jsx(Empty, { children: "No groups yet." }) : list.map((g) => _jsxs(Card, { title: g.name, children: [g.description && _jsx("p", { className: "muted", children: g.description }), g.members.length === 0 ? _jsx("p", { className: "muted", children: "No members." }) : _jsx("ul", { className: "files", children: g.members.map((m) => _jsxs("li", { children: [m.name, " ", _jsx(Badge, { children: m.kind === 'group' ? 'Group' : 'Person' }), ' ', _jsx(Act, { label: "Remove", confirm: `Remove ${m.name} from ${g.name}?`, run: () => run(() => api(`/api/platform/access/groups/${g.id}/members/${m.kind}/${m.id}`, { method: 'DELETE' })) })] }, m.kind + m.id)) }), _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { onClick: () => setAdding(g), children: "Add member" }), _jsx("button", { onClick: () => setEdit(g), children: "Rename" }), _jsx(Act, { label: "Delete group", danger: true, confirm: `Delete ${g.name}? Its permissions go with it.`, run: () => run(() => api(`/api/platform/access/groups/${g.id}`, { method: 'DELETE' })) })] })] }, g.id))), edit && _jsx(GroupForm, { init: edit, onClose: (ch) => { setEdit(null); if (ch)
                    reload(); } }), adding && _jsx(MemberForm, { group: adding, groups: list, users: Array.isArray(users.data) ? users.data : [], onClose: (ch) => { setAdding(null); if (ch)
                    reload(); } })] });
}
function GroupForm({ init, onClose }) {
    const [v, setV] = useState(init);
    const f = useSubmit(() => onClose(true));
    return _jsx(Modal, { title: init.id ? 'Rename group' : 'New group', onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: v.name, onChange: (e) => setV({ ...v, name: e.target.value }), autoFocus: true }) }), _jsx(Field, { label: "What is it for?", children: _jsx("input", { value: v.description, onChange: (e) => setV({ ...v, description: e.target.value }) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !v.name.trim(), onClick: () => f.submit(() => init.id
                                ? api(`/api/platform/access/groups/${init.id}`, { method: 'PUT', body: { name: v.name, description: v.description } })
                                : api('/api/platform/access/groups', { body: { name: v.name, description: v.description } })), children: init.id ? 'Save changes' : 'Create group' })] })] }) });
}
function MemberForm({ group, groups, users, onClose }) {
    const [pick, setPick] = useState('');
    const f = useSubmit(() => onClose(true));
    const taken = new Set(group.members.map((m) => m.kind + ':' + m.id));
    const options = [...users.filter((u) => u.active).map((u) => ({ key: 'user:' + u.id, label: `${u.name} (${u.email})` })),
        ...groups.filter((g) => g.id !== group.id).map((g) => ({ key: 'group:' + g.id, label: `${g.name} (group)` }))].filter((o) => !taken.has(o.key));
    return _jsx(Modal, { title: `Add to ${group.name}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Person or group", children: _jsxs("select", { value: pick, onChange: (e) => setPick(e.target.value), autoFocus: true, children: [_jsx("option", { value: "", children: "Choose\u2026" }), options.map((o) => _jsx("option", { value: o.key, children: o.label }, o.key))] }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !pick, onClick: () => f.submit(() => { const [kind, id] = pick.split(':'); return api(`/api/platform/access/groups/${group.id}/members`, { body: { kind, id } }); }), children: "Add" })] })] }) });
}
function Permissions() {
    const { data, error, loading, reload } = useLoad('/api/platform/access/grants');
    const groups = useLoad('/api/platform/access/groups');
    const users = useLoad('/api/platform/users');
    const companies = useLoad('/api/assets/companies');
    const [adding, setAdding] = useState(false);
    const [msg, setMsg] = useState('');
    const list = Array.isArray(data) ? data : [];
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const coName = (id) => (id == null ? 'Every company' : cos.find((c) => c.id === id)?.name ?? `Company ${id}`);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => setAdding(true), children: "Give permission" }), _jsx("span", { className: "muted", children: "Administrators can always do everything. Staff can view Assets; customer administrators view their own company's assets. Everything else is given here." })] }), _jsx(Loading, { loading: loading && !data, error: error }), _jsx(Err, { text: msg }), data && (list.length === 0 ? _jsx(Empty, { children: "No permissions given yet." }) : _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Who" }), _jsx("th", { children: "Module" }), _jsx("th", { children: "May" }), _jsx("th", { children: "For" }), _jsx("th", {})] }) }), _jsx("tbody", { children: list.map((g) => _jsxs("tr", { children: [_jsxs("td", { children: [g.principal, " ", _jsx(Badge, { children: g.kind === 'group' ? 'Group' : 'Person' })] }), _jsx("td", { children: modName(g.module) }), _jsx("td", { children: actName(g.action) }), _jsx("td", { children: coName(g.companyId) }), _jsx("td", { children: _jsx(Act, { label: "Remove", confirm: "Remove this permission?", run: async () => { setMsg(''); try {
                                            await api(`/api/platform/access/grants/${g.id}`, { method: 'DELETE' });
                                            reload();
                                        }
                                        catch (e) {
                                            setMsg(e.message);
                                        } } }) })] }, g.id)) })] })), adding && _jsx(GrantForm, { groups: Array.isArray(groups.data) ? groups.data : [], users: Array.isArray(users.data) ? users.data : [], companies: cos, onClose: (ch) => { setAdding(false); if (ch)
                    reload(); } })] });
}
function GrantForm({ groups, users, companies, onClose }) {
    const [who, setWho] = useState('');
    const [module, setModule] = useState('assets');
    const [acts, setActs] = useState(['read']);
    const [company, setCompany] = useState('');
    const f = useSubmit(() => onClose(true));
    const toggle = (a) => setActs(acts.includes(a) ? acts.filter((x) => x !== a) : [...acts, a]);
    return _jsx(Modal, { title: "Give permission", onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "To", children: _jsxs("select", { value: who, onChange: (e) => setWho(e.target.value), autoFocus: true, children: [_jsx("option", { value: "", children: "Choose a group or person\u2026" }), _jsx("optgroup", { label: "Groups", children: groups.map((g) => _jsx("option", { value: 'group:' + g.id, children: g.name }, g.id)) }), _jsx("optgroup", { label: "People", children: users.filter((u) => u.active).map((u) => _jsxs("option", { value: 'user:' + u.id, children: [u.name, " (", u.email, ")"] }, u.id)) })] }) }), _jsx(Field, { label: "Module", children: _jsx("select", { value: module, onChange: (e) => setModule(e.target.value), children: MODULES.map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) }), _jsxs("div", { children: [_jsx("span", { className: "muted", children: "May" }), _jsx("div", { className: "actions", style: { justifyContent: 'flex-start', flexWrap: 'wrap' }, children: ACTIONS.map(([k, n]) => _jsxs("label", { children: [_jsx("input", { type: "checkbox", checked: acts.includes(k), onChange: () => toggle(k) }), " ", n] }, k)) })] }), _jsx(Field, { label: "For", children: _jsxs("select", { value: company, onChange: (e) => setCompany(e.target.value), children: [_jsx("option", { value: "", children: "Every company" }), companies.map((c) => _jsxs("option", { value: c.id, children: [c.name, " only"] }, c.id))] }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !who || acts.length === 0, onClick: () => f.submit(async () => {
                                const [kind, principalId] = who.split(':');
                                for (const action of acts)
                                    await api('/api/platform/access/grants', { body: { kind, principalId, module, action, companyId: company ? Number(company) : null } }).catch((e) => { if (e.status !== 409)
                                        throw e; });
                            }), children: "Give permission" })] })] }) });
}
