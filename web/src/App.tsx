import { Suspense, lazy, useEffect, useMemo, useState } from 'react';
import { whoAmI, type WhoAmI } from './api';
import { signOut } from './auth';
import { LOOKS, currentLook, setLook } from './look';
import { Icon, Logo } from './chrome';
import { Ring } from './Rings';
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
const Access = lazy(() => import('./Access'));
const Licence = lazy(() => import('./Licence'));
const Mail = lazy(() => import('./Mail'));
const Updates = lazy(() => import('./Updates'));
const ServiceDeskApp = lazy(() => import('./servicedesk/ServiceDeskApp'));

export function Home({ who, live, assets }: { who: WhoAmI | null; live: boolean; assets: boolean }) {
  const sum = useLoad<Rec>(live && assets ? '/api/assets/summary' : null);
  const s = sum.data;
  const desk = useLoad<Rec>(live ? '/api/servicedesk/summary' : null);
  const d = desk.data;
  const ag = (s?.agents ?? {}) as Rec, co = (s?.compliance ?? {}) as Rec, wa = (s?.warranty ?? {}) as Rec;
  const n = (v: unknown) => Number(v ?? 0);
  const openAll = d ? n(d.open) + n(d.progress) + n(d.hold) : 0;
  return <>
    <header className="hero">
      {who ? <h1>Welcome, {who.name}</h1> : <h1>&nbsp;</h1>}
      <p>Your IT operations in one place.</p>
    </header>
    <div className="tiles">
      {assets && <section className="tile tile-wide" aria-labelledby="t-assets">
        <div className="tile-head"><span className="chip"><Icon name="Assets" size={22} /></span>
          <div><h2 id="t-assets"><a href="#/assets">Assets</a></h2><p className="muted">{s ? `${n(s.total)} assets, ${n(s.total) - n(s.byStatus?.retired) - n(s.byStatus?.lost)} in service` : 'Every device, licence and contract'}</p></div>
          <a className="open" href="#/assets">Open Assets</a></div>
        {s && <div className="rings">
          <Ring title="Agents" unit="devices" empty="No devices in service yet." parts={[
            { key: 'on', label: 'Live', value: n(ag.online), tone: 'ok', href: '#/assets?agent=online' },
            { key: 'due', label: 'Live, update due', value: n(ag.updateDue), tone: 'warn', href: '#/assets?agent=update' },
            { key: 'off', label: 'Not reporting', value: n(ag.offline), tone: 'bad', href: '#/assets?agent=offline' },
            { key: 'none', label: 'No agent', value: n(ag.none), tone: 'none', href: '#/assets?agent=none' }]} />
          <Ring title="Compliance" unit="with agent" empty="No device reports through an agent yet." parts={[
            { key: 'c', label: 'Compliant', value: n(co.compliant), tone: 'ok' },
            { key: 'nc', label: 'Not compliant', value: n(co.nonCompliant), tone: 'bad' },
            { key: 'u', label: 'No data yet', value: n(co.unknown), tone: 'none' }]} />
          <Ring title="Warranty" unit="devices" empty="No devices in service yet." parts={[
            { key: 'ok', label: 'Covered', value: n(wa.covered), tone: 'ok' },
            { key: 'soon', label: 'Ends within 30 days', value: n(wa.endingSoon), tone: 'warn' },
            { key: 'x', label: 'Expired', value: n(wa.expired), tone: 'bad' },
            { key: 'u', label: 'Not recorded', value: n(wa.unknown), tone: 'none' }]} />
        </div>}
        {s && n(co.nonCompliant) > 0 && <p className="muted tile-foot">Compliant means antivirus on and patches from the last 30 days. {n(co.antivirusOff)} with antivirus off, {n(co.patchesOlderThan30Days)} with older patches.</p>}
      </section>}
      <section className="tile tile-wide" aria-labelledby="t-desk">
        <div className="tile-head"><span className="chip"><Icon name="Service Desk" size={22} /></span>
          <div><h2 id="t-desk"><a href="#/servicedesk">Service Desk</a></h2><p className="muted">{d ? `${openAll} open tickets, ${n(d.unassigned)} unassigned` : 'Tickets with service levels and customer sign-in'}</p></div>
          <a className="open" href="#/servicedesk">Open Service Desk</a></div>
        {d && <div className="rings">
          <Ring title="Open tickets" unit="open" empty="No open tickets." parts={[
            { key: 'o', label: 'New', value: n(d.open), tone: 'info', href: '#/servicedesk' },
            { key: 'p', label: 'In progress', value: n(d.progress), tone: 'ok', href: '#/servicedesk' },
            { key: 'h', label: 'Awaiting customer', value: n(d.hold), tone: 'none', href: '#/servicedesk' }]} />
          <Ring title="Service level" unit="open" empty="No open tickets." parts={[
            { key: 'in', label: 'On time', value: Math.max(openAll - n(d.overdue), 0), tone: 'ok' },
            { key: 'late', label: 'Overdue', value: n(d.overdue), tone: 'bad' }]} />
          <Ring title="Closed" unit="tickets" empty="No tickets solved yet." parts={[
            { key: 'r', label: 'Resolved', value: n(d.resolved), tone: 'ok' },
            { key: 'c', label: 'Closed', value: n(d.closed), tone: 'none' }]} />
        </div>}
      </section>
      {MODULES.filter((m) => m !== 'Assets' && m !== 'Service Desk').map((m) => <div key={m} className="tile tile-soon" aria-disabled="true">
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
  const [look, setLookState] = useState(currentLook());
  const parts = useHash();
  useEffect(() => { whoAmI(fetcher).then(setWho, (e: Error) => setErr(e.message)); }, [fetcher]);
  const me = useLoad<Rec>(fetcher ? null : '/api/platform/me');
  const isAdmin = me.data?.role === 'admin';
  // every time DeskAsk shows comes from the server's clock; tell an administrator when it disagrees with this computer's
  const skewMin = useMemo(() => (me.data?.serverTime ? Math.round((new Date(me.data.serverTime as string).getTime() - Date.now()) / 60000) : 0), [me.data]);
  const mine = useLoad<Rec>(fetcher ? null : '/api/platform/access/mine');
  const canAssets = !!fetcher || (!!mine.data?.assets?.read && mine.data.assets.read !== 'none');
  const inAssets = parts[0] === 'assets' && canAssets;
  const inDesk = parts[0] === 'servicedesk';
  const inUsers = parts[0] === 'users' && isAdmin;
  const inAccess = parts[0] === 'access' && isAdmin;
  const inLicence = parts[0] === 'licence' && isAdmin;
  const inMail = parts[0] === 'mail' && isAdmin;
  const inUpdates = parts[0] === 'updates' && isAdmin;
  return (
    <div className="shell">
      <nav aria-label="Modules" className="side">
        <a className="brand" href="#/" aria-label="DeskAsk Platform home"><Logo /><strong>DeskAsk</strong></a>
        <a href="#/" className={!inAssets && !inDesk && !inUsers && !inAccess && !inLicence && !inMail && !inUpdates ? 'on' : ''}><Icon name="home" />Home</a>
        {MODULES.filter((m) => m !== 'Assets' || canAssets).map((m) => m === 'Assets'
          ? <a key={m} href="#/assets" className={inAssets ? 'on' : ''}><Icon name={m} />{m}</a>
          : m === 'Service Desk'
            ? <a key={m} href="#/servicedesk" className={inDesk ? 'on' : ''}><Icon name={m} />{m}</a>
            : <span key={m} title="Not built yet"><Icon name={m} />{m}<em>Soon</em></span>)}
        {isAdmin ? <a href="#/users" className={inUsers ? 'on' : ''}><Icon name="users" />Users</a> : null}
        {isAdmin ? <a href="#/access" className={inAccess ? 'on' : ''}><Icon name="access" />Access</a> : null}
        {isAdmin ? <a href="#/mail" className={inMail ? 'on' : ''}><Icon name="mail" />Mail</a> : null}
        {isAdmin ? <a href="#/updates" className={inUpdates ? 'on' : ''}><Icon name="access" />Updates</a> : null}
        {isAdmin ? <a href="#/licence" className={inLicence ? 'on' : ''}><Icon name="access" />Licence</a> : null}
        <label className="look-pick">Appearance<select value={look} onChange={(e) => { setLook(e.target.value); setLookState(e.target.value); }}>{LOOKS.map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
        {fetcher ? null : <a href="/account/security?returnUrl=%2F"><Icon name="access" />Two-step sign-in</a>}
        {fetcher ? null : <a className="out" href="#/" onClick={(e) => { e.preventDefault(); signOut(); }}><Icon name="signout" />Sign out</a>}
      </nav>
      <main className="main">
        {err ? <p role="alert" className="err">{err}</p> : null}
        {isAdmin && Math.abs(skewMin) >= 3 ? <p role="alert" className="err">The server's clock is about {Math.floor(Math.abs(skewMin) / 60)} h {Math.abs(skewMin) % 60} min {skewMin > 0 ? 'ahead of' : 'behind'} this computer, so times shown here and written to logs are wrong. On the server, set the correct time zone first (Windows: <code>Set-TimeZone -Id "India Standard Time"</code> for India), then correct the time (<code>w32tm /resync</code>).</p> : null}
        {inAssets
          ? <Suspense fallback={<p className="muted">Loading…</p>}><AssetsApp /></Suspense>
          : inDesk ? <Suspense fallback={<p className="muted">Loading…</p>}><ServiceDeskApp /></Suspense>
          : inUpdates ? <Suspense fallback={<p className="muted">Loading…</p>}><Updates /></Suspense>
          : inMail ? <Suspense fallback={<p className="muted">Loading…</p>}><Mail /></Suspense>
          : inLicence ? <Suspense fallback={<p className="muted">Loading…</p>}><Licence /></Suspense>
          : inAccess ? <Suspense fallback={<p className="muted">Loading…</p>}><Access /></Suspense>
          : inUsers ? <Suspense fallback={<p className="muted">Loading…</p>}><Users meId={me.data?.sub} /></Suspense>
          : !err ? <Home who={who} live={!fetcher} assets={canAssets} /> : null}
      </main>
    </div>
  );
}
