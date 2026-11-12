import { useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDate, useSubmit } from '../ui';
import { PRIORITY, PriorityBadge } from './List';

export const PROBLEM_STATUS: Record<string, string> = { open: 'Open', investigating: 'Investigating', known_error: 'Known error', resolved: 'Resolved', closed: 'Closed' };
export const ProblemBadge = ({ s }: { s: string }) => <Badge tone={s === 'resolved' || s === 'closed' ? 'ok' : s === 'known_error' ? 'warn' : undefined}>{PROBLEM_STATUS[s] ?? s}</Badge>;
const idsFrom = (t: string) => t.split(/[\s,]+/).map((x) => Number(x.replace(/\D/g, ''))).filter((n) => n > 0);

export function ProblemList({ staff }: { staff: boolean }) {
  const [status, setStatus] = useState('open'); const [text, setText] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/servicedesk/problems?status=${status === 'all' ? '' : status}&q=${encodeURIComponent(text)}`);
  const sug = useLoad<Rec>(staff ? '/api/servicedesk/problems/suggestions' : null);
  const items = (data?.items ?? []) as Rec[]; const hints = (sug.data?.items ?? []) as Rec[];
  const raise = async (h: Rec) => { const r = await api<Rec>('/api/servicedesk/problems', { body: { title: `Repeated ${h.category} tickets`, category: h.category, tickets: h.tickets } }); go('servicedesk/problems/' + r.id); };
  return <>
    <h1>Problems</h1>
    {hints.length > 0 && <Card title="Worth investigating">{hints.map((h) => <p key={h.category}>{h.count} tickets in the last 30 days are about <strong>{h.category}</strong> and not tied to a problem. <Act label="Raise a problem" run={() => raise(h)} /></p>)}</Card>}
    <div className="tools">
      <input placeholder="Search number, title, category…" value={text} onChange={(e) => setText(e.target.value)} style={{ minWidth: 240 }} aria-label="Search" />
      <select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status"><option value="open">Not finished</option><option value="all">Any status</option>{Object.entries(PROBLEM_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      {staff && <button className="primary" onClick={() => go('servicedesk/problems/new')}>New problem</button>}
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No problems match.</Empty> : <table><thead><tr><th>Problem</th><th>Title</th><th>Priority</th><th>Status</th><th>Tickets</th><th>Updated</th></tr></thead><tbody>
      {items.map(({ problem: p, tickets }) => <tr key={p.id} onClick={() => go('servicedesk/problems/' + p.id)} style={{ cursor: 'pointer' }}>
        <td><a href={'#/servicedesk/problems/' + p.id}>{p.number}</a></td><td>{p.title}<div className="muted">{[p.category, p.ownerName].filter(Boolean).join(' · ')}</div></td>
        <td><PriorityBadge p={p.priority} /></td><td><ProblemBadge s={p.status} /></td><td>{tickets}</td><td className="muted">{fmtDate(p.updatedAt)}</td>
      </tr>)}</tbody></table>)}
  </>;
}

export function NewProblem() {
  const settings = useLoad<Rec>('/api/servicedesk/settings');
  const [v, setV] = useState<Rec>({ title: '', description: '', priority: 'medium', category: '', tickets: '' });
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const f = useSubmit(() => undefined); const cats = (settings.data?.categories ?? []) as string[];
  return <>
    <h1>New problem</h1>
    <Card><div className="form">
      <Field label="Title"><input value={v.title} onChange={(e) => set('title', e.target.value)} maxLength={200} autoFocus /></Field>
      <Field label="What keeps going wrong?"><textarea rows={4} value={v.description} onChange={(e) => set('description', e.target.value)} /></Field>
      <div className="row">
        <Field label="Priority"><select value={v.priority} onChange={(e) => set('priority', e.target.value)}>{Object.entries(PRIORITY).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <Field label="Category"><select value={v.category} onChange={(e) => set('category', e.target.value)}><option value="">None</option>{cats.map((c) => <option key={c}>{c}</option>)}</select></Field>
      </div>
      <Field label="Related tickets (ids, separated by commas)"><input value={v.tickets} onChange={(e) => set('tickets', e.target.value)} /></Field>
      <Err text={f.err} />
      <div className="actions"><button onClick={() => go('servicedesk/problems')}>Cancel</button>
        <button className="primary" disabled={f.busy || !v.title.trim()} onClick={() => f.submit(async () => {
          const r = await api<Rec>('/api/servicedesk/problems', { body: { title: v.title, description: v.description, priority: v.priority, category: v.category, tickets: idsFrom(v.tickets) } }); go('servicedesk/problems/' + r.id);
        })}>Raise problem</button></div>
    </div></Card>
  </>;
}

export function ProblemDetail({ id, staff }: { id: string; staff: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/problems/' + id);
  const [edit, setEdit] = useState<Rec | null>(null); const [note, setNote] = useState(''); const [msg, setMsg] = useState(''); const [links, setLinks] = useState<string | null>(null);
  if (!data) return <Loading loading={loading} error={error} />;
  const p = data.problem as Rec; const notes = (data.notes ?? []) as Rec[]; const tickets = (data.tickets ?? []) as Rec[]; const changes = (data.changes ?? []) as Rec[];
  const patch = async (body: Rec) => { setMsg(''); try { await api('/api/servicedesk/problems/' + id, { method: 'PATCH', body }); reload(); return true; } catch (e) { setMsg((e as Error).message); return false; } };
  const e = edit ?? { rootCause: p.rootCause, workaround: p.workaround, resolution: p.resolution };
  return <>
    <p><a href="#/servicedesk/problems">← All problems</a></p>
    <h1>{p.number} · {p.title} <ProblemBadge s={p.status} /> <PriorityBadge p={p.priority} /></h1>
    <p className="muted">{[p.category, p.ownerName && 'Owner: ' + p.ownerName].filter(Boolean).join(' · ')}</p>
    <Err text={msg} />
    {p.description && <Card title="What keeps going wrong"><p>{p.description}</p></Card>}
    {staff && <div className="actions" style={{ justifyContent: 'flex-start', margin: '12px 0' }}>
      {Object.entries(PROBLEM_STATUS).map(([k, n]) => <Act key={k} label={n} primary={k === p.status} disabled={k === p.status} run={async () => { if (edit) await patch({ ...edit }); await patch({ status: k }); setEdit(null); }} />)}
    </div>}
    <Card title="Findings">
      {staff ? <div className="form">
        <Field label="Root cause"><textarea rows={3} value={e.rootCause} onChange={(x) => setEdit({ ...e, rootCause: x.target.value })} /></Field>
        <Field label="Workaround (what people can do meanwhile)"><textarea rows={3} value={e.workaround} onChange={(x) => setEdit({ ...e, workaround: x.target.value })} /></Field>
        <Field label="Resolution"><textarea rows={3} value={e.resolution} onChange={(x) => setEdit({ ...e, resolution: x.target.value })} /></Field>
        <div className="actions"><button className="primary" disabled={!edit} onClick={async () => { if (await patch({ ...edit })) setEdit(null); }}>Save findings</button></div>
      </div> : <><p><strong>Root cause:</strong> {p.rootCause || '–'}</p><p><strong>Workaround:</strong> {p.workaround || '–'}</p><p><strong>Resolution:</strong> {p.resolution || '–'}</p></>}
    </Card>
    <Card title="Linked">
      <p>Tickets: {tickets.length === 0 ? <span className="muted">none</span> : tickets.map((t) => <a key={t.id} href={'#/servicedesk/' + t.id} style={{ marginRight: 10 }}>{t.number}</a>)}</p>
      <p>Changes: {changes.length === 0 ? <span className="muted">none</span> : changes.map((c) => <a key={c.id} href={'#/servicedesk/changes/' + c.id} style={{ marginRight: 10 }}>{c.number}</a>)}</p>
      {staff && (links === null ? <button onClick={() => setLinks(tickets.map((t) => t.id).join(', '))}>Change linked tickets</button>
        : <div className="row"><Field label="Ticket ids"><input value={links} onChange={(x) => setLinks(x.target.value)} /></Field><button className="primary" onClick={async () => { if (await patch({ tickets: idsFrom(links) })) setLinks(null); }}>Save links</button></div>)}
    </Card>
    <Card title="Notes">
      {notes.map((n) => <p key={n.id}><strong>{n.author}</strong> <span className="muted">{fmtDate(n.at)}</span><br />{n.body}</p>)}
      {staff && <div className="form"><Field label="Add a note"><textarea rows={3} value={note} onChange={(x) => setNote(x.target.value)} /></Field>
        <div className="actions"><Act label="Add note" primary disabled={!note.trim()} run={async () => { await api(`/api/servicedesk/problems/${id}/notes`, { body: { body: note } }); setNote(''); reload(); }} /></div></div>}
    </Card>
  </>;
}
