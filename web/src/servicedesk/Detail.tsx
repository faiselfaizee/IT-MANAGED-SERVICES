import { useRef, useState } from 'react';
import { api, go, saveFile, upload, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Err, Field, Loading, fmtDate, fmtBytes, useSubmit } from '../ui';
import { CustomCard, useFields } from './Fields';
import { KIND, KindBadge, LEVEL, PRIORITY, PriorityBadge, STATUS, SlaBadge, StatusBadge } from './List';
import { KnownErrorHints } from './Itil';
import { Impact } from '../assets/Relations';

export default function Detail({ id }: { id: string }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/tickets/' + id);
  const t = data?.ticket as Rec | undefined; const can = (data?.can ?? {}) as Rec;
  const staffList = useLoad<Rec[]>(can.edit ? '/api/servicedesk/staff' : null);
  const fieldDefs = useFields();
  const settings = useLoad<Rec>(can.edit ? '/api/servicedesk/settings' : null);
  const canned = useLoad<Rec[]>(can.edit ? '/api/servicedesk/canned' : null);
  const [body, setBody] = useState(''); const [internal, setInternal] = useState(false); const file = useRef<HTMLInputElement>(null);
  const reply = useSubmit(() => { setBody(''); setInternal(false); reload(); });
  const patch = (b: Rec) => api(`/api/servicedesk/tickets/${id}`, { method: 'PATCH', body: b }).then(reload);
  const setStatus = (status: string) => api(`/api/servicedesk/tickets/${id}/status`, { body: { status } }).then(reload);
  if (!t) return <><p><a href="#/servicedesk">← All tickets</a></p><Loading loading={loading} error={error} /></>;
  const comments = (data?.comments ?? []) as Rec[]; const files = (data?.attachments ?? []) as Rec[]; const staff = (Array.isArray(staffList.data) ? staffList.data : []) as Rec[]; const cats = (settings.data?.categories ?? []) as string[];
  const closed = t.status === 'closed';
  return <>
    <p><a href="#/servicedesk">← All tickets</a></p>
    <h1>{t.number} {t.subject}</h1>
    <div className="tools"><KindBadge k={t.kind} major={t.major} /><StatusBadge s={t.status} /><PriorityBadge p={t.priority} /><SlaBadge s={t.sla} />{t.category && <Badge>{t.category}</Badge>}</div>
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0, 2fr) minmax(260px, 1fr)', alignItems: 'start' }}>
      <div>
        {t.description && <Card title="Description"><p style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{t.description}</p></Card>}
        <Card title="Conversation">
          {comments.length === 0 ? <p className="muted">Nothing yet.</p> : <ol className="timeline">{comments.map((c) =>
            <li key={c.id} className={c.kind}><div className="who"><b>{c.author}</b>{c.kind === 'internal' && <Badge tone="warn">Internal note</Badge>}<span className="muted">{fmtDate(c.at)}</span></div>
              <div className="said">{c.body}</div></li>)}</ol>}
          {can.comment && !(closed && !can.edit) && <div className="form" style={{ marginTop: 14 }}>
            {can.edit && Array.isArray(canned.data) && canned.data.length > 0 && <label><span>Canned reply</span>
              <select value="" aria-label="Canned reply" onChange={(e) => { const c = canned.data!.find((x) => String(x.id) === e.target.value); if (c) setBody((b) => (b ? b + '\n\n' : '') + fillCanned(String(c.body), t)); }}>
                <option value="">Insert a canned reply…</option>{canned.data.map((c) => <option key={c.id} value={c.id}>{c.title}</option>)}</select></label>}
            <textarea rows={4} placeholder="Write a reply…" value={body} onChange={(e) => setBody(e.target.value)} aria-label="Reply" />
            <Err text={reply.err} />
            <div className="actions">
              {can.internalNote && <label><input type="checkbox" checked={internal} onChange={(e) => setInternal(e.target.checked)} /> Internal note (the customer cannot see it)</label>}
              <button className="primary" disabled={reply.busy || !body.trim()} onClick={() => reply.submit(() => api(`/api/servicedesk/tickets/${id}/comments`, { body: { body, internal } }))}>{internal ? 'Add note' : 'Send reply'}</button>
            </div>
          </div>}
          {can.comment && closed && !can.edit && <p className="muted">This ticket is closed. Reopen it to reply.</p>}
        </Card>
      </div>
      <div>
        <Card title="Status">
          <div className="actions" style={{ justifyContent: 'flex-start', flexWrap: 'wrap' }}>
            {can.edit ? Object.entries(STATUS).map(([k, n]) => <Act key={k} label={n} primary={k === t.status} disabled={k === t.status} run={() => setStatus(k)} />)
              : can.status ? <>
                {!closed && <Act label="Close ticket" confirm="Close this ticket?" run={() => setStatus('closed')} />}
                {(t.status === 'resolved' || closed) && <Act label="Reopen" primary run={() => setStatus('open')} />}
              </> : null}
          </div>
        </Card>
        {(can.rate || data?.rating) && <Rating id={id} done={t.status === 'resolved' || closed} rating={data?.rating as Rec | null} canRate={!!can.rate} reload={reload} />}
        <CustomCard defs={fieldDefs} values={(data?.custom ?? {}) as Rec} edit={!!can.edit} save={patch} />
        <Related id={id} />
        {can.edit && t.assetId && <AssetImpact assetId={String(t.assetId)} />}
        {can.edit && <KnownErrorHints id={id} />}
        {can.edit && <Assist id={id} t={t} patch={patch} fill={setBody} />}
        <Card title="Details">
          {can.edit ? <div className="form">
            <label><span>Type</span><select value={t.kind} onChange={(e) => patch({ kind: e.target.value })}>{Object.entries(KIND).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
            {t.kind === 'incident' && <div className="row">
              <label><span>Impact</span><select value={t.impact} onChange={(e) => patch({ impact: e.target.value })}><option value="">Not set</option>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
              <label><span>Urgency</span><select value={t.urgency} onChange={(e) => patch({ urgency: e.target.value })}><option value="">Not set</option>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label></div>}
            {t.kind === 'incident' && <label className="check"><input type="checkbox" checked={!!t.major} onChange={(e) => patch({ major: e.target.checked })} /> Major incident (many people affected, handled first)</label>}
            <label><span>Priority</span><select value={t.priority} onChange={(e) => patch({ priority: e.target.value })}>{Object.entries(PRIORITY).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
            <label><span>Assigned to</span><select value={t.assigneeId ?? ''} onChange={(e) => patch({ assigneeId: e.target.value || null })}><option value="">Unassigned</option>{staff.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
            <label><span>Category</span><select value={t.category} onChange={(e) => patch({ category: e.target.value })}><option value="">None</option>{cats.map((c) => <option key={c}>{c}</option>)}</select></label>
          </div> : null}
          <Dl rows={[['Type', KIND[t.kind] ?? t.kind], ...(!can.edit && t.kind === 'incident' ? [['Impact', LEVEL[t.impact] ?? ''] as [string, string], ['Urgency', LEVEL[t.urgency] ?? ''] as [string, string]] : []), ['Company', t.company], ['Raised by', [t.requester, t.requesterEmail].filter(Boolean).join(' · ')], ['Asset', t.asset], ['Opened', fmtDate(t.createdAt)],
            ...(can.edit ? [['Assigned to', ''] as [string, string]] : [['Assigned to', t.assignee] as [string, string]]),
            ['First reply', t.firstResponseAt ? fmtDate(t.firstResponseAt) : t.paused ? 'Paused' : 'Not yet'], ['Reply due', fmtDate(t.respondDue)], ['Resolution due', t.paused ? 'Paused while waiting for the customer' : fmtDate(t.resolveDue)],
            ['Resolved', t.resolvedAt ? fmtDate(t.resolvedAt) : '']]} />
        </Card>
        <Card title="Files">
          {files.length === 0 ? <p className="muted">No files.</p> : <ul className="files">{files.map((f) => <li key={f.id}>
            <a href="#" onClick={(e) => { e.preventDefault(); void saveFile(`/api/servicedesk/tickets/${id}/attachments/${f.id}`, f.name); }}>{f.name}</a> <span className="muted">{fmtBytes(f.size)} · {f.by}</span>{f.internal && <Badge tone="warn">Internal</Badge>}</li>)}</ul>}
          {can.comment && <><input ref={file} type="file" aria-label="Add a file" style={{ display: 'none' }} onChange={async (e) => {
            const picked = e.target.files?.[0]; if (!picked) return;
            try { await upload(`/api/servicedesk/tickets/${id}/attachments?name=${encodeURIComponent(picked.name)}`, picked); reload(); } catch (x) { reply.submit(async () => { throw x; }); }
            e.target.value = '';
          }} /><button onClick={() => file.current?.click()}>Add a file</button></>}
        </Card>
      </div>
    </div>
    {error && <Err text={error} />}
    <p><button onClick={() => go('servicedesk')}>Back to tickets</button></p>
  </>;
}

/** Fills {requester}, {agent} and {number} in a canned reply. {agent} is the person assigned, or "the support team". */
export function fillCanned(text: string, t: Rec): string {
  const first = (n: unknown) => String(n ?? '').trim().split(' ')[0];
  return text.replace(/\{requester\}/gi, first(t.requester) || 'there').replace(/\{agent\}/gi, String(t.assignee || 'the support team')).replace(/\{number\}/gi, String(t.number ?? ''));
}

/** Suggestions for the support team: similar earlier tickets, a category and priority, and a reply to start from. */
function Assist({ id, t, patch, fill }: { id: string; t: Rec; patch: (b: Rec) => Promise<unknown>; fill: (text: string) => void }) {
  const [on, setOn] = useState(false); const a = useLoad<Rec>(on ? `/api/servicedesk/tickets/${id}/assist` : null);
  return <Card title="Suggestions">
    {!on ? <button onClick={() => setOn(true)}>Find similar tickets</button> : <>
      <Loading loading={a.loading && !a.data} error={a.error} />
      {a.data && <>
        {a.data.suggestedCategory && <p>Category looks like <b>{a.data.suggestedCategory}</b> <Act label="Use it" run={() => patch({ category: a.data!.suggestedCategory })} /></p>}
        {a.data.suggestedPriority && <p>Priority looks like <b>{PRIORITY[a.data.suggestedPriority] ?? a.data.suggestedPriority}</b> (now {PRIORITY[t.priority] ?? t.priority}) <Act label="Use it" run={() => patch({ priority: a.data!.suggestedPriority })} /></p>}
        {(a.data.similar as Rec[]).length === 0 ? <p className="muted">No similar tickets yet.</p> : <ul>{(a.data.similar as Rec[]).map((s) => <li key={s.id}><a href={`#/servicedesk/${s.id}`}>{s.number}</a> {s.subject} <span className="muted">· {s.status}</span>{s.answer && <div className="muted">Answered: {s.answer}</div>}</li>)}</ul>}
        <button onClick={() => fill(a.data!.replyDraft)}>Start a reply from this</button>
      </>}
    </>}
  </Card>;
}

/** What else suffers if the ticket's asset is down (needs Assets; hidden when it is not available). */
function AssetImpact({ assetId }: { assetId: string }) {
  const i = useLoad<Rec>(`/api/assets/${assetId}/impact`);
  return <Impact data={i.data} loading={i.loading} error={i.error} title="Affected if this asset fails" />;
}

/** Problems and changes this ticket is tied to (the team sees them; customers get nothing back). */
function Related({ id }: { id: string }) {
  const r = useLoad<Rec>(`/api/servicedesk/tickets/${id}/related`); const problems = (r.data?.problems ?? []) as Rec[]; const changes = (r.data?.changes ?? []) as Rec[];
  if (problems.length + changes.length === 0) return null;
  return <Card title="Related">
    {problems.length > 0 && <><b>Problems</b><ul>{problems.map((p) => <li key={p.id}><a href={`#/servicedesk/problems/${p.id}`}>{p.number}</a> {p.title} <span className="muted">· {String(p.status).replace('_', ' ')}</span></li>)}</ul></>}
    {changes.length > 0 && <><b>Changes</b><ul>{changes.map((c) => <li key={c.id}><a href={`#/servicedesk/changes/${c.id}`}>{c.number}</a> {c.title} <span className="muted">· {c.status}</span></li>)}</ul></>}
  </Card>;
}

/** "Did we solve it?": a thumbs up or down with an optional comment, once the ticket is resolved. */
function Rating({ id, done, rating, canRate, reload }: { id: string; done: boolean; rating: Rec | null; canRate: boolean; reload: () => void }) {
  const [comment, setComment] = useState(''); const f = useSubmit(reload);
  const send = (up: boolean) => f.submit(() => api(`/api/servicedesk/tickets/${id}/rating`, { body: { up, comment } }));
  if (!rating && !(canRate && done)) return null;
  return <Card title="Did we solve it?">
    {rating && <p>{rating.up ? '👍 Solved' : '👎 Not solved'}{rating.comment ? ': ' + rating.comment : ''}</p>}
    {canRate && done && <div className="form">
      <Field label="Comment (optional)"><input value={comment} maxLength={2000} onChange={(e) => setComment(e.target.value)} /></Field>
      <Err text={f.err} />
      <div className="actions" style={{ justifyContent: 'flex-start' }}><button className="primary" disabled={f.busy} onClick={() => send(true)}>👍 Solved</button><button disabled={f.busy} onClick={() => send(false)}>👎 Not solved</button></div>
    </div>}
  </Card>;
}
