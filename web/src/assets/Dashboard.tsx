import { useMemo, useState } from 'react';
import { useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp, MiniRing, Ring, type Part } from '../Rings';

const n = (v: unknown) => Number(v ?? 0);
/** The customer part of an asset list address: a customer id, "none" for assets without one, nothing for all. */
const coKey = (m: Rec) => (m.companyId == null ? (m.company === 'All customers' ? 'all' : 'none') : String(m.companyId));
export const listHref = (co: string, filter = '') => '#/assets/list' + (() => { const q = [co !== 'all' ? `company=${co}` : '', filter].filter(Boolean).join('&'); return q ? '?' + q : ''; })();
const attention = (m: Rec) => n(m.agentOffline) + n(m.nonCompliant) + n(m.warrantyExpired) + 3 * n(m.hardwareNotOk);

function agentParts(m: Rec, co: string): Part[] {
  return [
    { key: 'on', label: 'Live', value: n(m.agentOnline), tone: 'ok', href: listHref(co, 'agent=online') },
    { key: 'due', label: 'Live, update due', value: n(m.agentUpdate), tone: 'warn', href: listHref(co, 'agent=update') },
    { key: 'off', label: 'Not reporting', value: n(m.agentOffline), tone: 'bad', href: listHref(co, 'agent=offline') },
    { key: 'none', label: 'No agent', value: n(m.agentNone), tone: 'none', href: listHref(co, 'agent=none') },
  ];
}

/** Assets home: one tile per customer, worst first, each with a small ring of its agents and its main problems. */
export default function Dashboard() {
  const { data, error, loading } = useLoad<Rec>('/api/assets/dashboard');
  const [q, setQ] = useState(''); const [sort, setSort] = useState<'attention' | 'name' | 'assets'>('attention'); const [shown, setShown] = useState(60);
  const customers = (data?.customers ?? []) as Rec[];
  const list = useMemo(() => customers
    .filter((c) => !q || String(c.company).toLowerCase().includes(q.toLowerCase()))
    .sort((a, b) => sort === 'name' ? String(a.company).localeCompare(String(b.company)) : sort === 'assets' ? n(b.total) - n(a.total) : attention(b) - attention(a) || String(a.company).localeCompare(String(b.company))),
  [customers, q, sort]);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  // someone limited to one customer goes straight to that customer's dashboard
  if (data?.restricted && customers.length === 1) return <CustomerDash id={coKey(customers[0])} />;
  return <>
    <header className="dash-head"><h1>Assets</h1><p className="muted">{customers.length} customers · {n(data?.all?.total)} assets</p></header>
    <div className="tools">
      <input placeholder="Find a customer" value={q} onChange={(e) => { setQ(e.target.value); setShown(60); }} aria-label="Find a customer" />
      <select value={sort} onChange={(e) => setSort(e.target.value as typeof sort)} aria-label="Sort customers"><option value="attention">Needs attention first</option><option value="name">By name</option><option value="assets">Most assets first</option></select>
      <a className="button" href="#/assets/list">All assets</a>
    </div>
    {customers.length === 0 ? <Empty>No assets yet. Add assets, or install the agent or a site controller to find them.</Empty> :
      <div className="co-tiles">
        {!q && data?.all && <Tile m={data.all as Rec} index={0} />}
        {list.slice(0, shown).map((c, i) => <Tile key={coKey(c)} m={c} index={i + 1} />)}
      </div>}
    {list.length > shown && <div className="tools" style={{ justifyContent: 'center' }}><button onClick={() => setShown(shown + 60)}>Show {Math.min(60, list.length - shown)} more customers</button></div>}
  </>;
}

function Tile({ m, index }: { m: Rec; index: number }) {
  const co = coKey(m); const delay = Math.min(index, 12) * 70;
  const problems: [string, number, string][] = [
    ['Not compliant', n(m.nonCompliant), 'compliance=noncompliant'],
    ['Warranty expired or ending', n(m.warrantyExpired) + n(m.warrantySoon), 'warranty=expired'],
    ['Hardware not OK', n(m.hardwareNotOk), 'health=bad'],
  ];
  return <a className={`co-tile${co === 'all' ? ' all' : ''}${attention(m) > 0 ? ' attn' : ''}`} href={`#/assets/customer/${co}`}>
    <div className="co-tile-head"><b className="co-name">{m.company}</b><span className="muted"><CountUp value={n(m.total)} delay={delay} /> assets</span></div>
    <div className="co-tile-body">
      <MiniRing parts={agentParts(m, co)} label={`Agents of ${m.company}`} delay={delay} />
      <ul className="co-figs">
        <li><i className="dot tone-ok" aria-hidden="true" /><span>Live</span><b><CountUp value={n(m.agentOnline) + n(m.agentUpdate)} delay={delay} /></b></li>
        <li><i className="dot tone-bad" aria-hidden="true" /><span>Not reporting</span><b><CountUp value={n(m.agentOffline)} delay={delay} /></b></li>
        {problems.map(([l, v]) => <li key={l} className={v > 0 ? 'bad' : ''}><span>{l}</span><b><CountUp value={v} delay={delay} /></b></li>)}
      </ul>
    </div>
  </a>;
}

