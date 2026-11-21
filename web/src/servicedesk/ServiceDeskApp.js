import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useHash, useLoad } from '../http';
import Detail from './Detail';
import List from './List';
import Dashboard from './Dashboard';
import NewTicket from './NewTicket';
import { People, Settings } from './Admin';
import Integrations from './Integrations';
import Reports from './Reports';
import { ChangeDetail, ChangeList, NewChange } from './Changes';
import { NewProblem, ProblemDetail, ProblemList } from './Problems';
import MyTickets from './MyTickets';
import { KbArticle, KbList } from './Knowledge';
import { Approvals, Catalog, CatalogAdmin, ChangeBoard, ChangeCalendar, KnownErrors, StandardChanges } from './Itil';
/** The Service Desk module: routes inside the #/servicedesk address. */
export default function ServiceDeskApp() {
    const parts = useHash();
    const [, a, b] = parts;
    const me = useLoad('/api/servicedesk/me');
    const m = me.data;
    const staff = !!m?.staff;
    const admin = m?.role === 'admin';
    const sees = staff || m?.role === 'viewer';
    const isId = /^\d+$/.test(a ?? '');
    let page;
    if (isId)
        page = _jsx(Detail, { id: a });
    else if (a === 'changes' && sees)
        page = b === 'new' && staff ? _jsx(NewChange, {}) : /^\d+$/.test(b ?? '') ? _jsx(ChangeDetail, { id: b, staff: staff, admin: admin, myId: m?.id }) : _jsx(ChangeList, { staff: staff });
    else if (a === 'problems' && sees)
        page = b === 'new' && staff ? _jsx(NewProblem, {}) : /^\d+$/.test(b ?? '') ? _jsx(ProblemDetail, { id: b, staff: staff }) : _jsx(ProblemList, { staff: staff });
    else if (a === 'incidents')
        page = _jsx(List, { me: m, kind: "incident" }, "incidents");
    else if (a === 'major')
        page = _jsx(List, { me: m, kind: "incident", major: true }, "major");
    else if (a === 'requests')
        page = _jsx(List, { me: m, kind: "request" }, "requests");
    else if (a === 'new')
        page = _jsx(NewTicket, { me: m });
    else if (a === 'kb')
        page = b ? _jsx(KbArticle, { id: b, staff: staff }, b) : _jsx(KbList, { staff: staff });
    else if (a === 'catalog')
        page = _jsx(Catalog, { staff: staff });
    else if (a === 'approvals' && staff)
        page = _jsx(Approvals, {});
    else if (a === 'known-errors' && sees)
        page = _jsx(KnownErrors, {});
    else if (a === 'calendar' && sees)
        page = _jsx(ChangeCalendar, {});
    else if (a === 'standard' && sees)
        page = _jsx(StandardChanges, { admin: admin });
    else if (a === 'catalog-items' && admin)
        page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Catalogue items" }), _jsx(CatalogAdmin, {})] });
    else if (a === 'cab' && admin)
        page = _jsxs(_Fragment, { children: [_jsx("h1", { children: "Change advisory board" }), _jsx(ChangeBoard, {})] });
    else if (a === 'settings' && admin)
        page = _jsx(Settings, {});
    else if (a === 'reports' && (sees || m?.role === 'cadmin'))
        page = _jsx(Reports, { staff: sees });
    else if (a === 'connections' && admin)
        page = _jsx(Integrations, {});
    else if (a === 'people' && admin)
        page = _jsx(People, {});
    else if (a === 'all' || !sees)
        page = m?.customer ? _jsx(MyTickets, { me: m }, "mine") : _jsx(List, { me: m }, "all");
    else
        page = _jsx(Dashboard, {});
    const menu = [
        { group: 'Overview', items: [['', 'Dashboard', sees], ['all', m?.customer ? 'My tickets' : 'All tickets', true], ['new', 'New ticket', !!m?.canRaise]] },
        { group: 'Help', items: [['kb', 'Knowledge base', true]] },
        { group: 'Incidents', items: [['incidents', 'Incidents', true], ['major', 'Major incidents', sees]] },
        { group: 'Service requests', items: [['catalog', 'Service catalogue', !!m?.canRaise || staff], ['requests', 'Requests', true], ['approvals', 'Approvals', staff]] },
        { group: 'Problems', items: [['problems', 'Problems', sees], ['known-errors', 'Known errors', sees]] },
        { group: 'Changes', items: [['changes', 'Changes', sees], ['calendar', 'Change calendar', sees], ['standard', 'Standard changes', sees]] },
        { group: 'Reports', items: [['reports', 'Desk reports', sees || m?.role === 'cadmin']] },
        { group: 'Settings', items: [['catalog-items', 'Catalogue items', admin], ['cab', 'Change board', admin], ['connections', 'Connections', admin], ['people', 'People', admin], ['settings', 'Settings', admin]] },
    ];
    const section = isId ? '' : a ?? '';
    return _jsx(_Fragment, { children: _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": "Service Desk", children: [_jsx("div", { className: "modnav-head", children: _jsx("strong", { children: "Service Desk" }) }), menu.map((g) => {
                            const items = g.items.filter(([, , show]) => show);
                            return items.length === 0 ? null : _jsxs("div", { className: "modnav-group", children: [g.group && _jsx("h4", { children: g.group }), items.map(([k, l]) => _jsx("a", { href: '#/servicedesk' + (k ? '/' + k : ''), className: section === k ? 'on' : '', children: l }, k))] }, g.group);
                        })] }), _jsxs("main", { className: "modmain", children: [me.error ? _jsx("p", { role: "alert", className: "err", children: me.error }) : null, m || me.error ? page : _jsx("p", { className: "muted", children: "Loading\u2026" })] })] }) });
}
