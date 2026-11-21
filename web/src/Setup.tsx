import { useHash } from './http';
import { useSingleCompany } from './mode';
import Companies from './assets/Companies';
import Credentials from './assets/Credentials';
import Discover from './assets/Discover';
import Duplicates from './assets/Duplicates';
import CompanyPage from './assets/CompanyPage';
import Fleet from './assets/Fleet';

const PAGES: [string, string][] = [['companies', 'Customers'], ['company', 'Our organisation'], ['credentials', 'Credentials'], ['fleet', 'Agents & collectors'], ['discover', 'Discover'], ['duplicates', 'Duplicates']];

/**
 * Administration › Devices setup: the customers, their logins, the agents and site controllers, scans and duplicate check.
 * They are shared by Asset management and Monitoring, so they live here and work with either licence.
 */
export default function Setup() {
  const [, a, sub] = useHash(); const one = useSingleCompany();
  // one company: it is "Our organisation" (its users, domain controller, details); with several, each customer has the same page under Customers
  const pages = one ? PAGES.filter(([k]) => k !== 'companies') : PAGES.filter(([k]) => k !== 'company');   // one company: nobody adds customers
  const page = pages.some(([k]) => k === a) ? (a as string) : pages[0][0];
  return <div className="modshell">
    <aside className="modnav" aria-label="Devices setup">
      <div className="modnav-head"><strong>Devices setup</strong></div>
      <div className="modnav-group">{pages.map(([k, l]) => <a key={k} href={'#/setup/' + k} className={page === k ? 'on' : ''}>{l}</a>)}</div>
    </aside>
    <main className="modmain">
      {page === 'companies' ? (sub ? <CompanyPage /> : <Companies />)
        : page === 'company' ? <CompanyPage />
        : page === 'credentials' ? <Credentials />
        : page === 'fleet' ? <Fleet />
        : page === 'discover' ? <Discover />
        : <Duplicates />}
    </main>
  </div>;
}
