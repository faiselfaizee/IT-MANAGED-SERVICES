import { Suspense, lazy, useEffect, useMemo, useState, type ReactNode } from 'react';
import { whoAmI, type WhoAmI } from './api';
import { signOut } from './auth';
import { LOOKS, currentLook, setLook } from './look';
import { Icon, Logo } from './chrome';
import { logoUrl, useBrand } from './brand';
import { Ring } from './Rings';
import { useSingleCompany } from './mode';
import { setupPages } from './setupPages';
import { useHash, useLoad, type Rec } from './http';

export const MODULES = ['Service Desk', 'Assets', 'Monitoring', 'AD Manager', 'Vault', 'Backup'];
/** Modules that are announced but not built. A built module that the licence does not include is simply not shown. */
const PLANNED: string[] = [];
const ABOUT: Record<string, string> = {
  'Service Desk': 'Tickets, requests and SLAs',
  Monitoring: 'Uptime and service checks',
  'AD Manager': 'Users, groups and password resets',
  Vault: 'Shared secrets and credentials',
  Backup: 'Results of the backup software customers already run',
};
const AssetsApp = lazy(() => import('./assets/AssetsApp'));
const Assistant = lazy(() => import('./Assistant'));
const Users = lazy(() => import('./Users'));
const Access = lazy(() => import('./Access'));
const Licence = lazy(() => import('./Licence'));
const Branding = lazy(() => import('./Branding'));
const Subscription = lazy(() => import('./Subscription'));
const Mail = lazy(() => import('./Mail'));
const Updates = lazy(() => import('./Updates'));
const VendorApp = lazy(() => import('./vendor/VendorApp'));
const SetupApp = lazy(() => import('./Setup'));
const ServiceDeskApp = lazy(() => import('./servicedesk/ServiceDeskApp'));
const MonitoringApp = lazy(() => import('./monitoring/MonitoringApp'));
const VaultApp = lazy(() => import('./vault/VaultApp'));
const AdApp = lazy(() => import('./admanager/AdApp'));
const BackupApp = lazy(() => import('./backup/BackupApp'));

/** Users, security groups and module permissions on one page. */
function UsersArea({ meId, tab }: { meId?: string; tab: string }) {
  const t = tab === 'groups' || tab === 'permissions' ? tab : 'people';
  return <>
    <h1>Users</h1>
    <nav className="sub" aria-label="Users">
      <a href="#/users" className={t === 'people' ? 'on' : ''}>Users</a>
      <a href="#/users/groups" className={t === 'groups' ? 'on' : ''}>Groups</a>
      <a href="#/users/permissions" className={t === 'permissions' ? 'on' : ''}>Permissions</a>
    </nav>
    {t === 'people' ? <Users meId={meId} embedded /> : <Access tab={t} />}
  </>;
}

const ROLE: Record<string, string> = { admin: 'Administrator', engineer: 'Engineer', viewer: 'Viewer', cadmin: 'Customer administrator', customer: 'Customer' };

/** The signed-in person: name and role, appearance, two-step sign-in and signing out. */
function UserMenu({ name, role, look, onLook, live }: { name: string; role: string; look: string; onLook: (v: string) => void; live: boolean }) {
  const [open, setOpen] = useState(false);
  const initials = name.split(/[\s@.]+/).filter(Boolean).slice(0, 2).map((x) => x[0]?.toUpperCase()).join('') || '?';
  return <div className="usermenu" onBlur={(e) => { if (!e.currentTarget.contains(e.relatedTarget as Node)) setOpen(false); }}>
    <button className="userbtn" aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen(!open)}>
      <span className="avatar" aria-hidden>{initials}</span><span className="who"><strong>{name || 'Signed in'}</strong><small>{ROLE[role] ?? role}</small></span><i aria-hidden>▾</i></button>
    <div className={'menu' + (open ? ' open' : '')} role="menu">
      <label className="look-pick">Appearance<select value={look} onChange={(e) => onLook(e.target.value)}>{LOOKS.map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
      {live ? <a role="menuitem" href="/account/security?returnUrl=%2F"><Icon name="access" />Two-step sign-in</a> : null}
      {live ? <a role="menuitem" className="out" href="#/" onClick={(e) => { e.preventDefault(); signOut(); }}><Icon name="signout" />Sign out</a> : null}
    </div>
  </div>;
}

