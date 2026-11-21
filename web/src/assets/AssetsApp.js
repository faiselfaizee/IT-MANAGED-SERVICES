import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useHash, useLoad } from '../http';
import { usePerms } from '../perms';
import Companies from './Companies';
import Locations from './Locations';
import Vendors from './Vendors';
import Contracts, { useAmcAccess } from './Contracts';
import Renewals from './Renewals';
import Duplicates from './Duplicates';
import DeleteRequests from './DeleteRequests';
import Credentials from './Credentials';
import Detail from './Detail';
import Discover from './Discover';
import Fleet from './Fleet';
import List from './List';
import Dashboard, { CustomerDash } from './Dashboard';
import Reports from './Reports';
import Software from './Software';
import Licenses, { LicenseDetail } from './Licenses';
import { RemoteLog, Repository, Settings } from './Admin';
import { LIFECYCLE_PAGES, useAssetsLite } from './lite';
import { People, Stock, Types } from './Peripherals';
const MENU = [
    { group: 'Overview', items: [{ key: 'dashboard', label: 'Dashboard' }] },
    { group: 'Inventory', items: [{ key: 'list', label: 'All assets' }, { key: 'people', label: 'People' }, { key: 'stock', label: 'Stock' }, { key: 'delete-requests', label: 'Delete requests' }] },
    { group: 'Software', items: [{ key: 'software', label: 'Installed software' }, { key: 'licenses', label: 'Licences & subscriptions' }] },
    { group: 'Contracts', items: [{ key: 'amc', label: 'AMC' }, { key: 'rentals', label: 'Rentals' }, { key: 'renewals', label: 'Renewals' }, { key: 'vendors', label: 'Vendors' }] },
    { group: 'Agents', items: [{ key: 'commands', label: 'Command repository' }, { key: 'remote-log', label: 'Remote activity' }] },
    { group: 'Reports', items: [{ key: 'reports', label: 'Asset reports' }] },
    { group: 'Settings', items: [{ key: 'locations', label: 'Locations' }, { key: 'types', label: 'Kinds of asset' }, { key: 'settings', label: 'Settings' }] },
];
/** The Assets module: routes inside the #/assets address. */
export default function AssetsApp() {
    const lite = useAssetsLite(); // Monitoring without Asset management: only the shared device list
    const perms = usePerms('assets');
    const amcOk = useAmcAccess();
    const parts = useHash();
    const [, a, b] = parts;
    // administrators see every page; other people see the pages they may read (a customer administrator sees only assets and companies)
    const approval = useLoad('/api/assets/delete-approval');
    const may = (k) => (k !== 'delete-requests' || !!approval.data?.required) && (perms.admin || ['dashboard', 'list', 'people', 'stock', 'reports', 'companies', 'locations', 'vendors', 'renewals', 'duplicates', 'licenses'].includes(k) || (k === 'delete-requests' && (perms.modify || perms.delete)) || (perms.readAll && ['software', 'amc', 'rentals'].includes(k)) || (k === 'amc' && amcOk));
    const section = /^\d+$/.test(a ?? '') ? 'list' : a === 'customer' ? 'dashboard' : a ?? 'list';
    let page;
    if (/^\d+$/.test(a ?? ''))
        page = _jsx(Detail, { id: a, tab: b ?? 'overview' });
    else if (lite && (a === undefined || a === 'dashboard' || LIFECYCLE_PAGES.includes(a ?? '')))
        page = a === undefined ? _jsx(List, {}, location.hash) : _jsx("p", { className: "muted", children: "This part of Asset management is not in your licence. Customers, logins, agents and the device list are under Administration \u203A Devices setup and Assets \u203A All assets." });
    else
        switch (a) {
            case 'customer':
                page = _jsx(CustomerDash, { id: b ?? 'all' }, b);
                break;
            case 'list':
                page = _jsx(List, {}, location.hash);
                break;
            case 'reports':
                page = _jsx(Reports, { name: b }, b ?? '');
                break;
            case 'software':
                page = _jsx(Software, {});
                break;
            case 'licenses':
                page = b ? _jsx(LicenseDetail, { id: b }, b) : _jsx(Licenses, {});
                break;
            case 'discover':
                page = _jsx(Discover, {});
                break;
            case 'fleet':
                page = _jsx(Fleet, {});
                break;
            case 'delete-requests':
                page = _jsx(DeleteRequests, {});
                break;
            case 'companies':
                page = _jsx(Companies, {});
                break;
            case 'locations':
                page = _jsx(Locations, {}, location.hash);
                break;
            case 'vendors':
                page = _jsx(Vendors, { id: b }, b ?? '');
                break;
            case 'credentials':
                page = _jsx(Credentials, {});
                break;
            case 'amc':
                page = _jsx(Contracts, { kind: "amc" });
                break;
            case 'rentals':
                page = _jsx(Contracts, { kind: "rentals" });
                break;
            case 'renewals':
                page = _jsx(Renewals, {});
                break;
            case 'duplicates':
                page = _jsx(Duplicates, {});
                break;
            case 'remote-log':
                page = _jsx(RemoteLog, {});
                break;
            case 'commands':
                page = _jsx(Repository, {});
                break;
            case 'people':
                page = _jsx(People, {});
                break;
            case 'stock':
                page = _jsx(Stock, {});
                break;
            case 'types':
                page = _jsx(Types, {});
                break;
            case 'settings':
                page = _jsx(Settings, {});
                break;
            case 'dashboard':
                page = _jsx(Dashboard, {});
                break;
            default: page = _jsx(List, {}, location.hash); // the Assets menu opens on All assets
        }
    return _jsxs("div", { className: "modshell", children: [_jsxs("aside", { className: "modnav", "aria-label": "Assets", children: [_jsx("div", { className: "modnav-head", children: _jsx("strong", { children: lite ? 'Devices' : 'Assets' }) }), MENU.map((g) => {
                        const items = g.items.filter((i) => may(i.key) && !(lite && (LIFECYCLE_PAGES.includes(i.key) || i.key === 'dashboard')));
                        return items.length === 0 ? null : _jsxs("div", { className: "modnav-group", children: [g.group && _jsx("h4", { children: g.group }), items.map((i) => _jsx("a", { href: '#/assets' + (i.key ? '/' + i.key : ''), className: section === i.key ? 'on' : '', children: i.label }, i.key))] }, g.group);
                    }), perms.admin && _jsxs("div", { className: "modnav-group", children: [_jsx("h4", { children: "Setup" }), _jsx("a", { href: "#/setup/companies", children: "Customers, logins and agents" })] })] }), _jsx("main", { className: "modmain", children: page })] });
}
