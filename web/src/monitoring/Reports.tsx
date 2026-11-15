import { useState } from 'react';
import { saveFile, useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import { CountUp } from '../Rings';

const KIND: Record<string, string> = { offline: 'Device down', cpu: 'Processor', memory: 'Memory', disk: 'Disk space', check: 'Service checks', cert: 'Certificates', hardware: 'Hardware', traffic: 'Port traffic', port: 'Ports down', service: 'Services', process: 'Programs', log: 'Device messages' };
const pct = (v: number) => (v >= 99.995 ? '100%' : v.toFixed(v >= 99 ? 2 : 1) + '%');
const mins = (m: number) => (m < 1 ? 'none' : m < 60 ? `${Math.round(m)} min` : m < 1440 ? `${Math.floor(m / 60)} h ${Math.round(m % 60)} min` : `${Math.floor(m / 1440)} d ${Math.floor((m % 1440) / 60)} h`);

function months(): [string, string][] {
  const out: [string, string][] = []; const d = new Date(); d.setUTCDate(1);
  for (let i = 0; i < 13; i++) { const k = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`; out.push([k, d.toLocaleDateString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' }) + (i === 0 ? ' (so far)' : '')]); d.setUTCMonth(d.getUTCMonth() - 1); }
  return out;
}

/** The uptime report of a month, for one customer or all; the same as the PDF mailed on the 1st. */
export default function Reports() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const ms = months();
  const [month, setMonth] = useState(ms[1][0]); const [company, setCompany] = useState(qs.get('company') ?? 'all');
  const dash = useLoad<Rec>('/api/monitoring/dashboard');
  const { data, error, loading } = useLoad<Rec>(`/api/monitoring/reports/uptime?company=${encodeURIComponent(company)}&month=${month}`);
  const cos = ((dash.data?.customers ?? []) as Rec[]).filter((c) => c.companyId != null);
  const lines = (data?.lines ?? []) as Rec[]; const byKind = Object.entries((data?.byKind ?? {}) as Record<string, number>).sort((a, b) => b[1] - a[1]);
  const [err, setErr] = useState('');
  return <>
    <h1>Uptime report</h1>
    <div className="tools">
      <select value={company} onChange={(e) => setCompany(e.target.value)} aria-label="Customer">{!dash.data?.restricted && <option value="all">All customers</option>}{cos.map((c) => <option key={c.companyId} value={c.companyId}>{c.company}</option>)}</select>
      <select value={month} onChange={(e) => setMonth(e.target.value)} aria-label="Month">{ms.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
      <button onClick={async () => { try { setErr(''); await saveFile(`/api/monitoring/reports/uptime.pdf?company=${encodeURIComponent(company)}&month=${month}`, `uptime-${month}.pdf`); } catch (e) { setErr((e as Error).message); } }}>Download PDF</button>
      {err && <span className="err" role="alert">{err}</span>}
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && <>
      <div className="mon-now">
        <div><span className="muted">Average availability</span><b>{pct(Number(data.availability))}</b></div>
        <div><span className="muted">Devices watched</span><b><CountUp value={Number(data.devices)} /></b></div>
        <div><span className="muted">Outages</span><b><CountUp value={Number(data.outages)} /></b><span className="muted small">{mins(Number(data.downMinutes))} down in all</span></div>
        <div><span className="muted">Critical alerts</span><b><CountUp value={Number(data.critical)} /></b></div>
        <div><span className="muted">Warning alerts</span><b><CountUp value={Number(data.warning)} /></b></div>
        <div><span className="muted">Tickets from alerts</span><b><CountUp value={Number(data.tickets)} /></b></div>
      </div>
      <div className="grid two">
        <section><h2>Availability by device</h2>
          {lines.length === 0 ? <Empty>No watched devices.</Empty> : <div className="table-wrap"><table><thead><tr><th>Device</th><th>Down for</th><th>Availability</th><th>Alerts</th></tr></thead>
            <tbody>{lines.slice(0, 100).map((l) => <tr key={l.id}><td><a href={`#/monitoring/device/${l.id}`}>{l.name}</a><div className="muted small">{l.company}</div></td><td>{mins(l.downMinutes)}</td>
              <td><span className={l.availability < 99 ? 'badge bad' : l.availability < 99.9 ? 'badge warn' : ''}>{pct(l.availability)}</span></td><td>{l.alerts}</td></tr>)}</tbody></table></div>}
        </section>
        <section><h2>Alerts by kind</h2>
          {byKind.length === 0 ? <Empty>No alerts this month.</Empty> : <table><thead><tr><th>Kind</th><th>Alerts</th></tr></thead><tbody>{byKind.map(([k, v]) => <tr key={k}><td>{KIND[k] ?? k}</td><td>{v}</td></tr>)}</tbody></table>}
          <p className="muted small">Availability is the share of the month a device had no open "down" alert. To mail this report on the 1st of every month, switch it on in Monitoring › Settings.</p>
        </section>
      </div>
    </>}
  </>;
}
