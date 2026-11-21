import { jsx as _jsx } from "react/jsx-runtime";
import { useHash, useLoad } from '../http';
import Dashboard, { CustomerDash } from './Dashboard';
import Devices from './Devices';
import Device from './Device';
import Alerts from './Alerts';
import Rules from './Rules';
import Settings from './Settings';
import Events from './Events';
import Reports from './Reports';
import { ModNav } from '../ModNav';
/** The Monitoring module: routes inside the #/monitoring address. */
export default function MonitoringApp() {
    const [, a, b] = useHash();
    const me = useLoad('/api/platform/me');
    const admin = me.data?.role === 'admin';
    const section = a === 'customer' ? '' : a === 'device' ? 'devices' : a ?? '';
    let page;
    switch (a) {
        case 'customer':
            page = _jsx(CustomerDash, { id: b ?? 'all' }, b);
            break;
        case 'devices':
            page = _jsx(Devices, {}, location.hash);
            break;
        case 'device':
            page = _jsx(Device, { id: b ?? '' }, b);
            break;
        case 'alerts':
            page = _jsx(Alerts, {}, location.hash);
            break;
        case 'events':
            page = _jsx(Events, {}, location.hash);
            break;
        case 'reports':
            page = _jsx(Reports, { name: b?.split('?')[0] }, b ?? '');
            break;
        case 'rules':
            page = _jsx(Rules, { admin: admin });
            break;
        case 'settings':
            page = admin ? _jsx(Settings, {}) : _jsx(Dashboard, {});
            break;
        default: page = _jsx(Dashboard, {});
    }
    const groups = [
        { group: 'Overview', items: [{ key: '', label: 'Dashboard' }] },
        { group: 'Watch', items: [{ key: 'devices', label: 'Devices' }, { key: 'alerts', label: 'Alerts' }, { key: 'events', label: 'Messages' }] },
        { group: 'Reports', items: [{ key: 'reports', label: 'Monitoring reports' }] },
        { group: 'Settings', items: [{ key: 'rules', label: 'Alert rules' }, ...(admin ? [{ key: 'settings', label: 'Settings' }] : [])] },
    ];
    return _jsx(ModNav, { title: "Monitoring", base: "monitoring", groups: groups, section: section, children: page });
}
