import { useEffect, useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDay, useSubmit } from '../ui';

const WHO: Record<string, string> = { everyone: 'Everyone', company: 'One company', staff: 'Staff only' };

/** Articles that answer common questions: search for one, or (staff) write them. */
export function KbList({ staff }: { staff: boolean }) {
  const [q, setQ] = useState(''); const [term, setTerm] = useState('');
  useEffect(() => { const h = setTimeout(() => setTerm(q.trim()), 250); return () => clearTimeout(h); }, [q]);
  const { data, error, loading } = useLoad<Rec[]>('/api/servicedesk/kb?q=' + encodeURIComponent(term), [term]);
  const list = Array.isArray(data) ? data : [];
  return <>
    <h1>Knowledge base</h1>
    <div className="toolbar">
      <input type="search" aria-label="Search articles" placeholder="Search for an answer" value={q} onChange={(e) => setQ(e.target.value)} />
      {staff && <a className="button primary" href="#/servicedesk/kb/new">New article</a>}
    </div>
    <Loading loading={loading && !data} error={error} />
    {data && list.length === 0 && <Empty>{term ? 'No article matches. Open a ticket and we will help.' : staff ? 'No articles yet. Write the first one for the questions people ask most.' : 'No articles yet.'}</Empty>}
    {list.map((a) => <Card key={a.id}>
      <h3><a href={'#/servicedesk/kb/' + a.id}>{a.title}</a> {staff && !a.published && <Badge tone="warn">Draft</Badge>} {staff && <Badge>{WHO[a.visibility] ?? a.visibility}</Badge>}</h3>
      <p className="muted">{a.snippet}</p>
    </Card>)}
  </>;
}

/** One article, with "was this helpful?" for readers and the editor for staff. */
export function KbArticle({ id, staff }: { id: string; staff: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec>(id === 'new' ? null : '/api/servicedesk/kb/' + id, [id]);
  const [editing, setEditing] = useState(false); const [voted, setVoted] = useState<boolean | null>(null);
  if (id === 'new') return <KbEdit a={null} onDone={(n) => go('servicedesk/kb/' + n)} />;
  if (!data) return <Loading loading={loading} error={error} />;
  if (editing && staff) return <KbEdit a={data} onDone={() => { setEditing(false); reload(); }} />;
  return <>
    <p><a href="#/servicedesk/kb">‹ Knowledge base</a></p>
    <h1>{data.title}</h1>
    {staff && <p className="muted">{WHO[data.visibility]} · {data.published ? 'Published' : 'Draft'} · {data.views} views · {data.helpful} found it helpful, {data.notHelpful} did not · updated {fmtDay(data.updatedAt)}</p>}
    <Card><div style={{ whiteSpace: 'pre-wrap' }}>{data.body}</div></Card>
    {data.published && <p>Was this helpful?{' '}
      <Act label="Yes" primary={voted === true} run={async () => { await api(`/api/servicedesk/kb/${id}/vote`, { body: { helpful: true } }); setVoted(true); }} />{' '}
      <Act label="No" primary={voted === false} run={async () => { await api(`/api/servicedesk/kb/${id}/vote`, { body: { helpful: false } }); setVoted(false); }} />
      {voted === false && <> <a href="#/servicedesk/new">Open a ticket</a></>}</p>}
    {staff && <div className="actions" style={{ justifyContent: 'flex-start' }}>
      <button onClick={() => setEditing(true)}>Edit</button>
      <Act label="Delete" confirm="Delete this article?" run={async () => { await api('/api/servicedesk/kb/' + id, { method: 'DELETE' }); go('servicedesk/kb'); }} />
    </div>}
  </>;
}

function KbEdit({ a, onDone }: { a: Rec | null; onDone: (id: string) => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [v, setV] = useState<Rec>({ title: a?.title ?? '', body: a?.body ?? '', tags: a?.tags ?? '', visibility: a?.visibility ?? 'everyone', companyId: a?.companyId ?? '', published: !!a?.published });
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const f = useSubmit(() => undefined);
  const cos = Array.isArray(companies.data) ? companies.data : [];
  const save = (publish: boolean) => f.submit(async () => {
    const body: Rec = { title: v.title, body: v.body, tags: v.tags, visibility: v.visibility, published: publish };
    if (v.visibility === 'company') body.companyId = Number(v.companyId);
    const r = a ? await api<Rec>('/api/servicedesk/kb/' + a.id, { method: 'PUT', body }) : await api<Rec>('/api/servicedesk/kb', { body });
    onDone(String(r.id));
  });
  return <>
    <h1>{a ? 'Edit article' : 'New article'}</h1>
    <Card><div className="form">
      <Field label="Title"><input value={v.title} maxLength={200} onChange={(e) => set('title', e.target.value)} autoFocus /></Field>
      <Field label="Answer"><textarea rows={12} value={v.body} onChange={(e) => set('body', e.target.value)} /></Field>
      <Field label="Tags" hint="Words people might search for, separated by commas."><input value={v.tags} onChange={(e) => set('tags', e.target.value)} /></Field>
      <div className="row">
        <Field label="Who can read it"><select value={v.visibility} onChange={(e) => set('visibility', e.target.value)}>{Object.entries(WHO).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        {v.visibility === 'company' && <Field label="Company"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">Choose…</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>}
      </div>
      <Err text={f.err} />
      <div className="actions"><button onClick={() => (a ? onDone(String(a.id)) : go('servicedesk/kb'))}>Cancel</button>
        <button disabled={f.busy || !v.title.trim()} onClick={() => save(false)}>Save as draft</button>
        <button className="primary" disabled={f.busy || !v.title.trim() || (v.visibility === 'company' && !v.companyId)} onClick={() => save(true)}>Publish</button></div>
    </div></Card>
  </>;
}

/** On New ticket: articles that match the subject while it is typed, so many questions need no ticket. */
export function Suggestions({ subject }: { subject: string }) {
  const [term, setTerm] = useState('');
  useEffect(() => { const h = setTimeout(() => setTerm(subject.trim().length >= 4 ? subject.trim() : ''), 400); return () => clearTimeout(h); }, [subject]);
  const { data } = useLoad<Rec[]>(term ? '/api/servicedesk/kb?limit=4&q=' + encodeURIComponent(term) : null, [term]);
  const list = (Array.isArray(data) ? data : []).filter((a) => a.published);
  if (!term || list.length === 0) return null;
  return <div className="note" role="region" aria-label="Articles that may help">
    <strong>These articles may answer it</strong>
    <ul>{list.map((a) => <li key={a.id}><a href={'#/servicedesk/kb/' + a.id} target="_blank" rel="noreferrer">{a.title}</a></li>)}</ul>
  </div>;
}
