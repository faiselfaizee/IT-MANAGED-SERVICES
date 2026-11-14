import { useMemo, useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp, MiniRing, Ring, type Part } from '../Rings';

const n = (v: unknown) => Number(v ?? 0);
const coKey = (m: Rec) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
/** The device list, limited to a customer (unless all) and one ring part. */
export const devHref = (co: string, filter = '') => '#/monitoring/devices' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
export const alertHref = (co: string, filter = '') => '#/monitoring/alerts' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
const attention = (m: Rec) => 3 * n(m.critical) + n(m.warning);

export const METRIC_WORDS: Record<string, string> = { offline: 'Device down', cpu: 'Processor', memory: 'Memory', disk: 'Disk space', check: 'Service checks', cert: 'Certificates', hardware: 'Hardware' };

export function statusParts(m: Rec, co: string): Part[] {
  return [
    { key: 'ok', label: 'OK', value: n(m.ok), tone: 'ok', href: devHref(co, 'status=ok') },
    { key: 'warning', label: 'Warning', value: n(m.warning), tone: 'warn', href: devHref(co, 'status=warning') },
    { key: 'critical', label: 'Critical', value: n(m.critical), tone: 'bad', href: devHref(co, 'status=critical') },
    { key: 'muted', label: 'Muted', value: n(m.muted), tone: 'info', href: devHref(co, 'status=muted') },
    { key: 'unwatched', label: 'Not watched', value: n(m.unwatched), tone: 'none', href: devHref(co, 'status=unwatched') },
  ];
}

/** Monitoring home: one tile per customer, worst first, each with a small ring of its device states and its open alerts. */
export default function Dashboard() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/monitoring/dashboard');
  useInterval(reload, 60_000);
  const me = useLoad<Rec>('/api/platform/me');
  const [q, setQ] = useState(''); const [sort, setSort] = useState<'attention' | 'name' | 'devices'>('attention'); const [shown, setShown] = useState(60);
  const [checking, setChecking] = useState('');
  const customers = (data?.customers ?? []) as Rec[];
  const list = useMemo(() => customers
    .filter((c) => !q || String(c.company).toLowerCase().includes(q.toLowerCase()))
    .sort((a, b) => sort === 'name' ? String(a.company).localeCompare(String(b.company)) : sort === 'devices' ? n(b.devices) - n(a.devices) : attention(b) - attention(a) || String(a.company).localeCompare(String(b.company))),
  [customers, q, sort]);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  if (data?.restricted && customers.length === 1) return <CustomerDash id={coKey(customers[0])} />;
  const all = (data?.all ?? {}) as Rec;
  const checkNow = async () => {
    setChecking('Checking…');
    try { const r = await api<Rec>('/api/monitoring/evaluate', { method: 'POST', body: {} }); setChecking(`Checked: ${r.opened} new, ${r.closed} cleared, ${r.open} open.`); reload(); }
    catch (e) { setChecking((e as Error).message); }
  };
  return <>
    <header className="dash-head"><h1>Monitoring</h1><p className="muted">{customers.length} customers · {n(all.watched)} of {n(all.devices)} devices watched · {n(all.alertsCritical)} critical and {n(all.alertsWarning)} warning alerts open</p></header>
    <div className="tools">
      <input placeholder="Find a customer" value={q} onChange={(e) => { setQ(e.target.value); setShown(60); }} aria-label="Find a customer" />
      <select value={sort} onChange={(e) => setSort(e.target.value as typeof sort)} aria-label="Sort customers"><option value="attention">Needs attention first</option><option value="name">By name</option><option value="devices">Most devices first</option></select>
      <a className="button" href="#/monitoring/alerts">Open alerts</a>
      {me.data?.role === 'admin' && <button onClick={() => void checkNow()}>Check now</button>}
      {checking && <span className="muted" role="status">{checking}</span>}
    </div>
    {customers.length === 0 ? <Empty>No devices yet. Monitoring watches the devices in Assets: install the agent on computers and servers, or a site controller for printers, switches and firewalls.</Empty> :
      <div className="co-tiles">
        {!q && <Tile m={all} index={0} />}
        {list.slice(0, shown).map((c, i) => <Tile key={coKey(c)} m={c} index={i + 1} />)}
      </div>}
    {list.length > shown && <div className="tools" style={{ justifyContent: 'center' }}><button onClick={() => setShown(shown + 60)}>Show {Math.min(60, list.length - shown)} more customers</button></div>}
  </>;
}

function Tile({ m, index }: { m: Rec; index: number }) {
  const co = coKey(m); const delay = Math.min(index, 12) * 70;
  const figs: [string, number][] = [['Down', n(m.down)], ['Disks nearly full', n(m.diskCritical)], ['Checks failing', n(m.checksFailing)], ['Certificates expiring', n(m.certUrgent) + n(m.certSoon)]];
  return <a className={`co-tile${co === 'all' ? ' all' : ''}${n(m.critical) > 0 ? ' attn' : n(m.warning) > 0 ? ' warn' : ''}`} href={`#/monitoring/customer/${co}`}>
    <div className="co-tile-head"><b className="co-name">{m.company}</b><span className="muted"><CountUp value={n(m.devices)} delay={delay} /> devices</span></div>
    <div className="co-tile-body">
      <MiniRing parts={statusParts(m, co)} label={`Devices of ${m.company}`} delay={delay} />
      <ul className="co-figs">
        <li className={n(m.alertsCritical) > 0 ? 'bad' : ''}><i className="dot tone-bad" aria-hidden="true" /><span>Critical alerts</span><b><CountUp value={n(m.alertsCritical)} delay={delay} /></b></li>
        <li className={n(m.alertsWarning) > 0 ? 'warn' : ''}><i className="dot tone-warn" aria-hidden="true" /><span>Warning alerts</span><b><CountUp value={n(m.alertsWarning)} delay={delay} /></b></li>
        {figs.map(([l, v]) => <li key={l} className={v > 0 ? 'bad' : ''}><span>{l}</span><b><CountUp value={v} delay={delay} /></b></li>)}
      </ul>
    </div>
  </a>;
}

