import { useHash, useLoad, type Rec } from '../http';
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
  const me = useLoad<Rec>('/api/platform/me');
  const admin = me.data?.role === 'admin';
  const section = a === 'customer' ? '' : a === 'device' ? 'devices' : a ?? '';
  let page;
  switch (a) {
    case 'customer': page = <CustomerDash key={b} id={b ?? 'all'} />; break;
    case 'devices': page = <Devices key={location.hash} />; break;
    case 'device': page = <Device key={b} id={b ?? ''} />; break;
    case 'alerts': page = <Alerts key={location.hash} />; break;
    case 'events': page = <Events key={location.hash} />; break;
    case 'reports': page = <Reports key={b ?? ''} name={b?.split('?')[0]} />; break;
    case 'rules': page = <Rules admin={admin} />; break;
    case 'settings': page = admin ? <Settings /> : <Dashboard />; break;
    default: page = <Dashboard />;
  }
  const groups = [
    { group: 'Overview', items: [{ key: '', label: 'Dashboard' }] },
    { group: 'Watch', items: [{ key: 'devices', label: 'Devices' }, { key: 'alerts', label: 'Alerts' }, { key: 'events', label: 'Messages' }] },
    { group: 'Reports', items: [{ key: 'reports', label: 'Monitoring reports' }] },
    { group: 'Settings', items: [{ key: 'rules', label: 'Alert rules' }, ...(admin ? [{ key: 'settings', label: 'Settings' }] : [])] },
  ];
  return <ModNav title="Monitoring" base="monitoring" groups={groups} section={section}>{page}</ModNav>;
}
