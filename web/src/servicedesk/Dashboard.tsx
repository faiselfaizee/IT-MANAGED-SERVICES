import { useState } from 'react';
import { useInterval, useLoad, type Rec } from '../http';
import { CountUp, MiniRing, Ring, type Part } from '../Rings';
import { Empty, Loading } from '../ui';

const n = (v: unknown) => Number(v ?? 0);

function parts(m: Rec): Part[] {
  return [
    { key: 'o', label: 'New', value: n(m.open), tone: 'info', href: '#/servicedesk' },
    { key: 'p', label: 'In progress', value: n(m.progress), tone: 'ok', href: '#/servicedesk' },
    { key: 'h', label: 'Awaiting customer', value: n(m.hold), tone: 'none', href: '#/servicedesk' },
  ];
}

function Tile({ m, index }: { m: Rec; index: number }) {
  const delay = Math.min(index, 12) * 70; const all = m.companyId == null && m.company === 'All customers';
  const active = n(m.open) + n(m.progress) + n(m.hold);
  return <a className={`co-tile${all ? ' all' : ''}${n(m.overdue) > 0 ? ' attn' : n(m.critical) > 0 ? ' warn' : ''}`} href="#/servicedesk">
    <div className="co-tile-head"><b className="co-name">{m.company}</b><span className="muted"><CountUp value={active} delay={delay} /> open</span></div>
    <div className="co-tile-body">
      <MiniRing parts={parts(m)} label={`Open tickets of ${m.company}`} delay={delay} />
      <ul className="co-figs">
        <li className={n(m.overdue) > 0 ? 'bad' : ''}><i className="dot tone-bad" aria-hidden="true" /><span>Overdue</span><b><CountUp value={n(m.overdue)} delay={delay} /></b></li>
        <li className={n(m.critical) > 0 ? 'warn' : ''}><i className="dot tone-warn" aria-hidden="true" /><span>Critical</span><b><CountUp value={n(m.critical)} delay={delay} /></b></li>
        <li><span>Not assigned</span><b><CountUp value={n(m.unassigned)} delay={delay} /></b></li>
        <li><i className="dot tone-ok" aria-hidden="true" /><span>Solved, last 30 days</span><b><CountUp value={n(m.done)} delay={delay} /></b></li>
      </ul>
    </div>
  </a>;
}

/** The Service Desk dashboard: rings for the whole desk, then one tile of circles per customer, like Monitoring and Backup. */
export default function Dashboard() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/dashboard');
  useInterval(reload, 60_000);
  const [q, setQ] = useState('');
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const all = (data?.all ?? {}) as Rec; const tiles = (Array.isArray(data?.tiles) ? data!.tiles : []) as Rec[];
  const active = n(all.open) + n(all.progress) + n(all.hold);
  const list = tiles.filter((t) => !q || String(t.company).toLowerCase().includes(q.toLowerCase()));
  return <>
    <header className="dash-head"><h1>Service Desk</h1><p className="muted">{active} open tickets · {n(all.overdue)} overdue · {n(all.unassigned)} not assigned · {n(all.done)} solved in the last 30 days</p>
      <a className="button primary" href="#/servicedesk/all">All tickets</a></header>
    <div className="rings dash-rings">
      <Ring title="Open tickets" unit="open" delay={0} empty="No open tickets." parts={parts(all)} />
      <Ring title="Service level" unit="open" delay={120} empty="No open tickets." parts={[
        { key: 'in', label: 'On time', value: Math.max(active - n(all.overdue), 0), tone: 'ok' },
        { key: 'late', label: 'Overdue', value: n(all.overdue), tone: 'bad' }]} />
      <Ring title="Handling" unit="open" delay={240} empty="No open tickets." parts={[
        { key: 'a', label: 'Assigned', value: Math.max(active - n(all.unassigned), 0), tone: 'ok' },
        { key: 'u', label: 'Not assigned', value: n(all.unassigned), tone: 'warn' }]} />
    </div>
    {tiles.length === 0 ? <Empty>No tickets yet.</Empty> : <>
      {tiles.length > 6 && <div className="tools"><input placeholder="Find a customer" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find a customer" /></div>}
      <div className="co-tiles">{list.map((t, i) => <Tile key={String(t.companyId ?? 'none')} m={t} index={i + 1} />)}</div></>}
  </>;
}
