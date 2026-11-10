import { Suspense, lazy, useEffect, useState } from 'react';
import { whoAmI, type WhoAmI } from './api';
import { signOut } from './auth';
import { Icon, Logo } from './chrome';
import { useHash, useLoad, type Rec } from './http';

export const MODULES = ['Service Desk', 'Assets', 'Monitoring', 'AD Manager', 'Vault', 'Backup'];
const ABOUT: Record<string, string> = {
  'Service Desk': 'Tickets, requests and SLAs',
  Monitoring: 'Uptime and service checks',
  'AD Manager': 'Users, groups and password resets',
  Vault: 'Shared secrets and credentials',
  Backup: 'Jobs, schedules and restores',
};
const AssetsApp = lazy(() => import('./assets/AssetsApp'));
const Users = lazy(() => import('./Users'));

function Home({ who, live }: { who: WhoAmI | null; live: boolean }) {
  const sum = useLoad<Rec>(live ? '/api/assets/summary' : null);
  const s = sum.data;
  return <>
    <header className="hero">
      {who ? <h1>Welcome, {who.name}</h1> : <h1>&nbsp;</h1>}
      <p>Your IT operations in one place.</p>
    </header>
    <div className="tiles">
      <a className="tile tile-main" href="#/assets">
        <span className="chip"><Icon name="Assets" size={22} /></span>
        <h2>Assets</h2>
        <p className="muted">Every device, licence and contract, with QR labels, agents and discovery.</p>
        {s ? <dl className="stats">
          <div><dt>Assets</dt><dd>{s.total ?? 0}</dd></div>
          <div><dt>In use</dt><dd>{s.byStatus?.in_use ?? 0}</dd></div>
          <div><dt>Warranty ends within 30 days</dt><dd>{s.warrantyEndsIn30Days ?? 0}</dd></div>
        </dl> : null}
        <span className="open">Open Assets</span>
      </a>
      {MODULES.filter((m) => m !== 'Assets').map((m) => <div key={m} className="tile tile-soon" aria-disabled="true">
        <span className="chip"><Icon name={m} size={22} /></span>
        <h2>{m}</h2>
        <p className="muted">{ABOUT[m]}</p>
        <span className="soon">Planned</span>
      </div>)}
    </div>
  </>;
}

export default function App({ fetcher }: { fetcher?: typeof fetch }) {
  const [who, setWho] = useState<WhoAmI | null>(null);
  const [err, setErr] = useState('');
  const parts = useHash();
  useEffect(() => { whoAmI(fetcher).then(setWho, (e: Error) => setErr(e.message)); }, [fetcher]);
  const me = useLoad<Rec>(fetcher ? null : '/api/platform/me');
  const isAdmin = me.data?.role === 'admin';
  const inAssets = parts[0] === 'assets';
  const inUsers = parts[0] === 'users' && isAdmin;
  return (
    <div className="shell">
      <nav aria-label="Modules" className="side">
        <a className="brand" href="#/" aria-label="DeskAsk Platform home"><Logo /><strong>DeskAsk</strong></a>
        <a href="#/" className={!inAssets && !inUsers ? 'on' : ''}><Icon name="home" />Home</a>
        {MODULES.map((m) => m === 'Assets'
          ? <a key={m} href="#/assets" className={inAssets ? 'on' : ''}><Icon name={m} />{m}</a>
          : <span key={m} title="Not built yet"><Icon name={m} />{m}<em>Soon</em></span>)}
        {isAdmin ? <a href="#/users" className={inUsers ? 'on' : ''}><Icon name="users" />Users</a> : null}
        {fetcher ? null : <a className="out" href="#/" onClick={(e) => { e.preventDefault(); signOut(); }}><Icon name="signout" />Sign out</a>}
      </nav>
      <main className="main">
        {err ? <p role="alert" className="err">{err}</p> : null}
        {inAssets
          ? <Suspense fallback={<p className="muted">Loading…</p>}><AssetsApp /></Suspense>
          : inUsers ? <Suspense fallback={<p className="muted">Loading…</p>}><Users meId={me.data?.sub} /></Suspense>
          : !err ? <Home who={who} live={!fetcher} /> : null}
      </main>
    </div>
  );
}
