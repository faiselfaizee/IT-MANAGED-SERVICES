import { useHash } from './http';
import Companies from './assets/Companies';
import Credentials from './assets/Credentials';
import Discover from './assets/Discover';
import Duplicates from './assets/Duplicates';
import Fleet from './assets/Fleet';

const PAGES: [string, string][] = [['companies', 'Customers'], ['credentials', 'Credentials'], ['fleet', 'Agents & collectors'], ['discover', 'Discover'], ['duplicates', 'Duplicates']];

/**
 * Administration › Devices setup: the customers, their logins, the agents and site controllers, scans and duplicate check.
 * They are shared by Asset management and Monitoring, so they live here and work with either licence.
 */
export default function Setup() {
  const [, a] = useHash();
  const page = PAGES.some(([k]) => k === a) ? (a as string) : 'companies';
  return <div className="modshell">
    <aside className="modnav" aria-label="Devices setup">
      <div className="modnav-head"><strong>Devices setup</strong></div>
      <div className="modnav-group">{PAGES.map(([k, l]) => <a key={k} href={'#/setup/' + k} className={page === k ? 'on' : ''}>{l}</a>)}</div>
    </aside>
    <main className="modmain">
      {page === 'companies' ? <Companies />
        : page === 'credentials' ? <Credentials />
        : page === 'fleet' ? <Fleet />
        : page === 'discover' ? <Discover />
        : <Duplicates />}
    </main>
  </div>;
}
