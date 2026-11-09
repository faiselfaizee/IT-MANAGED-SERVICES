import { Suspense, lazy, useEffect, useState } from 'react';
import { whoAmI, type WhoAmI } from './api';
import { signOut } from './auth';
import { useHash } from './http';

export const MODULES = ['Service Desk', 'Assets', 'Monitoring', 'AD Manager', 'Vault', 'Backup'];
const AssetsApp = lazy(() => import('./assets/AssetsApp'));

export default function App({ fetcher }: { fetcher?: typeof fetch }) {
  const [who, setWho] = useState<WhoAmI | null>(null);
  const [err, setErr] = useState('');
  const parts = useHash();
  useEffect(() => { whoAmI(fetcher).then(setWho, (e: Error) => setErr(e.message)); }, [fetcher]);
  const inAssets = parts[0] === 'assets';
  return (
    <div className="shell">
      <nav aria-label="Modules" className="side">
        <strong>DeskAsk Platform</strong>
        <a href="#/" className={!inAssets ? 'on' : ''}>Home</a>
        {MODULES.map((m) => m === 'Assets' ? <a key={m} href="#/assets" className={inAssets ? 'on' : ''}>{m}</a> : <span key={m} title="Not built yet">{m}</span>)}
        {fetcher ? null : <a href="#/" onClick={(e) => { e.preventDefault(); signOut(); }}>Sign out</a>}
      </nav>
      <main className="main">
        {err ? <p role="alert">{err}</p> : null}
        {inAssets
          ? <Suspense fallback={<p>Loading…</p>}><AssetsApp /></Suspense>
          : <>{who ? <h1>Welcome, {who.name}</h1> : !err ? <p>Loading…</p> : null}<p>Choose a module on the left. Modules appear here as they are built.</p></>}
      </main>
    </div>
  );
}
