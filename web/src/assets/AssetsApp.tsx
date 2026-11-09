import { useHash } from '../http';
import Companies from './Companies';
import Contracts from './Contracts';
import Detail from './Detail';
import Discover from './Discover';
import Fleet from './Fleet';
import List from './List';
import Software from './Software';
import { RemoteLog, Repository, Settings } from './Admin';

const NAV: [string, string][] = [
  ['', 'Assets'], ['software', 'Software'], ['discover', 'Discover'], ['fleet', 'Agents & collectors'], ['companies', 'Companies'],
  ['amc', 'AMC'], ['rentals', 'Rentals'], ['remote-log', 'Remote activity'], ['commands', 'Command repository'], ['settings', 'Settings'],
];

/** The Assets module: routes inside the #/assets address. */
export default function AssetsApp() {
  const parts = useHash(); const [, a, b] = parts; const section = /^\d+$/.test(a ?? '') ? '' : a ?? '';
  let page;
  if (/^\d+$/.test(a ?? '')) page = <Detail id={a} tab={b ?? 'overview'} />;
  else switch (a) {
    case 'software': page = <Software />; break;
    case 'discover': page = <Discover />; break;
    case 'fleet': page = <Fleet />; break;
    case 'companies': page = <Companies />; break;
    case 'amc': page = <Contracts kind="amc" />; break;
    case 'rentals': page = <Contracts kind="rentals" />; break;
    case 'remote-log': page = <RemoteLog />; break;
    case 'commands': page = <Repository />; break;
    case 'settings': page = <Settings />; break;
    default: page = <List />;
  }
  return <>
    <nav className="sub" aria-label="Assets">{NAV.map(([k, l]) => <a key={k} href={'#/assets' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</nav>
    {page}
  </>;
}