/** One customer's detailed dashboard; every ring part opens the asset list filtered to it. */
export function CustomerDash({ id }: { id: string }) {
  const url = id === 'all' ? '/api/assets/dashboard' : `/api/assets/dashboard?company=${encodeURIComponent(id)}`;
  const { data, error, loading } = useLoad<Rec>(url);
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const m = (id === 'all' ? data?.all : data?.customer) as Rec | undefined;
  if (!m) return <Empty>This customer has no assets.</Empty>;
  const co = id;
  const ok = Math.max(n(m.inService) - n(m.hardwareNotOk), 0);
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/assets">Assets</a><span aria-hidden="true">›</span><span aria-current="page">{m.company}</span></nav>
    <header className="dash-head"><h1>{m.company}</h1><p className="muted">{n(m.total)} assets, {n(m.inService)} in service</p>
      <a className="button primary" href={listHref(co)}>View all assets</a></header>
    <div className="rings dash-rings">
      <Ring title="Agents" unit="devices" delay={0} empty="No devices in service." parts={agentParts(m, co)} />
      <Ring title="Compliance" unit="with agent" delay={120} empty="No device reports through an agent yet." parts={[
        { key: 'c', label: 'Compliant', value: n(m.compliant), tone: 'ok', href: listHref(co, 'compliance=compliant') },
        { key: 'nc', label: 'Not compliant', value: n(m.nonCompliant), tone: 'bad', href: listHref(co, 'compliance=noncompliant') },
        { key: 'u', label: 'No data yet', value: n(m.noData), tone: 'none', href: listHref(co, 'compliance=nodata') }]} />
      <Ring title="Warranty" unit="devices" delay={240} empty="No devices in service." parts={[
        { key: 'ok', label: 'Covered', value: n(m.warrantyCovered), tone: 'ok', href: listHref(co, 'warranty=covered') },
        { key: 'soon', label: 'Ends within 30 days', value: n(m.warrantySoon), tone: 'warn', href: listHref(co, 'warranty=soon') },
        { key: 'x', label: 'Expired', value: n(m.warrantyExpired), tone: 'bad', href: listHref(co, 'warranty=expired') },
        { key: 'u', label: 'Not recorded', value: n(m.warrantyUnknown), tone: 'none', href: listHref(co, 'warranty=unknown') }]} />
      <Ring title="Asset types" unit="in service" delay={360} empty="No devices in service." parts={[
        { key: 'pc', label: 'Computers', value: n(m.computers), tone: 'info', href: listHref(co, 'kind=computer') },
        { key: 'srv', label: 'Servers and hypervisors', value: n(m.servers), tone: 'ok', href: listHref(co, 'kind=server') },
        { key: 'net', label: 'Network devices', value: n(m.network), tone: 'warn', href: listHref(co, 'kind=network') },
        { key: 'prn', label: 'Printers', value: n(m.printers), tone: 'bad', href: listHref(co, 'kind=printer') },
        { key: 'o', label: 'Other', value: n(m.other), tone: 'none', href: listHref(co, 'kind=other') }]} />
      <Ring title="Hardware health" unit="in service" delay={480} empty="No devices in service." parts={[
        { key: 'bad', label: 'A part reports a fault', value: n(m.hardwareNotOk), tone: 'bad', href: listHref(co, 'health=bad') },
        { key: 'ok', label: 'No problem reported', value: ok, tone: 'ok' }]} />
      <Ring title="Network ports" unit="ports" delay={600} empty="No switch or firewall is read yet." parts={[
        { key: 'u', label: 'In use', value: n(m.portsUsed), tone: 'ok', href: listHref(co, 'kind=network') },
        { key: 'f', label: 'Free', value: n(m.portsFree), tone: 'info', href: listHref(co, 'kind=network') },
        { key: 'o', label: 'Switched off', value: n(m.portsOff), tone: 'none', href: listHref(co, 'kind=network') }]} />
    </div>
    <p className="muted tile-foot">Counts are for assets in service (not retired or lost). Compliant means antivirus on and patches from the last 30 days. Hardware health comes from server management controllers, hypervisors and network devices read by a site controller.</p>
  </>;
}
