import { useState } from 'react';
import { saveFile, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Loading, ago } from '../ui';
import { Ring } from '../Rings';

const TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { success: 'ok', warning: 'warn', failed: 'bad', missed: 'bad' };
const WORD: Record<string, string> = { success: 'Success', warning: 'Warning', failed: 'Failed', missed: 'Missed', waiting: 'Waiting', ignored: 'Not watched' };

/** Backup › Reports › Backup status: every job's state now and its runs in the period, with the success rate. */
export default function Report() {
  const [days, setDays] = useState('30'); const [co, setCo] = useState('');
  const companies = useLoad<Rec[]>('/api/assets/companies'); const cos = Array.isArray(companies.data) ? companies.data : [];
  const qs = `?days=${days}&company=${encodeURIComponent(co)}`;
  const { data, error, loading } = useLoad<Rec>('/api/backup/report' + qs, [qs]);
  const items = (data?.items ?? []) as Rec[]; const s = (data?.summary ?? {}) as Rec;
  return <>
    <h1>Backup status report</h1>
    <p className="muted">Every backup job: how it stands now, and how its runs went in the period. The success rate counts warnings as successful runs.</p>
    <div className="tools">
      <select value={days} onChange={(e) => setDays(e.target.value)} aria-label="Period"><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option><option value="366">Last year</option></select>
      <select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">All customers</option><option value="none">No customer</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
      <Act label="Export (CSV)" run={() => saveFile('/api/backup/report.csv' + qs, 'backup-status-report.csv')} />
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && Number(s.jobs) > 0 && <div className="rings" style={{ marginBottom: 16 }}>
      <Ring title="Jobs now" unit="jobs" parts={[{ key: 's', label: 'Success', value: Number(s.success), tone: 'ok' }, { key: 'w', label: 'Warning', value: Number(s.warning), tone: 'warn' },
        { key: 'f', label: 'Failed', value: Number(s.failed), tone: 'bad' }, { key: 'm', label: 'Missed', value: Number(s.missed), tone: 'bad' }]} />
      <Ring title="Runs in the period" unit="runs" delay={120} parts={[{ key: 'ok', label: 'Successful', value: Number(s.runs) - Number(s.runsFailed), tone: 'ok' }, { key: 'f', label: 'Failed', value: Number(s.runsFailed), tone: 'bad' }]} />
    </div>}
    {data && (items.length === 0 ? <Empty>No backup jobs here yet.</Empty> : <div className="table-wrap"><table><thead><tr><th>Job</th><th>Customer</th><th>Device</th><th>Software</th><th>Now</th><th>Last success</th>
      <th className="num">Runs OK</th><th className="num">Failed</th><th className="num">Success rate</th></tr></thead><tbody>
      {items.map((x) => <tr key={x.id}><td><a href={`#/backup/job/${x.id}`}>{x.name}</a></td><td>{x.company}</td><td>{x.assetId ? <a href={`#/assets/${x.assetId}`}>{x.device}</a> : x.device}</td><td>{x.tool}</td>
        <td><Badge tone={TONE[String(x.status)]}>{WORD[String(x.status)] ?? x.status}</Badge></td><td>{x.lastSuccessAt ? ago(String(x.lastSuccessAt)) : <span className="muted">never</span>}</td>
        <td className="num">{Number(x.ok) + Number(x.warn)}</td><td className="num">{x.fail}</td><td className="num">{x.rate === '' ? <span className="muted">no runs</span> : x.rate + '%'}</td></tr>)}</tbody></table></div>)}
  </>;
}
