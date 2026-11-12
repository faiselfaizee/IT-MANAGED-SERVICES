import { useState } from 'react';
import { saveFile, useLoad, type Rec } from '../http';
import { Card, Empty, Err, Loading, fmtDate, useSubmit } from '../ui';
import { PRIORITY } from './List';

const thisMonth = () => new Date().toISOString().slice(0, 7);
const hours = (h?: number | null) => (h == null ? 'No data' : h < 48 ? `${h} h` : `${Math.round(h / 24)} days`);
const pct = (p?: number | null) => (p == null ? 'No data' : `${p}%`);

/** Monthly service report for one company (or all). The PDF is made by the server. */
export default function Reports({ staff }: { staff: boolean }) {
  const [month, setMonth] = useState(thisMonth()); const [company, setCompany] = useState('');
  const companies = useLoad<Rec[]>(staff ? '/api/assets/companies' : null); const cos = Array.isArray(companies.data) ? companies.data : [];
  const r = useLoad<Rec>(`/api/servicedesk/reports/monthly?month=${month}${company ? `&companyId=${company}` : ''}`);
  const pdf = useSubmit(() => undefined);
  const d = r.data; const t = d?.totals as Rec | undefined;
  return <div className="report">
    <h1>Monthly report</h1>
    <div className="tools no-print">
      <label>Month <input type="month" value={month} max={thisMonth()} onChange={(e) => e.target.value && setMonth(e.target.value)} /></label>
      {staff && <label>Company <select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">All companies</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>}
      <button className="primary" onClick={() => pdf.submit(() => saveFile(`/api/servicedesk/reports/monthly.pdf?month=${month}${company ? `&companyId=${company}` : ''}`, `service-report-${month}.pdf`))} disabled={!d || pdf.busy}>Download PDF</button>
      <button onClick={() => window.print()} disabled={!d}>Print</button>
    </div>
    <Err text={pdf.err} />
    <Loading loading={r.loading && !d} error={r.error} />
    {d && t && <>
      <h2>{d.company} · {new Date(d.from).toLocaleString(undefined, { month: 'long', year: 'numeric', timeZone: 'UTC' })}</h2>
      <div className="tiles">
        {[['Tickets opened', t.opened], ['Tickets solved', t.resolved], ['Open at start', t.backlogStart], ['Open at end', t.backlogEnd], ['Average first reply', hours(t.avgFirstResponseHours)], ['Average time to solve', hours(t.avgResolutionHours)], ['Replied in time', pct(t.respondMetPercent)], ['Solved in time', pct(t.resolveMetPercent)]]
          .map(([k, v]) => <div key={String(k)} className="tile"><div className="muted">{k}</div><b style={{ fontSize: 22 }}>{String(v)}</b></div>)}
      </div>
      {d.changes && <p className="muted">Changes finished this month: {d.changes.completed} completed, {d.changes.failed} failed.</p>}
      <div className="grid2">
        <Breakdown title="By priority" rows={(d.byPriority as Rec[]).map((x) => ({ ...x, name: PRIORITY[x.name] ?? x.name }))} />
        <Breakdown title="By category" rows={d.byCategory} />
        <Breakdown title="How tickets arrived" rows={d.bySource} />
        <Breakdown title="Most active people" rows={d.byRequester} />
        <Breakdown title="Devices with most tickets" rows={d.byAsset} />
        {staff && !d.companyId && <Breakdown title="By company" rows={d.byCompany} />}
      </div>
      <Card title="Tickets opened this month">
        {d.tickets.length === 0 ? <Empty>No tickets were opened this month.</Empty> : <table><thead><tr><th>Ticket</th><th>Subject</th><th>Priority</th><th>Status</th><th>Opened</th><th>Solved</th></tr></thead><tbody>
          {(d.tickets as Rec[]).map((x) => <tr key={x.id}><td className="mono">{x.number}</td><td>{x.subject}<div className="muted">{x.requester}</div></td><td>{PRIORITY[x.priority] ?? x.priority}</td><td>{x.status}</td><td>{fmtDate(x.createdAt)}</td><td>{x.doneAt ? fmtDate(x.doneAt) : ''}</td></tr>)}</tbody></table>}
        {d.truncated && <p className="muted">Only the first 300 tickets are listed.</p>}
      </Card>
    </>}
  </div>;
}

function Breakdown({ title, rows }: { title: string; rows: Rec[] }) {
  return <Card title={title}>{rows.length === 0 ? <p className="muted">Nothing this month.</p> : <table><tbody>{rows.map((x) => <tr key={x.name}><td>{x.name}</td><td style={{ textAlign: 'right' }}>{x.count}</td></tr>)}</tbody></table>}</Card>;
}
