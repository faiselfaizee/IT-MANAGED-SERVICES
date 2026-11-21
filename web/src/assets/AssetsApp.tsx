import { useHash, useLoad, type Rec } from '../http';
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
import Packages from './Packages';
import Licenses, { LicenseDetail } from './Licenses';
import { RemoteLog, Repository, Settings } from './Admin';
import { LIFECYCLE_PAGES, useAssetsLite } from './lite';
import { Stock, Types } from './Peripherals';

type Nav = { key: string; label: string };
const MENU: { group: string; items: Nav[] }[] = [
  { group: 'Overview', items: [{ key: 'dashboard', label: 'Dashboard' }] },
  { group: 'Inventory', items: [{ key: 'list', label: 'All assets' }, { key: 'stock', label: 'Stock' }, { key: 'delete-requests', label: 'Delete requests' }] },
  { group: 'Software', items: [{ key: 'software', label: 'Installed software' }, { key: 'repository', label: 'Repository' }, { key: 'licenses', label: 'Licences & subscriptions' }] },
  { group: 'Contracts', items: [{ key: 'amc', label: 'AMC' }, { key: 'rentals', label: 'Rentals' }, { key: 'renewals', label: 'Renewals' }, { key: 'vendors', label: 'Vendors' }] },
  { group: 'Agents', items: [{ key: 'commands', label: 'Command repository' }, { key: 'remote-log', label: 'Remote activity' }] },
  { group: 'Reports', items: [{ key: 'reports', label: 'Asset reports' }] },
  { group: 'Settings', items: [{ key: 'locations', label: 'Locations' }, { key: 'types', label: 'Kinds of asset' }, { key: 'settings', label: 'Settings' }] },
];

/** The Assets module: routes inside the #/assets address. */
export default function AssetsApp() {
  const lite = useAssetsLite();   // Monitoring without Asset management: only the shared device list
  const perms = usePerms('assets'); const amcOk = useAmcAccess(); const parts = useHash(); const [, a, b] = parts;
  // administrators see every page; other people see the pages they may read (a customer administrator sees only assets and companies)
  const approval = useLoad<Rec>('/api/assets/delete-approval');
  const may = (k: string) => (k !== 'delete-requests' || !!approval.data?.required) && (perms.admin || ['dashboard', 'list', 'stock', 'reports', 'companies', 'locations', 'vendors', 'renewals', 'duplicates', 'licenses'].includes(k) || (k === 'delete-requests' && (perms.modify || perms.delete)) || (k === 'repository' && perms.modify) || (perms.readAll && ['software', 'amc', 'rentals'].includes(k)) || (k === 'amc' && amcOk)); const section = /^\d+$/.test(a ?? '') ? 'list' : a === 'customer' ? 'dashboard' : a ?? 'list';
  let page;
  if (/^\d+$/.test(a ?? '')) page = <Detail id={a} tab={b ?? 'overview'} />;
  else if (lite && (a === undefined || a === 'dashboard' || LIFECYCLE_PAGES.includes(a ?? ''))) page = a === undefined ? <List key={location.hash} /> : <p className="muted">This part of Asset management is not in your licence. Customers, logins, agents and the device list are under Administration › Devices setup and Assets › All assets.</p>;
  else switch (a) {
    case 'customer': page = <CustomerDash key={b} id={b ?? 'all'} />; break;
    case 'list': page = <List key={location.hash} />; break;
    case 'reports': page = <Reports key={b ?? ''} name={b} />; break;
    case 'software': page = <Software />; break;
    case 'repository': page = <Packages />; break;
    case 'licenses': page = b ? <LicenseDetail key={b} id={b} /> : <Licenses />; break;
    case 'discover': page = <Discover />; break;
    case 'fleet': page = <Fleet />; break;
    case 'delete-requests': page = <DeleteRequests />; break;
    case 'companies': page = <Companies />; break;
    case 'locations': page = <Locations key={location.hash} />; break;
    case 'vendors': page = <Vendors key={b ?? ''} id={b} />; break;
    case 'credentials': page = <Credentials />; break;
    case 'amc': page = <Contracts kind="amc" />; break;
    case 'rentals': page = <Contracts kind="rentals" />; break;
    case 'renewals': page = <Renewals />; break;
    case 'duplicates': page = <Duplicates />; break;
    case 'remote-log': page = <RemoteLog />; break;
    case 'commands': page = <Repository />; break;
    case 'stock': page = <Stock />; break;
    case 'types': page = <Types />; break;
    case 'settings': page = <Settings />; break;
    case 'dashboard': page = <Dashboard />; break;
    default: page = <List key={location.hash} />;   // the Assets menu opens on All assets
  }
  return <div className="modshell">
    <aside className="modnav" aria-label="Assets">
      <div className="modnav-head"><strong>{lite ? 'Devices' : 'Assets'}</strong></div>
      {MENU.map((g) => { const items = g.items.filter((i) => may(i.key) && !(lite && (LIFECYCLE_PAGES.includes(i.key) || i.key === 'dashboard'))); return items.length === 0 ? null : <div key={g.group} className="modnav-group">
        {g.group && <h4>{g.group}</h4>}
        {items.map((i) => <a key={i.key} href={'#/assets' + (i.key ? '/' + i.key : '')} className={section === i.key ? 'on' : ''}>{i.label}</a>)}</div>; })}
      {perms.admin && <div className="modnav-group"><h4>Setup</h4><a href="#/setup/companies">Customers, logins and agents</a></div>}
    </aside>
    <main className="modmain">{page}</main>
  </div>;
}
