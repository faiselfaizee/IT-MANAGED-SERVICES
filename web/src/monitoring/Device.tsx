import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { usePerms } from '../perms';
import { Act, ago, Empty, fmtBytes, fmtDate, fmtUptime, Loading } from '../ui';
import { LineChart } from './Chart';
import { Pct, StatusBadge } from './Devices';
import { AlertRows } from './Alerts';

const RANGES: [string, string][] = [['6h', '6 hours'], ['24h', '24 hours'], ['7d', '7 days'], ['30d', '30 days'], ['90d', '90 days']];

export default function Device({ id }: { id: string }) {
  const perms = usePerms('monitoring');
  const { data, error, loading, reload } = useLoad<Rec>(`/api/monitoring/devices/${id}`);
  const [range, setRange] = useState('24h');
  const series = useLoad<Rec>(`/api/monitoring/devices/${id}/series?range=${range}`, [range]);
  useInterval(() => { reload(); series.reload(); }, 60_000);
  const [muteHours, setMuteHours] = useState('2'); const [reason, setReason] = useState('');
  if (loading && !data) return <Loading loading error={error} />;
  if (error) return <Loading loading={false} error={error} />;
  const d = (data?.device ?? {}) as Rec; const alerts = (data?.alerts ?? []) as Rec[];
  const pts = (series.data?.points ?? []) as Rec[];
  const at = pts.map((p) => new Date(p.at).getTime());
  const from = series.data ? new Date(series.data.from).getTime() : Date.now() - 86400_000; const to = series.data ? new Date(series.data.to).getTime() : Date.now();
  const step = series.data?.hourly ? 3600_000 : 60_000;
  const canMute = perms.modify || perms.admin;
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/monitoring">Monitoring</a><span aria-hidden="true">›</span>
      <a href={`#/monitoring/customer/${d.companyId ?? 'none'}`}>{d.company}</a><span aria-hidden="true">›</span><span aria-current="page">{d.name}</span></nav>
    <header className="dash-head"><h1>{d.name}</h1><StatusBadge s={d.status} /><p className="muted">{d.tag}{d.ip ? ' · ' + d.ip : ''}{d.os ? ' · ' + d.os : ''}</p>
      <a className="button" href={`#/assets/${d.id}`}>Open the asset</a></header>

    {d.muted && <p className="note" role="status">Muted {d.muted.until ? 'until ' + fmtDate(d.muted.until) : 'until someone unmutes it'} by {d.muted.by}{d.muted.reason ? ': ' + d.muted.reason : ''}. No alerts open for it meanwhile.
      {canMute && <> <Act label="Unmute" run={async () => { await api(`/api/monitoring/devices/${id}/mute`, { method: 'DELETE' }); reload(); }} /></>}</p>}

    <div className="mon-now">
      <div><span className="muted">Processor</span><b>{d.cpu == null ? '–' : d.cpu.toFixed(0) + '%'}</b></div>
      <div><span className="muted">Memory</span><b>{d.mem == null ? '–' : d.mem.toFixed(0) + '%'}</b><span className="muted small">{d.memTotal ? 'of ' + fmtBytes(d.memTotal) : ''}</span></div>
      <div><span className="muted">Lowest disk free</span><b>{d.worstDisk == null ? '–' : d.worstDisk.toFixed(0) + '%'}</b></div>
      <div><span className="muted">Running for</span><b>{d.uptimeS ? fmtUptime(d.uptimeS) : '–'}</b></div>
      <div><span className="muted">Reachable</span><b>{d.pingUp === false ? 'No' : d.pingUp ? 'Yes' : '–'}</b><span className="muted small">{d.pingUp ? d.rttMs + ' ms' : d.pingUp === false && d.downSince ? 'since ' + ago(d.downSince) : 'no site controller'}</span></div>
      <div><span className="muted">Agent</span><b>{d.agent ? (d.agentSeen ? ago(d.agentSeen) : 'never') : 'none'}</b><span className="muted small">{d.agentVersion}</span></div>
    </div>
    {d.agent && !d.metricsAt && <p className="muted">No performance data yet. Agents send processor, memory and disk use from version 3.0: update the agent from the asset page.</p>}

    <div className="tools"><span className="muted">Period</span>{RANGES.map(([k, l]) => <button key={k} className={range === k ? 'primary' : ''} aria-pressed={range === k} onClick={() => setRange(k)}>{l}</button>)}
      {series.data?.hourly && <span className="muted">Hourly averages; the faint line is the hourly peak.</span>}</div>
    <LineChart title="Processor and memory use" at={at} from={from} to={to} stepMs={step} series={[
      { key: 'cpu', label: 'Processor', values: pts.map((p) => p.cpu), peaks: series.data?.hourly ? pts.map((p) => p.cpuMax) : undefined, cls: 's1' },
      { key: 'mem', label: 'Memory', values: pts.map((p) => p.mem), peaks: series.data?.hourly ? pts.map((p) => p.memMax) : undefined, cls: 's2' }]} />
    <LineChart title="Lowest free disk space" at={at} from={from} to={to} stepMs={step} series={[{ key: 'disk', label: 'Free space', values: pts.map((p) => p.diskFree), cls: 's3' }]} />

    <h2>Open and recent alerts</h2>
    {alerts.length === 0 ? <Empty>No alerts for this device.</Empty> : <AlertRows items={alerts} device onChange={reload} canAct={canMute} />}

    <div className="grid two">
      <section><h2>Disks and datastores</h2>
        {(d.disks ?? []).length === 0 ? <Empty>No disk data.</Empty> : <table><thead><tr><th>Disk</th><th>Free</th><th>Size</th></tr></thead>
          <tbody>{(d.disks as Rec[]).map((k) => <tr key={k.name}><td>{k.name}</td><td><Pct v={k.freePct} invert /> <span className="muted small">{fmtBytes(k.free)}</span></td><td>{fmtBytes(k.total)}</td></tr>)}</tbody></table>}
      </section>
      <section><h2>Service checks</h2>
        {(d.checks ?? []).length === 0 ? <Empty>No service checks. Add them on the asset's Checks tab.</Empty> : <table><thead><tr><th>Check</th><th>Result</th><th>Checked</th></tr></thead>
          <tbody>{(d.checks as Rec[]).map((c) => <tr key={c.id}><td>{c.name} <span className="muted small">{String(c.type).toUpperCase()}</span></td>
            <td>{c.ok == null ? <span className="muted">Not run yet</span> : c.ok ? <span className="badge ok">Passing</span> : <span className="badge bad">Failing {c.fails}×</span>}
              {c.certDays != null && <span className="muted small"> certificate {c.certDays <= 0 ? 'expired' : Math.floor(c.certDays) + ' days left'}</span>}
              {!c.ok && c.detail ? <div className="muted small">{c.detail}</div> : null}</td>
            <td>{c.at ? ago(c.at) : '–'}</td></tr>)}</tbody></table>}
        {(d.faults ?? []).length > 0 && <><h2>Hardware faults</h2><ul>{(d.faults as Rec[]).map((f, i) => <li key={i}><span className={`badge ${f.state === 'Critical' ? 'bad' : 'warn'}`}>{f.state}</span> {f.name || f.part}</li>)}</ul></>}
      </section>
    </div>

    {canMute && !d.muted && <section className="card"><h2 style={{ marginTop: 0 }}>Maintenance</h2>
      <p className="muted">Mute the device while you work on it: no new alerts open and its open alerts close. It unmutes by itself at the end.</p>
      <div className="tools">
        <select value={muteHours} onChange={(e) => setMuteHours(e.target.value)} aria-label="Mute for"><option value="1">1 hour</option><option value="2">2 hours</option><option value="4">4 hours</option><option value="24">1 day</option><option value="168">1 week</option><option value="">Until I unmute it</option></select>
        <input placeholder="Reason (optional)" value={reason} onChange={(e) => setReason(e.target.value)} aria-label="Reason" />
        <Act label="Mute" run={async () => { await api(`/api/monitoring/devices/${id}/mute`, { method: 'POST', body: muteHours ? { hours: Number(muteHours), reason } : { reason } }); reload(); }} />
      </div></section>}
  </>;
}
