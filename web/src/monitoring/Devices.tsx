import { useState } from 'react';
import { useInterval, useLoad, type Rec } from '../http';
import { ago, Empty, Loading } from '../ui';

/** Filters that come from a ring part, with the words shown for them. */
export const DEV_FILTERS: Record<string, Record<string, string>> = {
  status: { ok: 'OK', warning: 'Warning', critical: 'Critical', muted: 'Muted', unwatched: 'Not watched', down: 'Down' },
  disk: { ok: '20% or more disk free', low: '10 to 20% disk free', critical: 'Under 10% disk free', nodata: 'No disk data' },
  check: { ok: 'Checks passing', failing: 'A check failing', notrun: 'A check not run yet' },
  cert: { ok: 'Certificates fine', soon: 'Certificate expires within 30 days', urgent: 'Certificate expires within 7 days' },
  kind: { computer: 'Computers', server: 'Servers', network: 'Network devices', printer: 'Printers', other: 'Other devices' },
  metric: { offline: 'Down alerts', cpu: 'Processor alerts', memory: 'Memory alerts', disk: 'Disk alerts', check: 'Check alerts', cert: 'Certificate alerts', hardware: 'Hardware alerts' },
};
const TONE: Record<string, string> = { ok: 'ok', warning: 'warn', critical: 'bad', muted: 'info', unwatched: '' };
const WORD: Record<string, string> = { ok: 'OK', warning: 'Warning', critical: 'Critical', muted: 'Muted', unwatched: 'Not watched' };

export function StatusBadge({ s }: { s: string }) { return <span className={`badge ${TONE[s] ?? ''}`}><i className={`dot tone-${TONE[s] || 'none'}`} aria-hidden="true" /> {WORD[s] ?? s}</span>; }

/** A small bar for a percentage, with the number beside it (the number carries the meaning, the bar only helps the eye). */
export function Pct({ v, high = 90, invert = false }: { v: number | null | undefined; high?: number; invert?: boolean }) {
  if (v == null) return <span className="muted">–</span>;
  const bad = invert ? v < 100 - high : v >= high;
  return <span className="pct"><span className="pct-bar" aria-hidden="true"><i className={bad ? 'hot' : ''} style={{ width: Math.max(0, Math.min(100, v)) + '%' }} /></span>{v.toFixed(0)}%</span>;
}

export default function Devices() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const company = qs.get('company') ?? '';
  const filters = Object.keys(DEV_FILTERS).map((k) => [k, qs.get(k) ?? ''] as const).filter(([k, v]) => v && DEV_FILTERS[k][v]);
  const [search, setSearch] = useState(''); const [skip, setSkip] = useState(0); const take = 50;
  const co = useLoad<Rec>(company ? `/api/monitoring/dashboard?company=${encodeURIComponent(company)}` : null);
  const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
  const q = `/api/monitoring/devices?search=${encodeURIComponent(search)}` + (company ? `&company=${encodeURIComponent(company)}` : '') + filters.map(([k, v]) => `&${k}=${v}`).join('') + `&skip=${skip}&take=${take}`;
  const { data, error, loading, reload } = useLoad<Rec>(q);
  useInterval(reload, 60_000);
  const items = (data?.items ?? []) as Rec[]; const total = Number(data?.total ?? 0);
  const without = (key: string) => { const x = new URLSearchParams(qs); x.delete(key); const t = x.toString(); return '#/monitoring/devices' + (t ? '?' + t : ''); };
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/monitoring">Monitoring</a><span aria-hidden="true">›</span>
      {company ? <><a href={`#/monitoring/customer/${company}`}>{coName}</a><span aria-hidden="true">›</span></> : null}
      <span aria-current="page">{filters.length ? filters.map(([k, v]) => DEV_FILTERS[k][v]).join(', ') : 'All devices'}</span></nav>
    <h1>{company ? coName : 'All devices'}</h1>
    {(company || filters.length > 0) && <div className="filter-chips">{company && <a href={without('company')} title="Show every customer">{coName} ✕</a>}{filters.map(([k, v]) => <a key={k} href={without(k)} title="Remove this filter">{DEV_FILTERS[k][v]} ✕</a>)}</div>}
    <div className="tools"><input placeholder="Search name, tag, address or customer" value={search} onChange={(e) => { setSearch(e.target.value); setSkip(0); }} aria-label="Search devices" /></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No devices match.</Empty> :
      <div className="table-wrap"><table>
        <thead><tr><th>Status</th><th>Device</th><th>Customer</th><th>Processor</th><th>Memory</th><th>Lowest disk free</th><th>Reachable</th><th>Alerts</th></tr></thead>
        <tbody>{items.map((d) => <tr key={d.id}>
          <td><StatusBadge s={d.status} /></td>
          <td><a href={`#/monitoring/device/${d.id}`}>{d.name}</a><div className="muted small">{d.tag}{d.ip ? ' · ' + d.ip : ''}</div></td>
          <td>{d.company}</td>
          <td><Pct v={d.cpu} /></td><td><Pct v={d.mem} /></td>
          <td>{d.worstDisk == null ? <span className="muted">–</span> : <Pct v={d.worstDisk} high={90} invert />}</td>
          <td>{d.pingUp === false ? <span className="badge bad">No answer {d.downSince ? ago(d.downSince) : ''}</span> : d.pingUp ? `${d.rttMs} ms` : d.agent ? (d.agentSeen ? 'Agent ' + ago(d.agentSeen) : 'Agent never reported') : <span className="muted">–</span>}</td>
          <td>{d.alerts > 0 ? <a href={`#/monitoring/device/${d.id}`}>{d.alertsCritical > 0 ? <span className="badge bad">{d.alerts}</span> : <span className="badge warn">{d.alerts}</span>}</a> : <span className="muted">0</span>}</td>
        </tr>)}</tbody>
      </table></div>)}
    {total > take && <div className="tools"><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Previous</button><span className="muted">{skip + 1}–{Math.min(skip + take, total)} of {total}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Next</button></div>}
  </>;
}
