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
import Licenses, { LicenseDetail } from './Licenses';
import { RemoteLog, Repository, Settings } from './Admin';

type Nav = { key: string; label: string };
const MENU: { group: string; items: Nav[] }[] = [
  { group: '', items: [{ key: '', label: 'Dashboard' }] },
  { group: 'Inventory', items: [{ key: 'list', label: 'All assets' }, { key: 'discover', label: 'Discover' }, { key: 'duplicates', label: 'Duplicates' }, { key: 'delete-requests', label: 'Delete requests' }] },
  { group: 'Software', items: [{ key: 'licenses', label: 'Licences & subscriptions' }, { key: 'software', label: 'Installed software' }, { key: 'reports', label: 'Patch report' }] },
  { group: 'Agents', items: [{ key: 'fleet', label: 'Agents & collectors' }, { key: 'commands', label: 'Command repository' }, { key: 'remote-log', label: 'Remote activity' }] },
  { group: 'Contracts', items: [{ key: 'amc', label: 'AMC' }, { key: 'rentals', label: 'Rentals' }, { key: 'renewals', label: 'Renewals' }, { key: 'vendors', label: 'Vendors' }] },
  { group: 'Organisation', items: [{ key: 'companies', label: 'Companies' }, { key: 'locations', label: 'Locations' }, { key: 'credentials', label: 'Credentials' }] },
  { group: 'Admin', items: [{ key: 'settings', label: 'Settings' }] },
];

/** The Assets module: routes inside the #/assets address. */
export default function AssetsApp() {
  const perms = usePerms('assets'); const amcOk = useAmcAccess(); const parts = useHash(); const [, a, b] = parts;
  // administrators see every page; other people see the pages they may read (a customer administrator sees only assets and companies)
  const approval = useLoad<Rec>('/api/assets/delete-approval');
  const may = (k: string) => (k !== 'delete-requests' || !!approval.data?.required) && (perms.admin || ['', 'list', 'reports', 'companies', 'locations', 'vendors', 'renewals', 'duplicates', 'licenses'].includes(k) || (k === 'delete-requests' && (perms.modify || perms.delete)) || (perms.readAll && ['software', 'amc', 'rentals'].includes(k)) || (k === 'amc' && amcOk)); const section = /^\d+$/.test(a ?? '') ? 'list' : a === 'customer' ? '' : a ?? '';
  let page;
  if (/^\d+$/.test(a ?? '')) page = <Detail id={a} tab={b ?? 'overview'} />;
  else switch (a) {
    case 'customer': page = <CustomerDash key={b} id={b ?? 'all'} />; break;
    case 'list': page = <List key={location.hash} />; break;
    case 'reports': page = <Reports />; break;
    case 'software': page = <Software />; break;
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
    case 'settings': page = <Settings />; break;
    default: page = <Dashboard />;
  }
  return <div className="modshell">
    <aside className="modnav" aria-label="Assets">
      <div className="modnav-head"><strong>Assets</strong></div>
      {MENU.map((g) => { const items = g.items.filter((i) => may(i.key)); return items.length === 0 ? null : <div key={g.group} className="modnav-group">
        {g.group && <h4>{g.group}</h4>}
        {items.map((i) => <a key={i.key} href={'#/assets' + (i.key ? '/' + i.key : '')} className={section === i.key ? 'on' : ''}>{i.label}</a>)}</div>; })}
    </aside>
    <main className="modmain">{page}</main>
  </div>;
}
