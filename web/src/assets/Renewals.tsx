import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDay, useSubmit } from '../ui';

const KIND: Record<string, string> = { warranty: 'Warranty', amc: 'AMC contract', rental: 'Rental' };

/** What ends soon, and who is reminded about it. */
export default function Renewals() {
  const cfg = useLoad<Rec>('/api/assets/renewals/settings'); const up = useLoad<Rec>('/api/assets/renewals/upcoming?days=90');
  const [v, setV] = useState<{ enabled: boolean; days: string; to: string; customers: boolean } | null>(null); const [saved, setSaved] = useState(false);
  const f = useSubmit(() => { setSaved(true); cfg.reload(); });
  useEffect(() => { if (cfg.data) setV({ enabled: !!cfg.data.enabled, days: (cfg.data.days as number[]).join(', '), to: cfg.data.to ?? '', customers: cfg.data.customers !== false }); }, [cfg.data]);
  const items = (up.data?.items ?? []) as Rec[];
  return <>
    <h1>Renewals</h1>
    <Card title="Reminders">
      <Loading loading={!v} error={cfg.error} />
      {v && <div className="form">
        <label><input type="checkbox" checked={v.enabled} onChange={(e) => { setSaved(false); setV({ ...v, enabled: e.target.checked }); }} /> Send reminders when a warranty, AMC contract or rental is about to end</label>
        <Field label="Days before the end date (for example 30, 7)"><input value={v.days} onChange={(e) => { setSaved(false); setV({ ...v, days: e.target.value }); }} /></Field>
        <Field label="Your own addresses (separate with commas)"><input value={v.to} onChange={(e) => { setSaved(false); setV({ ...v, to: e.target.value }); }} placeholder="it@yourcompany.com" /></Field>
        <label><input type="checkbox" checked={v.customers} onChange={(e) => { setSaved(false); setV({ ...v, customers: e.target.checked }); }} /> Also tell each customer's contact about their own renewals</label>
        <p className="muted">Mail must be set up first (Mail page). Each reminder is sent once for each end date.</p>
        <Err text={f.err} />
        <div className="actions">{saved && <span className="muted">Saved.</span>}
          <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api('/api/assets/renewals/settings', { method: 'PUT', body: { enabled: v.enabled, days: v.days.split(/[ ,;]+/).filter(Boolean).map(Number).filter((n) => Number.isFinite(n)), to: v.to, customers: v.customers } }))}>Save</button>
          <Act label="Check now" run={async () => { await api('/api/assets/renewals/run', { method: 'POST', body: {} }); }} /></div>
      </div>}
    </Card>
    <Card title="Ending in the next 90 days">
      <Loading loading={up.loading && !up.data} error={up.error} />
      {up.data && (items.length === 0 ? <Empty>Nothing ends in the next 90 days.</Empty> : <table><thead><tr><th>What</th><th>Customer</th><th>Ends</th></tr></thead><tbody>
        {items.map((x) => <tr key={x.kind + x.id}><td>{KIND[x.kind] ?? x.kind}: {x.title}</td><td>{x.company}</td><td>{fmtDay(x.due)} <Badge tone={x.daysLeft <= 7 ? 'bad' : x.daysLeft <= 30 ? 'warn' : undefined}>{x.daysLeft === 0 ? 'today' : `${x.daysLeft} days`}</Badge></td></tr>)}</tbody></table>)}
    </Card>
  </>;
}