export function Home({ who, live, assets, monitoring = false, vault = false, adm = false, backup = false, customer = false, deskOn = true }: { who: WhoAmI | null; live: boolean; assets: boolean; monitoring?: boolean; vault?: boolean; adm?: boolean; backup?: boolean; deskOn?: boolean; customer?: boolean }) {
  const bk = useLoad<Rec>(live && backup ? '/api/backup/dashboard' : null);
  const b = (bk.data?.all ?? null) as Rec | null;
  const mon = useLoad<Rec>(live && monitoring ? '/api/monitoring/dashboard' : null);
  const mo = (mon.data?.all ?? null) as Rec | null;
  const sum = useLoad<Rec>(live && assets ? '/api/assets/summary' : null);
  const s = sum.data;
  const desk = useLoad<Rec>(live && deskOn ? '/api/servicedesk/summary' : null);
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
      {deskOn && <section className="tile tile-wide" aria-labelledby="t-desk">
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
      </section>}
      {assets && <section className="tile tile-wide" aria-labelledby="t-assets">
        <div className="tile-head"><span className="chip"><Icon name="Assets" size={22} /></span>
          <div><h2 id="t-assets"><a href="#/assets">Assets</a></h2><p className="muted">{s ? `${n(s.total)} assets, ${n(s.total) - n(s.byStatus?.retired) - n(s.byStatus?.lost)} in service` : 'Every device, licence and contract'}</p></div>
          <a className="open" href="#/assets">Open Assets</a></div>
        {s && <div className="rings">
          <Ring title="Agents" unit="devices" empty="No devices in service yet." parts={[
            { key: 'on', label: 'Live', value: n(ag.online), tone: 'ok', href: '#/assets/list?agent=online' },
            { key: 'due', label: 'Live, update due', value: n(ag.updateDue), tone: 'warn', href: '#/assets/list?agent=update' },
            { key: 'off', label: 'Not reporting', value: n(ag.offline), tone: 'bad', href: '#/assets/list?agent=offline' },
            { key: 'none', label: 'No agent', value: n(ag.none), tone: 'none', href: '#/assets/list?agent=none' }]} />
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
      {monitoring && <section className="tile tile-wide" aria-labelledby="t-mon">
        <div className="tile-head"><span className="chip"><Icon name="Monitoring" size={22} /></span>
          <div><h2 id="t-mon"><a href="#/monitoring">Monitoring</a></h2><p className="muted">{mo ? `${n(mo.watched)} of ${n(mo.devices)} devices watched, ${n(mo.alertsCritical) + n(mo.alertsWarning)} open alerts` : 'Alerts for devices that are down, busy, full or failing'}</p></div>
          <a className="open" href="#/monitoring">Open Monitoring</a></div>
        {mo && <div className="rings">
          <Ring title="Device status" unit="devices" empty="No devices yet." parts={[
            { key: 'ok', label: 'OK', value: n(mo.ok), tone: 'ok', href: '#/monitoring/devices?status=ok' },
            { key: 'w', label: 'Warning', value: n(mo.warning), tone: 'warn', href: '#/monitoring/devices?status=warning' },
            { key: 'c', label: 'Critical', value: n(mo.critical), tone: 'bad', href: '#/monitoring/devices?status=critical' },
            { key: 'm', label: 'Muted', value: n(mo.muted), tone: 'info', href: '#/monitoring/devices?status=muted' },
            { key: 'u', label: 'Not watched', value: n(mo.unwatched), tone: 'none', href: '#/monitoring/devices?status=unwatched' }]} />
          <Ring title="Open alerts" unit="alerts" empty="No open alerts. All clear." parts={[
            { key: 'c', label: 'Critical', value: n(mo.alertsCritical), tone: 'bad', href: '#/monitoring/alerts?severity=critical' },
            { key: 'w', label: 'Warning', value: n(mo.alertsWarning), tone: 'warn', href: '#/monitoring/alerts?severity=warning' }]} />
          <Ring title="Disk space" unit="devices" empty="No disk data yet." parts={[
            { key: 'ok', label: '20% or more free', value: n(mo.diskOk), tone: 'ok', href: '#/monitoring/devices?disk=ok' },
            { key: 'l', label: '10 to 20% free', value: n(mo.diskLow), tone: 'warn', href: '#/monitoring/devices?disk=low' },
            { key: 'c', label: 'Under 10% free', value: n(mo.diskCritical), tone: 'bad', href: '#/monitoring/devices?disk=critical' }]} />
        </div>}
      </section>}
      {backup && <section className="tile tile-wide" aria-labelledby="t-backup">
        <div className="tile-head"><span className="chip"><Icon name="Backup" size={22} /></span>
          <div><h2 id="t-backup"><a href="#/backup">Backup</a></h2><p className="muted">{b ? `${n(b.jobs)} jobs, ${n(b.failed) + n(b.missed)} failed or missed` : 'Results of the backup software your customers run'}</p></div>
          <a className="open" href="#/backup">Open Backup</a></div>
        {b && n(b.jobs) > 0 && <div className="rings">
          <Ring title="Last result" unit="jobs" empty="No jobs yet." parts={[
            { key: 's', label: 'Success', value: n(b.success), tone: 'ok', href: '#/backup/jobs?status=success' },
            { key: 'w', label: 'Warning', value: n(b.warning), tone: 'warn', href: '#/backup/jobs?status=warning' },
            { key: 'f', label: 'Failed', value: n(b.failed), tone: 'bad', href: '#/backup/jobs?status=failed' },
            { key: 'm', label: 'Missed', value: n(b.missed), tone: 'bad', href: '#/backup/jobs?status=missed' }]} />
          <Ring title="Last good backup" unit="jobs" empty="No watched jobs." parts={[
            { key: 'd', label: 'Within a day', value: n(b.ageDay), tone: 'ok', href: '#/backup/jobs?age=day' },
            { key: 'w', label: '1 to 7 days ago', value: n(b.ageWeek), tone: 'warn', href: '#/backup/jobs?age=week' },
            { key: 'o', label: 'Over a week ago', value: n(b.ageOlder), tone: 'bad', href: '#/backup/jobs?age=older' },
            { key: 'n', label: 'Never', value: n(b.ageNever), tone: 'none', href: '#/backup/jobs?age=never' }]} />
        </div>}
      </section>}
      {vault && <section className="tile tile-wide" aria-labelledby="t-vault">
        <div className="tile-head"><span className="chip"><Icon name="Vault" size={22} /></span>
          <div><h2 id="t-vault"><a href="#/vault">Vault</a></h2><p className="muted">Customer passwords, encrypted on your computer before they are saved</p></div>
          <a className="open" href="#/vault">Open the Vault</a></div>
      </section>}
      {adm && <section className="tile tile-wide" aria-labelledby="t-ad">
        <div className="tile-head"><span className="chip"><Icon name="AD Manager" size={22} /></span>
          <div><h2 id="t-ad"><a href="#/admanager">AD Manager</a></h2><p className="muted">Look up and manage your customers' Active Directory accounts and groups</p></div>
          <a className="open" href="#/admanager">Open AD Manager</a></div>
      </section>}
      {!customer && PLANNED.filter((m) => m !== 'Assets' && m !== 'Service Desk' && (m !== 'Monitoring' || !monitoring) && (m !== 'Vault' || !vault) && (m !== 'AD Manager' || !adm) && (m !== 'Backup' || !backup)).map((m) => <div key={m} className="tile tile-soon" aria-disabled="true">
        <span className="chip"><Icon name={m} size={22} /></span>
        <h2>{m}</h2>
        <p className="muted">{ABOUT[m]}</p>
        <span className="soon">Planned</span>
      </div>)}
    </div>
  </>;
}

/** The side menu under ⚙ Administration: your team, devices setup (shared by Assets and Monitoring) and the platform's own settings. */
function AdminNav({ section, canSetup, canUpdate, isVendor, children }: { section: string; canSetup: boolean; canUpdate: boolean; isVendor: boolean; children: ReactNode }) {
  const one = useSingleCompany();
  const setup = setupPages(one);
  const cur = section.startsWith('setup/') ? 'setup/' + (setup.some(([k]) => 'setup/' + k === section) ? section.slice(6) : setup[0][0]) : section;
  const groups: [string, [string, string][]][] = [
    ['People', [['users', 'Users']]],
    ...(canSetup ? [['Devices setup', setup.map(([k, l]) => ['setup/' + k, l] as [string, string])] as [string, [string, string][]]] : []),
    ['Platform', [['mail', 'Mail'], ['branding', 'Branding'], ...(canUpdate ? [['updates', 'Updates'] as [string, string]] : []), ['licence', 'Licence'], ['subscription', 'Subscription'], ...(isVendor ? [['vendor', 'Vendor console'] as [string, string]] : [])]],
  ];
  return <div className="modshell">
    <aside className="modnav" aria-label="Administration">
      <div className="modnav-head"><strong>Administration</strong></div>
      {groups.map(([g, items]) => <div key={g} className="modnav-group"><h4>{g}</h4>
        {items.map(([k, l]) => <a key={k} href={'#/' + k} className={cur === k ? 'on' : ''} aria-current={cur === k ? 'page' : undefined}>{l}</a>)}</div>)}
    </aside>
    <main className="modmain">{children}</main>
  </div>;
}

export default function App({ fetcher }: { fetcher?: typeof fetch }) {
  const [who, setWho] = useState<WhoAmI | null>(null);
  const [err, setErr] = useState('');
  const [look, setLookState] = useState(currentLook());
  const [menuOpen, setMenuOpen] = useState(false);
  const parts = useHash();
  useEffect(() => { whoAmI(fetcher).then(setWho, (e: Error) => setErr(e.message)); }, [fetcher]);
  const singleCompany = useSingleCompany();   // one company: the pickers and filters for customers are hidden everywhere (styles.css)
  useEffect(() => { document.documentElement.classList.toggle('single-company', singleCompany && !fetcher); }, [singleCompany, fetcher]);
  const me = useLoad<Rec>(fetcher ? null : '/api/platform/me');
  const isAdmin = me.data?.role === 'admin';
  const isCustomer = me.data?.role === 'customer';
  // every time DeskAsk shows comes from the server's clock; tell an administrator when it disagrees with this computer's
  const skewMin = useMemo(() => (me.data?.serverTime ? Math.round((new Date(me.data.serverTime as string).getTime() - Date.now()) / 60000) : 0), [me.data]);
  const mine = useLoad<Rec>(fetcher ? null : '/api/platform/access/mine');
  const mods = useLoad<Rec[]>(fetcher ? null : '/api/platform/modules');
  const lic = (k: string) => !!fetcher || (Array.isArray(mods.data) && mods.data.some((m) => m.key === k && (m.licensed || m.readOnly)));   // in the licence (or kept read-only after it ended)
  const readsAssets = !!fetcher || (!!mine.data?.assets?.read && mine.data.assets.read !== 'none');
  const canAssets = lic('assets') && readsAssets;                                   // the Assets menu entry
  const inAssets = parts[0] === 'assets' && (canAssets || (lic('monitoring') && readsAssets));   // Monitoring alone still opens the shared device list
  const canSetup = isAdmin && (lic('assets') || lic('monitoring'));                 // customers, logins, agents: shared by Assets and Monitoring
  const inSetup = parts[0] === 'setup' && canSetup;
  const canDesk = lic('servicedesk');
  const inDesk = parts[0] === 'servicedesk' && canDesk;
  const canMon = lic('monitoring') && (!!fetcher || (!!mine.data?.monitoring?.read && mine.data.monitoring.read !== 'none'));
  const inMon = parts[0] === 'monitoring' && canMon;
  const canVault = !fetcher && !!me.data && me.data.role !== 'customer' && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'vault')?.licensed || mods.data.find((m) => m.key === 'vault')?.readOnly));
  const inVault = parts[0] === 'vault' && canVault;
  const inAsk = parts[0] === 'assistant' && canAssets;
  const canAd = !fetcher && !!me.data && me.data.role !== 'customer' && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'adsuite')?.licensed || mods.data.find((m) => m.key === 'adsuite')?.readOnly));
  const inAd = parts[0] === 'admanager' && canAd;
  const canBackup = !fetcher && !!(Array.isArray(mods.data) && (mods.data.find((m) => m.key === 'backup')?.licensed || mods.data.find((m) => m.key === 'backup')?.readOnly)) && !!mine.data?.backup?.read && mine.data.backup.read !== 'none';
  const inBackup = parts[0] === 'backup' && canBackup;
  const inUsers = (parts[0] === 'users' || parts[0] === 'access') && isAdmin;
  const inLicence = parts[0] === 'licence' && isAdmin;
  const inBranding = parts[0] === 'branding' && isAdmin;
  const inSub = parts[0] === 'subscription' && isAdmin;
  const brand = useBrand();
  const inMail = parts[0] === 'mail' && isAdmin;
  const commerce = useLoad<Rec>(fetcher || !isAdmin ? null : '/api/commerce/status', [isAdmin]);
  const upd = useLoad<Rec>(fetcher || !isAdmin ? null : '/api/platform/updates/access', [isAdmin]);
  const canUpdate = isAdmin && upd.data?.allowed !== false;   // the shared server is updated by the vendor only
  const inUpdates = parts[0] === 'updates' && canUpdate;
  const isVendor = isAdmin && !!commerce.data?.enabled;
  const inVendor = parts[0] === 'vendor' && isVendor;
  const readOnly = Array.isArray(mods.data) ? mods.data.filter((m) => m.readOnly).length > 0 : false;
  const inAdmin = inUsers || inSetup || inMail || inUpdates || inLicence || inSub || inBranding || inVendor;
  const inHome = !inAsk && !inAssets && !inSetup && !inDesk && !inMon && !inVault && !inAd && !inBackup && !inUsers && !inLicence && !inSub && !inBranding && !inMail && !inUpdates && !inVendor;
  // the top row: the products this person may open (a module the licence does not include is not shown)
  const products: [string, string, string, boolean][] = [['home', '#/', 'Home', inHome],
    ...(canDesk ? [['Service Desk', '#/servicedesk', 'Service Desk', inDesk] as [string, string, string, boolean]] : []),
    ...(canAssets ? [['Assets', '#/assets', 'Assets', inAssets] as [string, string, string, boolean]] : []),
    ...(canMon ? [['Monitoring', '#/monitoring', 'Monitoring', inMon] as [string, string, string, boolean]] : []),
    ...(canAd ? [['AD Manager', '#/admanager', 'AD Manager', inAd] as [string, string, string, boolean]] : []),
    ...(canVault ? [['Vault', '#/vault', 'Vault', inVault] as [string, string, string, boolean]] : []),
    ...(canBackup ? [['Backup', '#/backup', 'Backup', inBackup] as [string, string, string, boolean]] : [])];
  const adminPage = (page: ReactNode) => <AdminNav section={parts[0] === 'setup' ? 'setup/' + (parts[1] ?? '') : parts[0] === 'access' ? 'users' : (parts[0] ?? '')} canSetup={canSetup} canUpdate={canUpdate} isVendor={isVendor}>{page}</AdminNav>;
  return (
    <div className="shell shell-top">
      <header className="topnav">
        <a className="brand" href="#/" aria-label={brand.name + ' home'}>{brand.hasLogo ? <span className="logo-chip"><img src={logoUrl(brand)} alt="" /></span> : <Logo size={32} />}<span className="brand-text"><strong>{brand.name}</strong></span></a>
        <button className="topnav-menu" aria-label="Menu" aria-expanded={menuOpen} onClick={() => setMenuOpen(!menuOpen)}><Icon name="menu" size={22} /></button>
        <nav aria-label="Products" className={'products' + (menuOpen ? ' open' : '')} onClick={() => setMenuOpen(false)}>
          {products.map(([key, href, label, on]) => <a key={key} href={href} className={on ? 'on' : ''} aria-current={on ? 'page' : undefined}><Icon name={key} size={20} /><span>{label}</span></a>)}
          {isAdmin ? <a href="#/users" className={'admin-small' + (inAdmin ? ' on' : '')} aria-current={inAdmin ? 'page' : undefined}><Icon name="settings" size={20} /><span>Administration</span></a> : null}
        </nav>
        <div className="topnav-right">
          {canAssets ? <a href="#/assistant" className={'gear' + (inAsk ? ' on' : '')} aria-label="Ask DeskAsk" title="Ask DeskAsk" aria-current={inAsk ? 'page' : undefined}><Icon name="ask" size={22} /></a> : null}
          {isAdmin ? <a href="#/users" className={'gear' + (inAdmin ? ' on' : '')} aria-label="Administration" title="Administration" aria-current={inAdmin ? 'page' : undefined}><Icon name="settings" size={22} /></a> : null}
          <UserMenu name={String(me.data?.name ?? who?.name ?? '')} role={String(me.data?.role ?? '')} look={look} onLook={(v) => { setLook(v); setLookState(v); }} live={!fetcher} />
        </div>
      </header>
      <main className="main">
        {err ? <p role="alert" className="err">{err}</p> : null}
        {readOnly ? <p role="alert" className="err">The licence has ended, so DeskAsk is read-only: everything stays visible, but nothing can be changed.{isAdmin ? <> <a href="#/licence">Renew or install a licence</a>.</> : ' Ask your administrator to renew the licence.'}</p> : null}
        {isAdmin && Math.abs(skewMin) >= 3 ? <p role="alert" className="err">The server's clock is about {Math.floor(Math.abs(skewMin) / 60)} h {Math.abs(skewMin) % 60} min {skewMin > 0 ? 'ahead of' : 'behind'} this computer, so times shown here and written to logs are wrong. On the server, set the correct time zone first (Windows: <code>Set-TimeZone -Id "India Standard Time"</code> for India), then correct the time (<code>w32tm /resync</code>).</p> : null}
        {inSetup ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><SetupApp bare /></Suspense>)
          : inAssets
          ? <Suspense fallback={<p className="muted">Loading…</p>}><AssetsApp /></Suspense>
          : inDesk ? <Suspense fallback={<p className="muted">Loading…</p>}><ServiceDeskApp /></Suspense>
          : inMon ? <Suspense fallback={<p className="muted">Loading…</p>}><MonitoringApp /></Suspense>
          : inAsk ? <Suspense fallback={<p className="muted">Loading…</p>}><Assistant /></Suspense>
          : inVault ? <Suspense fallback={<p className="muted">Loading…</p>}><VaultApp /></Suspense>
          : inAd ? <Suspense fallback={<p className="muted">Loading…</p>}><AdApp /></Suspense>
          : inBackup ? <Suspense fallback={<p className="muted">Loading…</p>}><BackupApp /></Suspense>
          : inUpdates ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><Updates /></Suspense>)
          : inMail ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><Mail /></Suspense>)
          : inSub ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><Subscription /></Suspense>)
          : inBranding ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><Branding /></Suspense>)
          : inLicence ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><Licence /></Suspense>)
          : inVendor ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><VendorApp path={parts.slice(1)} /></Suspense>)
          : inUsers ? adminPage(<Suspense fallback={<p className="muted">Loading…</p>}><UsersArea meId={me.data?.sub} tab={parts[0] === 'access' ? 'groups' : (parts[1] ?? '')} /></Suspense>)
          : !err ? <div className="page"><Home who={who} live={!fetcher} assets={canAssets} monitoring={canMon && !fetcher} vault={canVault} adm={canAd} backup={canBackup} deskOn={canDesk} customer={isCustomer} /></div> : null}
        {brand.footerText ? <footer className="site-foot">{brand.footerText}</footer> : null}
      </main>
    </div>
  );
}