/** One customer's monitoring rings; every part opens the device or alert list filtered to it. */
export function CustomerDash({ id }: { id: string }) {
  const url = id === 'all' ? '/api/monitoring/dashboard' : `/api/monitoring/dashboard?company=${encodeURIComponent(id)}`;
  const { data, error, loading, reload } = useLoad<Rec>(url);
  useInterval(reload, 60_000);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const m = (id === 'all' ? data?.all : data?.customer) as Rec | undefined;
  if (!m) return <Empty>This customer has no devices.</Empty>;
  const co = id; const bm = (m.byMetric ?? {}) as Rec;
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/monitoring">Monitoring</a><span aria-hidden="true">›</span><span aria-current="page">{m.company}</span></nav>
    <header className="dash-head"><h1>{m.company}</h1><p className="muted">{n(m.devices)} devices, {n(m.watched)} watched, {n(m.withMetrics)} sending performance data</p>
      <a className="button primary" href={devHref(co)}>View all devices</a></header>
    <div className="rings dash-rings">
      <Ring title="Device status" unit="devices" delay={0} empty="No devices in service." parts={statusParts(m, co)} />
      <Ring title="Open alerts" unit="alerts" delay={120} empty="No open alerts. All clear." parts={[
        { key: 'c', label: 'Critical', value: n(m.alertsCritical), tone: 'bad', href: alertHref(co, 'severity=critical') },
        { key: 'w', label: 'Warning', value: n(m.alertsWarning), tone: 'warn', href: alertHref(co, 'severity=warning') }]} />
      <BarList title="Open alerts by kind" empty="No open alerts." rows={Object.keys(METRIC_WORDS).map((k) => ({ key: k, label: METRIC_WORDS[k], value: n(bm[k]), href: alertHref(co, 'metric=' + k) }))} />
      <Ring title="Disk space" unit="devices" delay={360} empty="No disk data yet. Agents 3.0 and later send it." parts={[
        { key: 'ok', label: '20% or more free', value: n(m.diskOk), tone: 'ok', href: devHref(co, 'disk=ok') },
        { key: 'low', label: '10 to 20% free', value: n(m.diskLow), tone: 'warn', href: devHref(co, 'disk=low') },
        { key: 'crit', label: 'Under 10% free', value: n(m.diskCritical), tone: 'bad', href: devHref(co, 'disk=critical') }]} />
      <Ring title="Service checks" unit="checks" delay={480} empty="No service checks. Add them on an asset's Checks tab." parts={[
        { key: 'ok', label: 'Passing', value: n(m.checksOk), tone: 'ok', href: devHref(co, 'check=ok') },
        { key: 'f', label: 'Failing', value: n(m.checksFailing), tone: 'bad', href: devHref(co, 'check=failing') },
        { key: 'n', label: 'Not run yet', value: n(m.checksNotRun), tone: 'none', href: devHref(co, 'check=notrun') }]} />
      <Ring title="Certificates" unit="checked" delay={600} empty="No certificate checks yet." parts={[
        { key: 'ok', label: 'More than 30 days left', value: n(m.certOk), tone: 'ok', href: devHref(co, 'cert=ok') },
        { key: 's', label: '8 to 30 days left', value: n(m.certSoon), tone: 'warn', href: devHref(co, 'cert=soon') },
        { key: 'u', label: '7 days or less, or expired', value: n(m.certUrgent), tone: 'bad', href: devHref(co, 'cert=urgent') }]} />
    </div>
    <p className="muted tile-foot">Counts are for devices in service. A device is watched when it has an agent, a site controller pings it, or it has service checks. Rules decide when an alert opens; it clears by itself once the problem has gone for two checks in a row.</p>
  </>;
}

/** Counts per category as bars, largest first: one colour, the number written beside each bar, each row opening its list. */
function BarList({ title, rows, empty }: { title: string; rows: { key: string; label: string; value: number; href: string }[]; empty: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  const sorted = [...rows].sort((a, b) => b.value - a.value);
  const total = rows.reduce((s, r) => s + r.value, 0);
  return <figure className="ring bar-list">
    <figcaption>{title}</figcaption>
    {total === 0 ? <p className="muted">{empty}</p> : <ul>{sorted.map((r) => <li key={r.key}><a href={r.href}>
      <span className="name">{r.label}</span><span className="track" aria-hidden="true"><i style={{ width: (r.value / max) * 100 + '%' }} /></span><b className="num"><CountUp value={r.value} /></b></a></li>)}</ul>}
  </figure>;
}
