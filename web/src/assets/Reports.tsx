import { useState } from 'react';
import { saveFile, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Loading } from '../ui';
import { Ring } from '../Rings';

/** The patch and compliance report: every computer with an agent, oldest patches first, for one customer or all. */
export default function Reports() {
  const [co, setCo] = useState(''); const [show, setShow] = useState('all');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const qs = `?company=${encodeURIComponent(co)}&show=${show}`;
  const { data, error, loading } = useLoad<Rec>('/api/assets/reports/patches' + qs);
  const items = (data?.items ?? []) as Rec[]; const list = Array.isArray(companies.data) ? companies.data : [];
  const total = Number(data?.total ?? 0);
  return <>
    <h1>Patch and compliance report</h1>
    <p className="muted">Computers that report through an agent. Compliant means antivirus on and patches installed in the last 30 days.</p>
    <div className="tools">
      <select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">All customers</option><option value="none">No customer</option>{list.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
      <select value={show} onChange={(e) => setShow(e.target.value)} aria-label="Show"><option value="all">All computers</option><option value="behind">Patches older than 30 days</option><option value="noncompliant">Not compliant</option></select>
      <Act label="Export (CSV)" run={() => saveFile('/api/assets/reports/patches.csv' + qs, 'patch-report.csv')} />
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && show === 'all' && total > 0 && <div className="rings" style={{ marginBottom: 16 }}>
      <Ring title="Patches" unit="computers" parts={[{ key: 'ok', label: 'Last 30 days', value: total - Number(data.behind), tone: 'ok' }, { key: 'old', label: 'Older or unknown', value: Number(data.behind), tone: 'bad' }]} />
      <Ring title="Compliance" unit="computers" delay={120} parts={[{ key: 'c', label: 'Compliant', value: Number(data.compliant), tone: 'ok' }, { key: 'n', label: 'Not compliant', value: Number(data.nonCompliant), tone: 'bad' },
        { key: 'u', label: 'No data yet', value: total - Number(data.compliant) - Number(data.nonCompliant), tone: 'none' }]} />
    </div>}
    {data && (items.length === 0 ? <Empty>No computers here.</Empty> : <table><thead><tr><th>Computer</th><th>Customer</th><th>Operating system</th><th>Last patch</th><th className="num">Days</th><th>Antivirus</th><th>Compliance</th></tr></thead><tbody>
      {items.map((x) => <tr key={x.id}><td><a href={`#/assets/${x.id}`}>{x.name}</a><div className="muted mono">{x.tag}</div></td><td>{x.company}</td><td>{x.os}</td><td>{x.lastPatch || <span className="muted">unknown</span>}</td>
        <td className="num">{x.days ?? ''}</td><td>{x.antivirus}</td><td><Badge tone={x.compliance === 'Compliant' ? 'ok' : x.compliance === 'Not compliant' ? 'bad' : undefined}>{x.compliance}</Badge></td></tr>)}</tbody></table>)}
    {total > items.length && <p className="muted">Showing {items.length} of {total}. The export has all of them.</p>}
  </>;
}
