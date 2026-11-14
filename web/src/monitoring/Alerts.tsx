import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { usePerms } from '../perms';
import { Act, ago, Empty, fmtDate, Loading } from '../ui';
import { METRIC_WORDS } from './Dashboard';

const dur = (a: string, b?: string | null) => {
  const m = Math.max(0, Math.round(((b ? new Date(b).getTime() : Date.now()) - new Date(a).getTime()) / 60000));
  return m < 60 ? `${m} min` : m < 1440 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${Math.floor(m / 1440)} d ${Math.floor((m % 1440) / 60)} h`;
};

/** Alert rows: what is wrong, since when, who is on it, and the ticket. Open alerts can be acknowledged or get a ticket. */
export function AlertRows({ items, device, onChange, canAct }: { items: Rec[]; device?: boolean; onChange: () => void; canAct: boolean }) {
  const [msg, setMsg] = useState('');
  return <>
    {msg && <p className="err" role="alert">{msg}</p>}
    <div className="table-wrap"><table>
      <thead><tr><th>Severity</th>{!device && <th>Device</th>}<th>What is wrong</th><th>Since</th><th>Status</th><th>Ticket</th></tr></thead>
      <tbody>{items.map((a) => <tr key={a.id} className={a.closedAt ? 'closed' : ''}>
        <td><span className={`badge ${a.closedAt ? '' : a.severity === 'critical' ? 'bad' : 'warn'}`}>{a.severity === 'critical' ? 'Critical' : 'Warning'}</span></td>
        {!device && <td><a href={`#/monitoring/device/${a.assetId}`}>{a.asset}</a><div className="muted small">{a.company}</div></td>}
        <td>{a.text}<div className="muted small">{a.rule} · {METRIC_WORDS[a.metric] ?? a.metric}</div></td>
        <td title={fmtDate(a.openedAt)}>{ago(a.openedAt)}<div className="muted small">{a.closedAt ? 'lasted ' + dur(a.openedAt, a.closedAt) : 'for ' + dur(a.openedAt)}</div></td>
        <td>{a.closedAt ? <span className="muted">{a.closeReason} {ago(a.closedAt)}</span>
          : a.ackAt ? <span>Acknowledged by {a.ackBy}</span>
          : canAct ? <Act label="Acknowledge" run={async () => { try { await api(`/api/monitoring/alerts/${a.id}/ack`, { method: 'POST', body: {} }); setMsg(''); } catch (e) { setMsg((e as Error).message); } onChange(); }} /> : <span className="muted">Open</span>}</td>
        <td>{a.ticket ? <a href={`#/servicedesk/${a.ticketId}`}>{a.ticket}</a>
          : !a.closedAt && canAct ? <Act label="Open ticket" run={async () => { try { await api(`/api/monitoring/alerts/${a.id}/ticket`, { method: 'POST', body: {} }); setMsg(''); } catch (e) { setMsg((e as Error).message); } onChange(); }} /> : <span className="muted">–</span>}</td>
      </tr>)}</tbody>
    </table></div>
  </>;
}

export default function Alerts() {
  const perms = usePerms('monitoring');
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const company = qs.get('company') ?? '';
  const [state, setState] = useState(qs.get('state') ?? 'open'); const [severity, setSeverity] = useState(qs.get('severity') ?? ''); const [metric, setMetric] = useState(qs.get('metric') ?? '');
  const [search, setSearch] = useState(''); const [skip, setSkip] = useState(0); const take = 50;
  const co = useLoad<Rec>(company ? `/api/monitoring/dashboard?company=${encodeURIComponent(company)}` : null);
  const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
  const q = `/api/monitoring/alerts?state=${state}&severity=${severity}&metric=${metric}&search=${encodeURIComponent(search)}` + (company ? `&company=${encodeURIComponent(company)}` : '') + `&skip=${skip}&take=${take}`;
  const { data, error, loading, reload } = useLoad<Rec>(q);
  useInterval(reload, 60_000);
  const items = (data?.items ?? []) as Rec[]; const total = Number(data?.total ?? 0);
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/monitoring">Monitoring</a><span aria-hidden="true">›</span>
      {company ? <><a href={`#/monitoring/customer/${company}`}>{coName}</a><span aria-hidden="true">›</span></> : null}<span aria-current="page">Alerts</span></nav>
    <h1>{company ? `Alerts for ${coName}` : 'Alerts'}</h1>
    {company && <div className="filter-chips"><a href="#/monitoring/alerts" title="Show every customer">{coName} ✕</a></div>}
    <div className="tools">
      <select value={state} onChange={(e) => { setState(e.target.value); setSkip(0); }} aria-label="Which alerts"><option value="open">Open</option><option value="closed">Cleared</option><option value="all">All</option></select>
      <select value={severity} onChange={(e) => { setSeverity(e.target.value); setSkip(0); }} aria-label="Severity"><option value="">Any severity</option><option value="critical">Critical</option><option value="warning">Warning</option></select>
      <select value={metric} onChange={(e) => { setMetric(e.target.value); setSkip(0); }} aria-label="Kind"><option value="">Any kind</option>{Object.entries(METRIC_WORDS).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
      <input placeholder="Search device, customer or text" value={search} onChange={(e) => { setSearch(e.target.value); setSkip(0); }} aria-label="Search alerts" />
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>{state === 'open' ? 'No open alerts. All clear.' : 'No alerts match.'}</Empty> : <AlertRows items={items} onChange={reload} canAct={perms.modify || perms.admin} />)}
    {total > take && <div className="tools"><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Previous</button><span className="muted">{skip + 1}–{Math.min(skip + take, total)} of {total}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Next</button></div>}
  </>;
}
