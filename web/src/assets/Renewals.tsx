import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDay, useSubmit } from '../ui';
import { usePerms } from '../perms';

const KIND: Record<string, string> = { warranty: 'Warranty', amc: 'AMC contract', rental: 'Rental' };

/** What ends soon, and who is reminded about it. */
export default function Renewals() {
  const cfg = useLoad<Rec>('/api/assets/renewals/settings'); const up = useLoad<Rec>('/api/assets/renewals/upcoming?days=90');
  const [v, setV] = useState<{ enabled: boolean; days: string; to: string; customers: boolean; users: boolean; vendors: boolean; customerAmc: boolean } | null>(null); const [saved, setSaved] = useState(false);
  const perms = usePerms('assets'); const ch = (patch: Partial<NonNullable<typeof v>>) => { setSaved(false); setV({ ...v!, ...patch }); };
  const f = useSubmit(() => { setSaved(true); cfg.reload(); });
  useEffect(() => { if (cfg.data) setV({ enabled: !!cfg.data.enabled, days: (cfg.data.days as number[]).join(', '), to: cfg.data.to ?? '', customers: cfg.data.customers !== false, users: cfg.data.users !== false, vendors: cfg.data.vendors !== false, customerAmc: cfg.data.customerAmc === true }); }, [cfg.data]);
  const items = (up.data?.items ?? []) as Rec[];
  return <>
    <h1>Renewals</h1>
    {perms.admin && <Card title="Reminders">
      <Loading loading={!v} error={cfg.error} />
      {v && <div className="form">
        <label className="check"><input type="checkbox" checked={v.enabled} onChange={(e) => ch({ enabled: e.target.checked })} /> Send reminders when a warranty, AMC contract or rental is about to end</label>
        <Field label="Days before the end date (one month and 15 days: 30, 15)"><input value={v.days} onChange={(e) => ch({ days: e.target.value })} /></Field>
        <Field label="Your team (every reminder; separate addresses with commas)"><input value={v.to} onChange={(e) => ch({ to: e.target.value })} placeholder="it@yourcompany.com" /></Field>
        <p className="muted small">Each customer's own team (Companies › Edit › "Your team for this customer") also gets that customer's reminders.</p>
        <label className="check"><input type="checkbox" checked={v.customers} onChange={(e) => ch({ customers: e.target.checked })} /> The customer's contact, about their own renewals</label>
        <label className="check"><input type="checkbox" checked={v.users} onChange={(e) => ch({ users: e.target.checked })} /> The person an asset is assigned to, about its warranty (when "Assigned to" holds an email or the name of a DeskAsk user)</label>
        <label className="check"><input type="checkbox" checked={v.vendors} onChange={(e) => ch({ vendors: e.target.checked })} /> The vendor, about warranties and AMC contracts bought from them, asking for a renewal quotation (unless the vendor is set not to be mailed)</label>
        <label className="check"><input type="checkbox" checked={v.customerAmc} onChange={(e) => ch({ customerAmc: e.target.checked })} /> Let a customer administrator add, change and renew the AMC contracts of their own company (optional; off means they can only see their renewals)</label>
        <p className="muted">Mail must be set up first (Mail page). Each reminder goes out once for each end date and each of the days above. The wording is under Mail › Templates.</p>
        <Err text={f.err} />
        <div className="actions">{saved && <span className="muted">Saved.</span>}
          <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/assets/renewals/settings', { method: 'PUT', body: { enabled: v.enabled, days: v.days.split(/[ ,;]+/).filter(Boolean).map(Number).filter((n) => Number.isFinite(n)), to: v.to, customers: v.customers, users: v.users, vendors: v.vendors, customerAmc: v.customerAmc } }))}>Save</button>
          <Act label="Check now" run={async () => { await api('/api/assets/renewals/run', { method: 'POST', body: {} }); }} /></div>
      </div>}
    </Card>}
    <Card title="Ending in the next 90 days">
      <Loading loading={up.loading && !up.data} error={up.error} />
      {up.data && (items.length === 0 ? <Empty>Nothing ends in the next 90 days.</Empty> : <table><thead><tr><th>What</th><th>Customer</th><th>Vendor</th><th>Ends</th><th>Reminders sent</th></tr></thead><tbody>
        {items.map((x) => <tr key={x.kind + x.id}><td>{KIND[x.kind] ?? x.kind}: {x.kind === 'warranty' ? <a href={`#/assets/${x.id}`}>{x.title}</a> : x.title}{x.detail && <div className="muted small">{x.detail}</div>}</td>
          <td>{x.company}{x.site && <div className="muted small">{x.site}</div>}</td><td>{x.vendorId ? <a href={`#/assets/vendors/${x.vendorId}`}>{x.vendor}</a> : <span className="muted">–</span>}</td>
          <td>{fmtDay(x.due)} <Badge tone={x.daysLeft <= 7 ? 'bad' : x.daysLeft <= 30 ? 'warn' : undefined}>{x.daysLeft === 0 ? 'today' : `${x.daysLeft} days`}</Badge></td>
          <td>{((x.remindersSent ?? []) as number[]).length ? [...(x.remindersSent as number[])].sort((a, b) => b - a).map((d) => `${d}-day`).join(', ') : <span className="muted">not yet</span>}</td></tr>)}</tbody></table>)}
    </Card>
  </>;
}
