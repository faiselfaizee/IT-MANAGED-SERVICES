import { useHash, useLoad, type Rec } from '../http';
import Dashboard, { CustomerDash } from './Dashboard';
import Devices from './Devices';
import Device from './Device';
import Alerts from './Alerts';
import Rules from './Rules';
import Settings from './Settings';

const NAV: [string, string, boolean][] = [['', 'Dashboard', false], ['devices', 'Devices', false], ['alerts', 'Alerts', false], ['rules', 'Alert rules', false], ['settings', 'Settings', true]];

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
    case 'rules': page = <Rules admin={admin} />; break;
    case 'settings': page = admin ? <Settings /> : <Dashboard />; break;
    default: page = <Dashboard />;
  }
  return <>
    <nav className="sub" aria-label="Monitoring">{NAV.filter(([, , adm]) => !adm || admin).map(([k, l]) => <a key={k} href={'#/monitoring' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</nav>
    {page}
  </>;
}
