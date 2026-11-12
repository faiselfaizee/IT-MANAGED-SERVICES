import { useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Modal, fmtDate, useSubmit } from '../ui';

export const CHANGE_STATUS: Record<string, string> = { draft: 'Draft', review: 'Awaiting approval', approved: 'Approved', rejected: 'Rejected', scheduled: 'Scheduled', implementing: 'In progress', completed: 'Completed', failed: 'Failed', closed: 'Closed', cancelled: 'Cancelled' };
export const CHANGE_TYPE: Record<string, string> = { normal: 'Normal', standard: 'Standard (pre-approved)', emergency: 'Emergency' };
const LEVEL: Record<string, string> = { low: 'Low', medium: 'Medium', high: 'High' };
const tone = (s: string): 'ok' | 'warn' | 'bad' | undefined => (s === 'completed' || s === 'approved' ? 'ok' : s === 'review' || s === 'scheduled' || s === 'implementing' ? 'warn' : s === 'rejected' || s === 'failed' ? 'bad' : undefined);
export const ChangeBadge = ({ s }: { s: string }) => <Badge tone={tone(s)}>{CHANGE_STATUS[s] ?? s}</Badge>;
const local = (s?: string | null) => (s ? new Date(s).toISOString().slice(0, 16) : '');
export const STAGES: [string, string][] = [['submission', 'Submission'], ['planning', 'Planning'], ['approval', 'Approval'], ['implementation', 'Implementation'], ['review', 'Review'], ['close', 'Close']];
export const CLOSURE: Record<string, string> = { successful: 'Successful', successful_with_issues: 'Successful with issues', unsuccessful: 'Unsuccessful', rolled_back: 'Rolled back' };
/** Which of the six stages a change is in; a draft has been created (submission) and is being planned. */
const stageIndex = (stage: string) => Math.max(1, STAGES.findIndex(([k]) => k === stage));
function Stepper({ stage, status }: { stage: string; status: string }) {
  const at = stageIndex(stage); const stopped = status === 'cancelled' || status === 'rejected';
  return <ol className="stages" aria-label="Change stages">{STAGES.map(([k, n], i) => <li key={k} className={i < at || (status === 'closed') ? 'done' : i === at ? (stopped ? 'stop' : 'now') : ''} aria-current={i === at ? 'step' : undefined}>{n}</li>)}</ol>;
}
const idsFrom = (t: string) => t.split(/[\s,]+/).map((x) => Number(x.replace(/\D/g, ''))).filter((n) => n > 0);

