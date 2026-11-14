import { useHash } from '../http';
import { usePerms } from '../perms';
import Companies from './Companies';
import Contracts from './Contracts';
import Renewals from './Renewals';
import DeleteRequests from './DeleteRequests';
import Credentials from './Credentials';
import Detail from './Detail';
import Discover from './Discover';
import Fleet from './Fleet';
import List from './List';
import Dashboard, { CustomerDash } from './Dashboard';
import Reports from './Reports';
import Software from './Software';
import { RemoteLog, Repository, Settings } from './Admin';

const NAV: [string, string][] = [
  ['', 'Dashboard'], ['list', 'All assets'], ['reports', 'Patch report'], ['software', 'Software'], ['discover', 'Discover'], ['fleet', 'Agents & collectors'], ['delete-requests', 'Delete requests'], ['companies', 'Companies'], ['credentials', 'Credentials'],
  ['amc', 'AMC'], ['rentals', 'Rentals'], ['renewals', 'Renewals'], ['remote-log', 'Remote activity'], ['commands', 'Command repository'], ['settings', 'Settings'],
];

/** The Assets module: routes inside the #/assets address. */
export default function AssetsApp() {
  const perms = usePerms('assets'); const parts = useHash(); const [, a, b] = parts;
  // administrators see every page; other people see the pages they may read (a customer administrator sees only assets and companies)
  const nav = NAV.filter(([k]) => perms.admin || ['', 'list', 'reports', 'companies'].includes(k) || (k === 'delete-requests' && (perms.modify || perms.delete)) || (perms.readAll && ['software', 'amc', 'rentals'].includes(k))); const section = /^\d+$/.test(a ?? '') ? 'list' : a === 'customer' ? '' : a ?? '';
  let page;
  if (/^\d+$/.test(a ?? '')) page = <Detail id={a} tab={b ?? 'overview'} />;
  else switch (a) {
    case 'customer': page = <CustomerDash key={b} id={b ?? 'all'} />; break;
    case 'list': page = <List key={location.hash} />; break;
    case 'reports': page = <Reports />; break;
    case 'software': page = <Software />; break;
    case 'discover': page = <Discover />; break;
    case 'fleet': page = <Fleet />; break;
    case 'delete-requests': page = <DeleteRequests />; break;
    case 'companies': page = <Companies />; break;
    case 'credentials': page = <Credentials />; break;
    case 'amc': page = <Contracts kind="amc" />; break;
    case 'rentals': page = <Contracts kind="rentals" />; break;
    case 'renewals': page = <Renewals />; break;
    case 'remote-log': page = <RemoteLog />; break;
    case 'commands': page = <Repository />; break;
    case 'settings': page = <Settings />; break;
    default: page = <Dashboard />;
  }
  return <>
    <nav className="sub" aria-label="Assets">{nav.map(([k, l]) => <a key={k} href={'#/assets' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</nav>
    {page}
  </>;
}
