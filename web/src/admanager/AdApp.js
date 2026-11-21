import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { go, useHash, useLoad } from '../http';
import { Badge, Card, Empty, Loading } from '../ui';
import { Ring } from '../Rings';
import Domains from './Domains';
import Reports from './Reports';
import Requests from './Requests';
import { ObjectList, Panel, has } from './Objects';
import { NewUser } from './UserForm';
import { BulkCreate, BulkModify } from './Bulk';
import { Automation, Templates } from './Settings';
import { CreateOther } from './Create';
const MENU = [
    { group: 'Overview', items: [{ key: '', label: 'Overview' }] },
    { group: 'Management', items: [{ key: 'users', label: 'Users' }, { key: 'groups', label: 'Groups' }, { key: 'computers', label: 'Computers' }, { key: 'ous', label: 'OUs' }] },
    { group: 'Create', items: [{ key: 'new-user', label: 'New user', right: 'create' }, { key: 'new-other', label: 'New group or OU', right: 'create' }, { key: 'bulk-create', label: 'Users from a file', right: 'create' }, { key: 'bulk-modify', label: 'Change from a file', right: 'modify' }] },
    { group: 'Workflow', items: [{ key: 'requests', label: 'Account requests' }, { key: 'approvals', label: 'Approvals' }] },
    { group: 'Reports', items: [{ key: 'reports', label: 'All reports' }] },
    { group: 'Settings', items: [{ key: 'automation', label: 'Automation', admin: true }, { key: 'templates', label: 'User templates', admin: true }, { key: 'domains', label: 'Domains and delegation', admin: true }] },
];
/** Remembers which domain is open, so every screen works on the same one. */
export function useDomain(domains) {
    const [id, setId] = useState(() => { try {
        return Number(sessionStorage.getItem('ad.domain') ?? 0);
    }
    catch {
        return 0;
    } });
    const d = domains.find((x) => x.id === id) ?? domains.find((x) => x.enabled) ?? domains[0] ?? null;
    return [d, (n) => { setId(n); try {
            sessionStorage.setItem('ad.domain', String(n));
        }
        catch { /* not kept */ } }];
}
export function DomainPick({ domains, value, onPick }) {
    return _jsx("select", { "aria-label": "Domain", value: value?.id ?? '', onChange: (e) => onPick(Number(e.target.value)), children: domains.map((d) => _jsxs("option", { value: d.id, children: [d.company ? d.company + ' · ' : '', d.name] }, d.id)) });
}
function Overview({ domain }) {
    const o = useLoad(`/api/adsuite/domains/${domain.id}/overview`, [domain.id]);
    if (!o.data)
        return _jsx(Loading, { loading: o.loading, error: o.error });
    const u = o.data.users, a = o.data.activity, c = o.data.computers;
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: String(domain.name) }), _jsxs("div", { className: "rings", children: [_jsx(Ring, { title: "User accounts", parts: [{ key: 'on', label: 'Enabled', value: u.enabled - u.locked, tone: 'ok', href: '#/admanager/users' }, { key: 'lock', label: 'Locked', value: u.locked, tone: 'bad', href: '#/admanager/users' }, { key: 'off', label: 'Disabled', value: u.disabled, tone: 'none', href: '#/admanager/users' }] }), _jsx(Ring, { title: "Sign-in activity", parts: [{ key: '30', label: 'Last 30 days', value: a.last30, tone: 'ok' }, { key: '90', label: '30 to 90 days', value: a.last90, tone: 'warn' }, { key: 'old', label: 'Over 90 days', value: a.older, tone: 'bad', href: '#/admanager/reports' }, { key: 'never', label: 'Never', value: a.never, tone: 'none' }] }), _jsx(Ring, { title: "Computers", parts: [{ key: 'a', label: 'Active', value: c.active, tone: 'ok' }, { key: 's', label: 'Stale (90 days)', value: c.stale, tone: 'warn', href: '#/admanager/reports' }, { key: 'd', label: 'Disabled', value: c.disabled, tone: 'none' }] })] }), _jsxs("div", { className: "tiles", children: [_jsxs("a", { className: "tile-s", href: "#/admanager/reports", children: [_jsx("strong", { children: String(u.privileged) }), _jsx("span", { children: "Privileged accounts" })] }), _jsxs("a", { className: "tile-s", href: "#/admanager/reports", children: [_jsx("strong", { children: String(u.expired) }), _jsx("span", { children: "Expired accounts" })] }), _jsxs("a", { className: "tile-s", href: "#/admanager/approvals", children: [_jsx("strong", { children: String(o.data.waiting) }), _jsx("span", { children: "Waiting for approval" })] })] }), o.data.truncated && _jsx("p", { className: "muted small", children: "Counted the first 20,000 objects only." })] });
}
/** The AD Manager module: manage a customer's Active Directory through its site controller. */
export default function AdApp() {
    const parts = useHash();
    const section = parts[1] ?? '';
    const me = useLoad('/api/platform/me');
    const list = useLoad('/api/adsuite/domains');
    const domains = (list.data ?? []);
    const [domain, pick] = useDomain(domains);
    const [open, setOpen] = useState('');
    const admin = me.data?.role === 'admin';
    if (list.error || list.loading || !list.data)
        return _jsx(Loading, { loading: list.loading, error: list.error });
    const may = (i) => (!i.admin || admin) && (!i.right || admin || has(domain, i.right));
    let page;
    if (section === 'domains' && admin)
        page = _jsx(Domains, { domains: domains, onChanged: list.reload });
    else if (!domain)
        page = _jsx(Card, { children: _jsx(Empty, { children: admin ? _jsxs(_Fragment, { children: ["No directory is connected yet. ", _jsx("a", { href: "#/admanager/domains", children: "Add a domain" }), "."] }) : 'No directory has been shared with you yet. Ask your administrator.' }) });
    else if (!domain.enabled)
        page = _jsx(Card, { children: _jsx("p", { className: "err", children: "This domain is switched off." }) });
    else {
        const kinds = { users: 'user', groups: 'group', computers: 'computer', ous: 'ou' };
        if (kinds[section])
            page = _jsxs("div", { className: "masterdetail", children: [_jsx(ObjectList, { domain: domain, kind: kinds[section], open: open, onOpen: setOpen }, section + domain.id), open ? _jsx(Panel, { domain: domain, dn: open, onChanged: () => undefined, onOpen: setOpen, onClose: () => setOpen('') }, open)
                        : _jsx("div", { className: "panel placeholder", children: _jsx("p", { className: "muted", children: "Choose one on the left to see its details and what you can do with it." }) })] });
        else if (section === 'new-user')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "New user" }), _jsx(NewUser, { domain: domain, onDone: (dn) => { setOpen(dn); go('admanager/users'); } })] });
        else if (section === 'new-other')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "New group or OU" }), _jsx(CreateOther, { domain: domain })] });
        else if (section === 'bulk-create')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Users from a file" }), _jsx(BulkCreate, { domain: domain })] });
        else if (section === 'bulk-modify')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Change users from a file" }), _jsx(BulkModify, { domain: domain })] });
        else if (section === 'reports')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Reports" }), _jsx(Reports, { domains: domains })] });
        else if (section === 'requests')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Account requests" }), _jsx(Requests, { domains: domains, mode: "requests" })] });
        else if (section === 'approvals')
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Approvals" }), _jsx(Requests, { domains: domains, mode: "approvals" })] });
        else if (section === 'automation' && admin)
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Automation" }), _jsx(Automation, { domain: domain })] });
        else if (section === 'templates' && admin)
            page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "User templates" }), _jsx(Templates, { domain: domain })] });
        else
            page = _jsx(Overview, { domain: domain });
    }
    return _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": "AD Manager", children: [_jsxs("div", { className: "modnav-head", children: [_jsx("strong", { children: "AD Manager" }), domains.length > 0 && _jsx(DomainPick, { domains: domains, value: domain, onPick: (n) => { pick(n); setOpen(''); } }), domain && !domain.enabled && _jsx(Badge, { tone: "bad", children: "Off" })] }), MENU.map((g) => {
                        const items = g.items.filter(may);
                        return items.length === 0 ? null : _jsxs("div", { className: "modnav-group", children: [g.group && _jsx("h4", { children: g.group }), items.map((i) => _jsx("a", { href: '#/admanager' + (i.key ? '/' + i.key : ''), className: section === i.key ? 'on' : '', onClick: () => setOpen(''), children: i.label }, i.key))] }, g.group);
                    })] }), _jsx("main", { className: "modmain", children: page })] });
}