export function ChangeList({ staff }: { staff: boolean }) {
  const [status, setStatus] = useState('open'); const [type, setType] = useState(''); const [text, setText] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/servicedesk/changes?status=${status === 'all' ? '' : status}&type=${type}&q=${encodeURIComponent(text)}`);
  const items = (data?.items ?? []) as Rec[];
  return <>
    <h1>Changes</h1>
    <div className="tools">
      <input placeholder="Search number, title, company…" value={text} onChange={(e) => setText(e.target.value)} style={{ minWidth: 240 }} aria-label="Search" />
      <select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status"><option value="open">Not finished</option><option value="all">Any status</option>{Object.entries(CHANGE_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      <select value={type} onChange={(e) => setType(e.target.value)} aria-label="Type"><option value="">Any type</option>{Object.entries(CHANGE_TYPE).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select>
      {staff && <button className="primary" onClick={() => go('servicedesk/changes/new')}>New change</button>}
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No changes match.</Empty> : <table><thead><tr><th>Change</th><th>Title</th><th>Type</th><th>Risk</th><th>Status</th><th>Window</th><th>Approvals</th></tr></thead><tbody>
      {items.map(({ change: c, approvals }) => <tr key={c.id} onClick={() => go('servicedesk/changes/' + c.id)} style={{ cursor: 'pointer' }}>
        <td><a href={'#/servicedesk/changes/' + c.id}>{c.number}</a></td><td>{c.title}<div className="muted">{[c.companyName, c.ownerName].filter(Boolean).join(' · ')}</div></td>
        <td>{CHANGE_TYPE[c.type]?.split(' ')[0]}</td><td>{LEVEL[c.risk]}</td><td><ChangeBadge s={c.status} /></td>
        <td className="muted">{c.startAt ? fmtDate(c.startAt) : '–'}</td><td>{approvals} of {c.quorum}</td>
      </tr>)}</tbody></table>)}
  </>;
}

export function NewChange() {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [v, setV] = useState<Rec>({ title: '', type: 'normal', risk: 'low', impact: 'low', reason: '', description: '', plan: '', rollback: '', companyId: '', startAt: '', endAt: '', tickets: '' });
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const f = useSubmit(() => undefined); const cos = Array.isArray(companies.data) ? companies.data : [];
  return <>
    <h1>New change</h1>
    <Card><div className="form">
      <Field label="Title"><input value={v.title} onChange={(e) => set('title', e.target.value)} maxLength={200} autoFocus /></Field>
      <div className="row">
        <Field label="Type"><select value={v.type} onChange={(e) => set('type', e.target.value)}>{Object.entries(CHANGE_TYPE).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <Field label="Risk"><select value={v.risk} onChange={(e) => set('risk', e.target.value)}>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <Field label="Impact"><select value={v.impact} onChange={(e) => set('impact', e.target.value)}>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
      </div>
      <Field label="Company"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">None</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <Field label="Why is this needed?"><textarea rows={3} value={v.reason} onChange={(e) => set('reason', e.target.value)} /></Field>
      <Field label="What will change?"><textarea rows={3} value={v.description} onChange={(e) => set('description', e.target.value)} /></Field>
      <Field label="Implementation plan"><textarea rows={4} value={v.plan} onChange={(e) => set('plan', e.target.value)} /></Field>
      <Field label="Rollback plan"><textarea rows={3} value={v.rollback} onChange={(e) => set('rollback', e.target.value)} /></Field>
      <div className="row"><Field label="Window starts"><input type="datetime-local" value={v.startAt} onChange={(e) => set('startAt', e.target.value)} /></Field><Field label="Window ends"><input type="datetime-local" value={v.endAt} onChange={(e) => set('endAt', e.target.value)} /></Field></div>
      <Field label="Related tickets (ids, separated by commas)"><input value={v.tickets} onChange={(e) => set('tickets', e.target.value)} /></Field>
      <Err text={f.err} />
      <div className="actions"><button onClick={() => go('servicedesk/changes')}>Cancel</button>
        <button className="primary" disabled={f.busy || !v.title.trim()} onClick={() => f.submit(async () => {
          const body: Rec = { title: v.title, type: v.type, risk: v.risk, impact: v.impact, reason: v.reason, description: v.description, plan: v.plan, rollback: v.rollback, tickets: idsFrom(v.tickets) };
          if (v.companyId) body.companyId = Number(v.companyId); if (v.startAt) body.startAt = new Date(v.startAt).toISOString(); if (v.endAt) body.endAt = new Date(v.endAt).toISOString();
          const r = await api<Rec>('/api/servicedesk/changes', { body }); go('servicedesk/changes/' + r.id);
        })}>Save draft</button></div>
    </div></Card>
  </>;
}

function Ask({ title, label, need, onClose, onSend }: { title: string; label: string; need: boolean; onClose: () => void; onSend: (t: string) => Promise<unknown> }) {
  const [t, setT] = useState(''); const f = useSubmit(onClose);
  return <Modal title={title} onClose={onClose}><div className="form">
    <Field label={label}><textarea rows={4} value={t} onChange={(e) => setT(e.target.value)} autoFocus /></Field><Err text={f.err} />
    <div className="actions"><button onClick={onClose}>Cancel</button><button className="primary" disabled={f.busy || (need && !t.trim())} onClick={() => f.submit(() => onSend(t))}>Save</button></div>
  </div></Modal>;
}

function Tasks({ id, stage, rows, staff, locked, reload, people, title, hint }: { id: string; stage: 'planning' | 'implementation'; rows: Rec[]; staff: boolean; locked: boolean; reload: () => void; people: Rec[]; title: string; hint: string }) {
  const mine = rows.filter((x) => x.stage === stage); const [text, setText] = useState(''); const [who, setWho] = useState(''); const f = useSubmit(() => undefined);
  const call = (path: string, method: string, body?: unknown) => f.submit(async () => { await api(`/api/servicedesk/changes/${id}/tasks${path}`, { method, body: body ?? {} }); reload(); });
  return <Card title={title}>
    {mine.length === 0 ? <Empty>{hint}</Empty> : <table><tbody>{mine.map((x) => <tr key={x.id}>
      <td style={{ width: 28 }}><input type="checkbox" checked={x.done} disabled={!staff || locked} aria-label={'Done: ' + x.title} onChange={(e) => call('/' + x.id, 'PATCH', { done: e.target.checked })} /></td>
      <td style={x.done ? { textDecoration: 'line-through', opacity: 0.7 } : undefined}>{x.title}</td><td className="muted">{x.assignee}</td>
      <td style={{ textAlign: 'right' }}>{staff && !locked && <button onClick={() => call('/' + x.id, 'DELETE')} aria-label={'Remove ' + x.title}>Remove</button>}</td></tr>)}</tbody></table>}
    {staff && !locked && <div className="row" style={{ marginTop: 10 }}>
      <Field label="New task"><input value={text} onChange={(e) => setText(e.target.value)} maxLength={300} /></Field>
      {stage === 'implementation' && <Field label="Assigned to"><select value={who} onChange={(e) => setWho(e.target.value)}><option value="">Nobody</option>{people.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select></Field>}
      <button className="primary" disabled={f.busy || !text.trim()} onClick={() => call('', 'POST', { title: text, stage, assigneeId: who || null }).then(() => { setText(''); setWho(''); })}>Add task</button>
    </div>}
    <Err text={f.err} />
  </Card>;
}

export function ChangeDetail({ id, staff, admin, myId }: { id: string; staff: boolean; admin: boolean; myId?: string }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/changes/' + id);
  const [ask, setAsk] = useState<'' | 'reject' | 'done' | 'failed'>(''); const [note, setNote] = useState(''); const [win, setWin] = useState<Rec | null>(null); const [links, setLinks] = useState<string | null>(null);
  const [msg, setMsg] = useState(''); const [plan, setPlan] = useState<Rec | null>(null); const [review, setReview] = useState<string | null>(null); const [closure, setClosure] = useState('');
  const people = useLoad<Rec[]>(staff ? '/api/servicedesk/staff' : null);
  if (!data) return <Loading loading={loading} error={error} />;
  const c = data.change as Rec; const votes = (data.votes ?? []) as Rec[]; const notes = (data.notes ?? []) as Rec[]; const tickets = (data.tickets ?? []) as Rec[]; const problems = (data.problems ?? []) as Rec[]; const tasks = (data.tasks ?? []) as Rec[];
  const post = async (path: string, body?: unknown) => { setMsg(''); try { await api(`/api/servicedesk/changes/${id}/${path}`, { body: body ?? {} }); reload(); } catch (e) { setMsg((e as Error).message); throw e; } };
  const patch = async (body: Rec) => { setMsg(''); try { await api('/api/servicedesk/changes/' + id, { method: 'PATCH', body }); reload(); } catch (e) { setMsg((e as Error).message); } };
  const planning = ['draft', 'rejected', 'review'].includes(c.status); const mine = votes.find((x) => x.userId === myId); const approvals = votes.filter((x) => x.decision === 'approve').length;
  const canVote = staff && c.status === 'review' && c.ownerId !== myId && (c.type !== 'emergency' || admin);
  const w = win ?? { startAt: local(c.startAt), endAt: local(c.endAt) };
  return <>
    <p><a href="#/servicedesk/changes">← All changes</a></p>
    <h1>{c.number} · {c.title} <ChangeBadge s={c.status} /></h1>
    <Stepper stage={c.stage ?? 'planning'} status={c.status} />
    <Err text={msg} />
    <Card><Dl rows={[['Type', CHANGE_TYPE[c.type]], ['Risk', LEVEL[c.risk]], ['Impact', LEVEL[c.impact]], ['Company', c.companyName], ['Owner', c.ownerName], ['Window', c.startAt ? `${fmtDate(c.startAt)} to ${fmtDate(c.endAt)}` : ''], ['Approvals', c.type === 'standard' ? 'Pre-approved' : `${approvals} of ${c.quorum} needed`]]} /></Card>
    {staff && <div className="actions" style={{ justifyContent: 'flex-start', margin: '12px 0' }}>
      {['draft', 'rejected'].includes(c.status) && <Act label="Submit for approval" primary run={() => post('submit')} />}
      {canVote && <Act label={mine?.decision === 'approve' ? 'Approved by you' : 'Approve'} primary disabled={mine?.decision === 'approve'} run={() => post('vote', { decision: 'approve' })} />}
      {canVote && <button onClick={() => setAsk('reject')}>Reject</button>}
      {c.status === 'approved' && <Act label="Schedule" disabled={!c.startAt || !c.endAt} run={() => post('status', { status: 'scheduled' })} />}
      {['approved', 'scheduled'].includes(c.status) && <Act label="Start work" primary run={() => post('status', { status: 'implementing' })} />}
      {c.status === 'implementing' && <button className="primary" onClick={() => setAsk('done')}>Mark completed</button>}
      {c.status === 'implementing' && <button onClick={() => setAsk('failed')}>Mark failed</button>}
      {['completed', 'failed'].includes(c.status) && <Act label="Close change" primary disabled={!closure || !(review ?? c.reviewNotes ?? '').trim()} run={() => post('status', { status: 'closed', closureCode: closure, reviewNotes: (review ?? c.reviewNotes ?? '').trim() })} />}
      {['draft', 'review', 'rejected', 'approved', 'scheduled'].includes(c.status) && <Act label="Cancel change" confirm="Cancel this change?" run={() => post('status', { status: 'cancelled' })} />}
    </div>}
    {c.status === 'approved' && staff && (!c.startAt || !c.endAt) && <p className="muted">Set the change window below before scheduling.</p>}
    <Card title="Plan">
      {staff && planning ? <div className="form">
        {([['reason', 'Why is this needed?'], ['plan', 'Implementation plan'], ['rollback', 'Rollback plan'], ['impactAnalysis', 'Impact analysis (who and what is affected)']] as const).map(([k, n]) => <Field key={k} label={n}><textarea rows={3} value={(plan ?? c)[k] ?? ''} onChange={(e) => setPlan({ ...(plan ?? {}), [k]: e.target.value })} /></Field>)}
        <div className="actions"><button disabled={!plan} onClick={() => patch(plan!).then(() => setPlan(null))}>Save plan</button></div></div>
        : <Dl rows={[['Why', c.reason], ['What changes', c.description], ['Implementation', c.plan], ['Rollback', c.rollback], ['Impact analysis', c.impactAnalysis], ['Outcome', c.outcome]]} />}
      {staff && !['implementing', 'completed', 'failed', 'cancelled'].includes(c.status) && <div className="row" style={{ marginTop: 10 }}>
        <Field label="Window starts"><input type="datetime-local" value={w.startAt} onChange={(e) => setWin({ ...w, startAt: e.target.value })} /></Field>
        <Field label="Window ends"><input type="datetime-local" value={w.endAt} onChange={(e) => setWin({ ...w, endAt: e.target.value })} /></Field>
        <button disabled={!win} onClick={() => patch({ startAt: w.startAt ? new Date(w.startAt).toISOString() : null, endAt: w.endAt ? new Date(w.endAt).toISOString() : null }).then(() => setWin(null))}>Save window</button>
      </div>}
    </Card>
    {(c.stage === 'planning' || tasks.some((x) => x.stage === 'planning')) && <Tasks id={id} stage="planning" rows={tasks} staff={staff} locked={c.stage !== 'planning'} reload={reload} people={[]} title="Planning checklist" hint="Add the things to check before asking for approval, such as backup taken or users told." />}
    {['approved', 'scheduled', 'implementing', 'completed', 'failed', 'closed'].includes(c.status) && <Tasks id={id} stage="implementation" rows={tasks} staff={staff} locked={['completed', 'failed', 'closed'].includes(c.status)} reload={reload} people={Array.isArray(people.data) ? people.data : []} title="Implementation tasks" hint="Break the work into tasks. All of them must be done before the change can be marked completed." />}
    {['completed', 'failed', 'closed'].includes(c.status) && <Card title="Review and close">
      {staff && c.status !== 'closed' ? <div className="form">
        <Field label="Review notes: what went well, what to do differently"><textarea rows={4} value={review ?? c.reviewNotes ?? ''} onChange={(e) => setReview(e.target.value)} /></Field>
        <div className="row"><Field label="How did the change end?"><select value={closure} onChange={(e) => setClosure(e.target.value)}><option value="">Choose…</option>{Object.entries(CLOSURE).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
          <button disabled={review === null} onClick={() => patch({ reviewNotes: review }).then(() => setReview(null))}>Save review notes</button></div>
        <p className="muted">Use Close change above when the notes are written and the ending is chosen.</p></div>
        : <Dl rows={[['Closed as', CLOSURE[c.closureCode] ?? c.closureCode], ['Review notes', c.reviewNotes]]} />}
    </Card>}
    <Card title="Approvals">{votes.length === 0 ? <Empty>No decisions yet.</Empty> : <table><tbody>{votes.map((x) => <tr key={x.userId}><td>{x.name}</td><td><Badge tone={x.decision === 'approve' ? 'ok' : 'bad'}>{x.decision === 'approve' ? 'Approved' : 'Rejected'}</Badge></td><td>{x.comment}</td><td className="muted">{fmtDate(x.at)}</td></tr>)}</tbody></table>}</Card>
    <Card title="Linked">
      <p>Tickets: {tickets.length === 0 ? <span className="muted">none</span> : tickets.map((t) => <a key={t.id} href={'#/servicedesk/' + t.id} style={{ marginRight: 10 }}>{t.number}</a>)}</p>
      <p>Problems: {problems.length === 0 ? <span className="muted">none</span> : problems.map((p) => <a key={p.id} href={'#/servicedesk/problems/' + p.id} style={{ marginRight: 10 }}>{p.number}</a>)}</p>
      {staff && (links === null ? <button onClick={() => setLinks(tickets.map((t) => t.id).join(', '))}>Change linked tickets</button>
        : <div className="row"><Field label="Ticket ids"><input value={links} onChange={(e) => setLinks(e.target.value)} /></Field><button className="primary" onClick={() => patch({ tickets: idsFrom(links) }).then(() => setLinks(null))}>Save links</button></div>)}
    </Card>
    <Card title="Notes">
      {notes.map((n) => <p key={n.id}><strong>{n.author}</strong> <span className="muted">{fmtDate(n.at)}</span><br />{n.body}</p>)}
      {staff && <div className="form"><Field label="Add a note"><textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
        <div className="actions"><Act label="Add note" primary disabled={!note.trim()} run={async () => { await post('notes', { body: note }); setNote(''); }} /></div></div>}
    </Card>
    {ask === 'reject' && <Ask title="Reject this change" label="Why are you rejecting it?" need onClose={() => setAsk('')} onSend={async (t) => { await post('vote', { decision: 'reject', comment: t }); }} />}
    {ask === 'done' && <Ask title="Mark completed" label="What happened? Was the rollback used?" need onClose={() => setAsk('')} onSend={async (t) => { await post('status', { status: 'completed', outcome: t }); }} />}
    {ask === 'failed' && <Ask title="Mark failed" label="What went wrong? Was the rollback used?" need onClose={() => setAsk('')} onSend={async (t) => { await post('status', { status: 'failed', outcome: t }); }} />}
  </>;
}
