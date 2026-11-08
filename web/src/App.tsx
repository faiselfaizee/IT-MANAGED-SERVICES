import { useEffect, useState } from 'react';
import { whoAmI, type WhoAmI } from './api';

export const MODULES = ['Service Desk', 'Assets', 'Monitoring', 'AD Manager', 'Vault', 'Backup'];

export default function App({ fetcher }: { fetcher?: typeof fetch }) {
  const [who, setWho] = useState<WhoAmI | null>(null);
  const [err, setErr] = useState('');
  useEffect(() => { whoAmI(fetcher).then(setWho, (e: Error) => setErr(e.message)); }, [fetcher]);
  return (
    <div style={{ fontFamily: 'system-ui, sans-serif', display: 'flex', minHeight: '100vh' }}>
      <nav aria-label="Modules" style={{ width: 220, padding: 16, background: '#14213d', color: '#fff' }}>
        <strong>DeskAsk Platform</strong>
        <ul style={{ listStyle: 'none', padding: 0 }}>{MODULES.map((m) => <li key={m} style={{ padding: '6px 0', opacity: 0.6 }}>{m}</li>)}</ul>
      </nav>
      <main style={{ flex: 1, padding: 24 }}>
        {err ? <p role="alert">{err}</p> : who ? <h1>Welcome, {who.name}</h1> : <p>Loading…</p>}
        <p>Phase 0 shell. Modules appear here as they are built.</p>
      </main>
    </div>
  );
}
