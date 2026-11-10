import { useRef, useState } from 'react';
import { api, go, saveFile, upload, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Err, Loading, fmtDate, fmtBytes, useSubmit } from '../ui';
import { PRIORITY, PriorityBadge, STATUS, SlaBadge, StatusBadge } from './List';

export default function Detail({ id }: { id: string }) {
  const { data, error, loading, reload } = useLoad<Rec>('/api/servicedesk/tickets/' + id);
  const t = data?.ticket as Rec | undefined; const can = (data?.can ?? {}) as Rec;
  const staffList = useLoad<Rec[]>(can.edit ? '/api/servicedesk/staff' : null);
  const settings = useLoad<Rec>(can.edit ? '/api/servicedesk/settings' : null);
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
    <div className="tools"><StatusBadge s={t.status} /><PriorityBadge p={t.priority} /><SlaBadge s={t.sla} />{t.category && <Badge>{t.category}</Badge>}</div>
    <div className="grid" style={{ gridTemplateColumns: 'minmax(0, 2fr) minmax(260px, 1fr)', alignItems: 'start' }}>
      <div>
        {t.description && <Card title="Description"><p style={{ whiteSpace: 'pre-wrap', margin: 0 }}>{t.description}</p></Card>}
        <Card title="Conversation">
          {comments.length === 0 ? <p className="muted">Nothing yet.</p> : <ol className="timeline">{comments.map((c) =>
            <li key={c.id} className={c.kind}><div className="who"><b>{c.author}</b>{c.kind === 'internal' && <Badge tone="warn">Internal note</Badge>}<span className="muted">{fmtDate(c.at)}</span></div>
              <div className="said">{c.body}</div></li>)}</ol>}
          {can.comment && !(closed && !can.edit) && <div className="form" style={{ marginTop: 14 }}>
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
        <Card title="Details">
          {can.edit ? <div className="form">
            <label><span>Priority</span><select value={t.priority} onChange={(e) => patch({ priority: e.target.value })}>{Object.entries(PRIORITY).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></label>
            <label><span>Assigned to</span><select value={t.assigneeId ?? ''} onChange={(e) => patch({ assigneeId: e.target.value || null })}><option value="">Unassigned</option>{staff.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></label>
            <label><span>Category</span><select value={t.category} onChange={(e) => patch({ category: e.target.value })}><option value="">None</option>{cats.map((c) => <option key={c}>{c}</option>)}</select></label>
          </div> : null}
          <Dl rows={[['Company', t.company], ['Raised by', [t.requester, t.requesterEmail].filter(Boolean).join(' · ')], ['Asset', t.asset], ['Opened', fmtDate(t.createdAt)],
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
