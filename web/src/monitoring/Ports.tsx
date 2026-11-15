import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Empty } from '../ui';
import { LineChart } from './Chart';
import { Pct } from './Devices';

const fmtBps = (b: number) => (b >= 1e9 ? (b / 1e9).toFixed(1) + ' Gbit/s' : b >= 1e6 ? (b / 1e6).toFixed(1) + ' Mbit/s' : b >= 1e3 ? (b / 1e3).toFixed(0) + ' kbit/s' : Math.round(b) + ' bit/s');
const speed = (m: number) => (m >= 1000 ? `${m / 1000} Gbps` : m > 0 ? `${m} Mbps` : '');

/** A network device's ports: state, traffic, errors, and which ones are watched (a watched port going down opens an alert). */
export function Ports({ id, ports, at, canEdit, onChange }: { id: string; ports: Rec[]; at: string | null; canEdit: boolean; onChange: () => void }) {
  const [open, setOpen] = useState<string | null>(null); const [range, setRange] = useState('24h'); const [show, setShow] = useState<'all' | 'up' | 'watched'>('all');
  const series = useLoad<Rec>(open ? `/api/monitoring/devices/${id}/ports/${encodeURIComponent(open)}/series?range=${range}` : null, [open, range]);
  if (ports.length === 0) return <Empty>No port readings yet. The site controller reads the ports of network devices every 5 minutes (read mode SNMP or Network device).</Empty>;
  const list = ports.filter((p) => show === 'all' || (show === 'up' ? p.up : p.watched));
  const pts = (series.data?.points ?? []) as Rec[];
  const sel = ports.find((p) => p.index === open);
  return <>
    <div className="tools"><span className="muted">{ports.filter((p) => p.up).length} of {ports.length} ports up{at ? `, read ${new Date(at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}` : ''}</span>
      <select value={show} onChange={(e) => setShow(e.target.value as typeof show)} aria-label="Which ports"><option value="all">All ports</option><option value="up">Ports up</option><option value="watched">Watched ports</option></select></div>
    <div className="table-wrap"><table>
      <thead><tr><th>Watched</th><th>Port</th><th>State</th><th>In</th><th>Out</th><th>Errors / min</th><th /></tr></thead>
      <tbody>{list.map((p) => <tr key={p.index} className={open === p.index ? 'sel' : ''}>
        <td>{canEdit ? <input type="checkbox" checked={p.watched} aria-label={`Watch ${p.name}`} onChange={async (e) => { await api(`/api/monitoring/devices/${id}/ports/${encodeURIComponent(p.index)}/watch`, { method: 'PUT', body: { watched: e.target.checked } }); onChange(); }} /> : p.watched ? 'Yes' : ''}</td>
        <td>{p.name}{p.alias && <div className="muted small">{p.alias}</div>}</td>
        <td>{!p.adminUp ? <span className="badge">Switched off</span> : p.up ? <span className="badge ok">Up {speed(p.mbps)}</span> : <span className={`badge ${p.watched ? 'bad' : ''}`}>Down</span>}</td>
        <td>{p.up && p.rates ? <><Pct v={p.inPct} high={80} /> <span className="muted small">{fmtBps(p.inBps)}</span></> : <span className="muted">–</span>}</td>
        <td>{p.up && p.rates ? <><Pct v={p.outPct} high={80} /> <span className="muted small">{fmtBps(p.outBps)}</span></> : <span className="muted">–</span>}</td>
        <td>{p.rates && (p.errors > 0 || p.discards > 0) ? <span className={p.errors >= 1 ? 'badge warn' : ''}>{p.errors.toFixed(1)}{p.discards > 0 ? ` (+${p.discards.toFixed(1)} discarded)` : ''}</span> : <span className="muted">0</span>}</td>
        <td><button className="link" onClick={() => setOpen(open === p.index ? null : p.index)}>{open === p.index ? 'Hide traffic' : 'Traffic'}</button></td>
      </tr>)}</tbody>
    </table></div>
    {sel && <div className="port-chart">
      <div className="tools"><span className="muted">Traffic of {sel.name}</span>{[['6h', '6 hours'], ['24h', '24 hours'], ['7d', '7 days'], ['30d', '30 days']].map(([k, l]) => <button key={k} className={range === k ? 'primary' : ''} aria-pressed={range === k} onClick={() => setRange(k)}>{l}</button>)}</div>
      <LineChart title={`${sel.name}: share of ${speed(sel.mbps) || 'port speed'} in use`} at={pts.map((p) => new Date(p.at).getTime())}
        from={series.data ? new Date(series.data.from).getTime() : Date.now() - 86400000} to={series.data ? new Date(series.data.to).getTime() : Date.now()} stepMs={series.data?.hourly ? 3600000 : 300000}
        series={[{ key: 'in', label: 'In', values: pts.map((p) => p.inPct), peaks: series.data?.hourly ? pts.map((p) => p.inMax) : undefined, cls: 's1' },
          { key: 'out', label: 'Out', values: pts.map((p) => p.outPct), peaks: series.data?.hourly ? pts.map((p) => p.outMax) : undefined, cls: 's2' }]} />
    </div>}
  </>;
}
