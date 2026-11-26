import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, useSubmit } from '../ui';

export const RELATION: Record<string, string> = { runs_on: 'runs on', depends_on: 'depends on', part_of: 'is part of', connected_to: 'is connected to' };
const INVERSE: Record<string, string> = { runs_on: 'runs this', depends_on: 'is needed by', part_of: 'contains', connected_to: 'is connected to' };

/** The asset's Relationships tab: what it runs on, depends on, is part of or is connected to, and what suffers if it fails. */
export function Relations({ id, canEdit }: { id: string; canEdit: boolean }) {
  const rel = useLoad<Rec>(`/api/assets/${id}/relations`); const impact = useLoad<Rec>(`/api/assets/${id}/impact`);
  const [kind, setKind] = useState('runs_on'); const [q, setQ] = useState(''); const [to, setTo] = useState('');
  const found = useLoad<Rec>(q.trim().length >= 2 ? `/api/assets?search=${encodeURIComponent(q.trim())}&take=15` : null, [q]);
  const f = useSubmit(() => { setTo(''); setQ(''); rel.reload(); impact.reload(); });
  const out = (rel.data?.outgoing ?? []) as Rec[]; const inc = (rel.data?.incoming ?? []) as Rec[];
  const choices = ((found.data?.items ?? []) as Rec[]).filter((a) => String(a.id) !== id);
  const remove = (r: Rec) => api(`/api/assets/relations/${r.id}`, { method: 'DELETE' }).then(() => { rel.reload(); impact.reload(); });
  return <>
    <Card title="Relationships">
      <Loading loading={rel.loading && !rel.data} error={rel.error} />
      {rel.data && (out.length + inc.length === 0 ? <Empty>No relationships yet. Link this asset to what it runs on or depends on, so a ticket or change shows what is affected.</Empty> : <table><tbody>
        {out.map((r) => <tr key={'o' + r.id}><td>This asset <b>{RELATION[r.kind]}</b></td><td><a href={`#/assets/${r.assetId}`}>{r.name}</a> <span className="muted">{r.tag}{r.company ? ` · ${r.company}` : ''}</span>{r.note && <div className="muted small">{r.note}</div>}</td>
          <td>{canEdit && <Act label="Remove" danger confirm="Remove this relationship?" run={() => remove(r)} />}</td></tr>)}
        {inc.map((r) => <tr key={'i' + r.id}><td><a href={`#/assets/${r.assetId}`}>{r.name}</a> <span className="muted">{r.tag}</span></td><td>{INVERSE[r.kind]} this asset{r.note && <div className="muted small">{r.note}</div>}</td>
          <td>{canEdit && <Act label="Remove" danger confirm="Remove this relationship?" run={() => remove(r)} />}</td></tr>)}
      </tbody></table>)}
      {canEdit && <div className="form" style={{ marginTop: 12 }}>
        <div className="row">
          <Field label="This asset"><select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind of relationship">{Object.entries(RELATION).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
          <Field label="Find the other asset"><input value={q} onChange={(e) => { setQ(e.target.value); setTo(''); }} placeholder="Name or tag" aria-label="Find the other asset" /></Field>
        </div>
        {q.trim().length >= 2 && <select size={Math.min(6, Math.max(2, choices.length))} value={to} onChange={(e) => setTo(e.target.value)} aria-label="Other asset">
          {choices.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.tag})</option>)}</select>}
        <Err text={f.err} />
        <div className="actions" style={{ justifyContent: 'flex-start' }}><button className="primary" disabled={f.busy || !to} onClick={() => f.submit(() => api(`/api/assets/${id}/relations`, { body: { toId: Number(to), kind } }))}>Add relationship</button></div>
      </div>}
    </Card>
    <Impact data={impact.data} loading={impact.loading} error={impact.error} title="If this asset fails" />
  </>;
}

/** The assets that suffer when one fails, nearest first, with their open tickets. */
export function Impact({ data, loading, error, title }: { data: Rec | null; loading: boolean; error: string; title: string }) {
  const items = (data?.items ?? []) as Rec[];
  if (error && !data) return null;
  if (data && items.length === 0) return <Card title={title}><p className="muted">Nothing else is linked to depend on it.</p></Card>;
  return <Card title={title}>
    <Loading loading={loading && !data} error="" />
    {items.length > 0 && <><p className="muted">{items.length} asset{items.length === 1 ? '' : 's'} would be affected.</p>
      <table><thead><tr><th>Asset</th><th>How</th><th className="num">Open tickets</th></tr></thead><tbody>
        {items.map((a) => <tr key={a.id}><td><a href={`#/assets/${a.id}`}>{a.name}</a> <span className="muted">{a.tag}{a.company ? ` · ${a.company}` : ''}</span></td>
          <td>{a.depth === 1 ? 'Directly' : `${a.depth} steps away`}</td><td className="num">{a.openTickets > 0 ? <Badge tone="warn">{a.openTickets}</Badge> : '–'}</td></tr>)}
      </tbody></table></>}
  </Card>;
}
