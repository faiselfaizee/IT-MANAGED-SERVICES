import { useHash } from './http';
import { useSingleCompany } from './mode';
import Companies from './assets/Companies';
import Credentials from './assets/Credentials';
import Discover from './assets/Discover';
import Duplicates from './assets/Duplicates';
import DirectoryPage from './assets/DirectoryPage';
import Fleet from './assets/Fleet';

const PAGES: [string, string][] = [['companies', 'Customers'], ['credentials', 'Credentials'], ['directory', 'Domain controller'], ['fleet', 'Agents & collectors'], ['discover', 'Discover'], ['duplicates', 'Duplicates']];

/**
 * Administration › Devices setup: the customers, their logins, the agents and site controllers, scans and duplicate check.
 * They are shared by Asset management and Monitoring, so they live here and work with either licence.
 */
export default function Setup() {
  const [, a] = useHash(); const one = useSingleCompany();
  const pages = one ? PAGES.filter(([k]) => k !== 'companies') : PAGES.filter(([k]) => k !== 'directory');   // with several customers the domain controller is set up on each customer   // one company: nobody adds customers
  const page = pages.some(([k]) => k === a) ? (a as string) : pages[0][0];
  return <div className="modshell">
    <aside className="modnav" aria-label="Devices setup">
      <div className="modnav-head"><strong>Devices setup</strong></div>
      <div className="modnav-group">{pages.map(([k, l]) => <a key={k} href={'#/setup/' + k} className={page === k ? 'on' : ''}>{l}</a>)}</div>
    </aside>
    <main className="modmain">
      {page === 'companies' ? <Companies />
        : page === 'directory' ? <DirectoryPage />
        : page === 'credentials' ? <Credentials />
        : page === 'fleet' ? <Fleet />
        : page === 'discover' ? <Discover />
        : <Duplicates />}
    </main>
  </div>;
}
