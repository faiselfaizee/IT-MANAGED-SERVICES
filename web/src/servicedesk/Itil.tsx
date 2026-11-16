import { useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDate, useSubmit } from '../ui';

const KINDS: [string, string][] = [['text', 'Short text'], ['longtext', 'Long text'], ['number', 'Number'], ['date', 'Date'], ['choice', 'Choice'], ['checkbox', 'Yes / no'], ['email', 'Email']];

// ------------------------------------------------------------------ service catalogue (request fulfilment)

function RequestForm({ item, staff, onClose }: { item: Rec; staff: boolean; onClose: () => void }) {
  const [a, setA] = useState<Rec>({}); const [note, setNote] = useState(''); const [done, setDone] = useState<Rec | null>(null);
  const companies = useLoad<Rec[]>(staff ? '/api/assets/companies' : null); const [company, setCompany] = useState('');
  const f = useSubmit(() => undefined);
  const fields = (item.fields ?? []) as Rec[];
  if (done) return <Modal title="Request sent" onClose={onClose}>
    <p>Your request <strong>{String(done.number)}</strong> was opened.{done.approval === 'pending' ? ' It waits for approval first; you will get an email when it is decided.' : ''}</p>
    <div className="actions"><button className="primary" onClick={() => go('servicedesk/' + done.id)}>Open it</button><button onClick={onClose}>Close</button></div></Modal>;
  return <Modal title={String(item.name)} onClose={onClose}><div className="form">
    {item.description && <p className="muted">{String(item.description)}</p>}
    {staff && <Field label="For customer (optional)"><select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">Our own company</option>{((companies.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>}
    {fields.map((fd) => { const k = String(fd.key); const label = String(fd.label) + (fd.required ? ' *' : '');
      return <div key={k}>{fd.kind === 'checkbox' ? <label className="check"><input type="checkbox" checked={!!a[k]} onChange={(e) => setA({ ...a, [k]: e.target.checked })} /> {label}</label>
        : <Field label={label}>{fd.kind === 'longtext' ? <textarea rows={3} value={a[k] ?? ''} onChange={(e) => setA({ ...a, [k]: e.target.value })} />
          : fd.kind === 'choice' ? <select value={a[k] ?? ''} onChange={(e) => setA({ ...a, [k]: e.target.value })}><option value="">Choose…</option>{((fd.options ?? []) as string[]).map((o) => <option key={o}>{o}</option>)}</select>
          : <input type={fd.kind === 'number' ? 'number' : fd.kind === 'date' ? 'date' : fd.kind === 'email' ? 'email' : 'text'} value={a[k] ?? ''} onChange={(e) => setA({ ...a, [k]: e.target.value })} />}</Field>}
        {fd.help && <p className="muted small">{String(fd.help)}</p>}</div>; })}
    <Field label="Anything else"><textarea rows={2} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
    {item.needsApproval && <p className="muted small">This needs approval before the team starts on it.</p>}
    <Err text={f.err} />
    <div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(async () => {
      const answers: Rec = {}; for (const fd of fields) { const v = a[String(fd.key)]; if (v !== undefined && v !== '') answers[String(fd.key)] = fd.kind === 'number' ? Number(v) : v; }
      setDone(await api<Rec>(`/api/servicedesk/catalog/${item.id}/request`, { method: 'POST', body: { answers, note, companyId: company ? Number(company) : undefined } }));
    })}>Send request</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function Catalog({ staff }: { staff: boolean }) {
  const list = useLoad<Rec[]>('/api/servicedesk/catalog'); const [q, setQ] = useState(''); const [pick, setPick] = useState<Rec | null>(null);
  const items = ((list.data ?? []) as Rec[]).filter((i) => !q || `${i.name} ${i.description} ${i.category}`.toLowerCase().includes(q.toLowerCase()));
  const groups = [...new Set(items.map((i) => String(i.category)))];
  return <>
    <div className="dash-head"><h1>Service catalogue</h1><p className="muted">Ask for something: equipment, access, software, an account. Each request becomes an SRV ticket.</p></div>
    <div className="tools"><input placeholder="What do you need?" value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 280 }} /></div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && items.length === 0 && <Empty>{q ? 'Nothing matches.' : 'The catalogue is empty. An administrator adds items under Catalogue items.'}</Empty>}
    {groups.map((g) => <section key={g}><h3>{g}</h3><div className="cmdcards">{items.filter((i) => i.category === g).map((i) => <div key={i.id} className="cmdcard">
      <div className="cmdtop"><strong>{i.icon ? `${i.icon} ` : ''}{i.name}</strong>{i.needsApproval && <Badge tone="warn">Approval</Badge>}</div>
      <p className="muted small">{i.description}</p>
      <div className="cmdfoot"><span className="muted small">{i.resolveHours ? `Usually within ${i.resolveHours} h` : ''}</span><button className="primary" onClick={() => setPick(i)}>Request</button></div></div>)}</div></section>)}
    {pick && <RequestForm item={pick} staff={staff} onClose={() => { setPick(null); }} />}
  </>;
}

export function Approvals() {
  const list = useLoad<Rec[]>('/api/servicedesk/approvals'); const [ask, setAsk] = useState<Rec | null>(null); const [note, setNote] = useState('');
  const rows = (list.data ?? []) as Rec[];
  return <>
    <div className="dash-head"><h1>Approvals</h1><p className="muted">Requests from the service catalogue waiting for you to approve.</p></div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (rows.length === 0 ? <Empty>Nothing waits for your approval.</Empty> : rows.map((r) => <Card key={r.id} title={`${r.number} · ${r.item}`}>
      <p><strong>{r.requester}</strong>{r.company ? ` (${r.company})` : ''} · <span className="muted">{fmtDate(r.createdAt)}</span></p>
      <pre className="plaintext">{r.details}</pre>
      <div className="actions"><Act label="Approve" primary run={async () => { await api(`/api/servicedesk/tickets/${r.id}/approval`, { method: 'POST', body: { decision: 'approve' } }); list.reload(); }} />
        <button onClick={() => { setAsk(r); setNote(''); }}>Turn down</button><a href={'#/servicedesk/' + r.id}>Open the request</a></div></Card>))}
    {ask && <Modal title={`Turn down ${ask.number}`} onClose={() => setAsk(null)}><Field label="Why (the requester sees this)"><textarea rows={3} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      <div className="actions"><Act label="Turn down" primary disabled={!note.trim()} run={async () => { await api(`/api/servicedesk/tickets/${ask.id}/approval`, { method: 'POST', body: { decision: 'reject', note } }); setAsk(null); list.reload(); }} /><button onClick={() => setAsk(null)}>Cancel</button></div></Modal>}
  </>;
}

function ItemForm({ item, onClose, onSaved }: { item: Rec | null; onClose: () => void; onSaved: () => void }) {
  const cab = useLoad<Rec>('/api/servicedesk/cab');
  const staff = ((cab.data?.staff ?? []) as Rec[]);
  const [v, setV] = useState<Rec>(item ? { ...item } : { name: '', category: 'General', description: '', icon: '', priority: 'medium', resolveHours: '', approvers: [], assigneeId: '', staffOnly: false, active: true, fields: [] });
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const fields = (v.fields ?? []) as Rec[];
  const setField = (i: number, k: string, x: unknown) => set('fields', fields.map((f, j) => (j === i ? { ...f, [k]: x } : f)));
  const f = useSubmit(() => { onSaved(); onClose(); });
  const body = () => ({ ...v, resolveHours: v.resolveHours === '' || v.resolveHours == null ? null : Number(v.resolveHours), assigneeId: v.assigneeId || null,
    fields: fields.map((x) => ({ ...x, options: x.kind === 'choice' ? String(x.optionsText ?? ((x.options ?? []) as string[]).join('\n')).split('\n').map((o) => o.trim()).filter(Boolean) : undefined, optionsText: undefined })) });
  return <Modal title={item ? 'Edit catalogue item' : 'New catalogue item'} onClose={onClose}><div className="form">
    <div className="grid2">
      <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} placeholder="New laptop" /></Field>
      <Field label="Category"><input value={v.category} onChange={(e) => set('category', e.target.value)} placeholder="Hardware" /></Field>
      <div className="wide"><Field label="Description"><textarea rows={2} value={v.description} onChange={(e) => set('description', e.target.value)} /></Field></div>
      <Field label="Icon (an emoji, optional)"><input value={v.icon} maxLength={4} onChange={(e) => set('icon', e.target.value)} /></Field>
      <Field label="Priority"><select value={v.priority} onChange={(e) => set('priority', e.target.value)}>{['low', 'medium', 'high', 'critical'].map((p) => <option key={p}>{p}</option>)}</select></Field>
      <Field label="Done within (hours, empty = service level)"><input type="number" min={1} value={v.resolveHours ?? ''} onChange={(e) => set('resolveHours', e.target.value)} /></Field>
      <Field label="Goes to"><select value={v.assigneeId ?? ''} onChange={(e) => set('assigneeId', e.target.value)}><option value="">Not assigned</option>{staff.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}</select></Field>
    </div>
    <fieldset><legend>Approvers (any one of them approves; none = no approval)</legend>
      <div className="grid2">{staff.map((s) => <label key={s.id} className="check"><input type="checkbox" checked={(v.approvers as string[]).includes(s.id)} onChange={() => set('approvers', (v.approvers as string[]).includes(s.id) ? (v.approvers as string[]).filter((x) => x !== s.id) : [...(v.approvers as string[]), s.id])} /> {s.name}</label>)}</div></fieldset>
    <fieldset><legend>Form</legend>
      {fields.length === 0 && <p className="muted small">No questions: the request carries only the note.</p>}
      {fields.map((fd, i) => <div key={i} className="grid2" style={{ borderBottom: '1px solid var(--line)', paddingBottom: 8, marginBottom: 8 }}>
        <Field label="Question"><input value={fd.label ?? ''} onChange={(e) => setField(i, 'label', e.target.value)} /></Field>
        <Field label="Key (letters, digits, _)"><input value={fd.key ?? ''} onChange={(e) => setField(i, 'key', e.target.value.toLowerCase().replace(/[^a-z0-9_]/g, '_'))} /></Field>
        <Field label="Kind"><select value={fd.kind ?? 'text'} onChange={(e) => setField(i, 'kind', e.target.value)}>{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
        <label className="check"><input type="checkbox" checked={!!fd.required} onChange={(e) => setField(i, 'required', e.target.checked)} /> Required</label>
        {fd.kind === 'choice' && <div className="wide"><Field label="Options (one per line)"><textarea rows={3} value={fd.optionsText ?? ((fd.options ?? []) as string[]).join('\n')} onChange={(e) => setField(i, 'optionsText', e.target.value)} /></Field></div>}
        <div className="wide"><button className="link" onClick={() => set('fields', fields.filter((_, j) => j !== i))}>Remove this question</button></div>
      </div>)}
      <button onClick={() => set('fields', [...fields, { key: 'q' + (fields.length + 1), label: '', kind: 'text', required: false }])}>Add a question</button>
    </fieldset>
    <label className="check"><input type="checkbox" checked={!!v.staffOnly} onChange={(e) => set('staffOnly', e.target.checked)} /> Only the support team sees it</label>
    <label className="check"><input type="checkbox" checked={v.active !== false} onChange={(e) => set('active', e.target.checked)} /> Offered</label>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy || !String(v.name).trim()} onClick={() => void f.submit(() => item ? api(`/api/servicedesk/catalog/${item.id}`, { method: 'PUT', body: body() }) : api('/api/servicedesk/catalog', { method: 'POST', body: body() }))}>Save</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function CatalogAdmin() {
  const list = useLoad<Rec[]>('/api/servicedesk/catalog?all=true'); const [edit, setEdit] = useState<Rec | null | undefined>(undefined);
  const rows = (list.data ?? []) as Rec[];
  return <Card title="Catalogue items">
    <div className="actions"><button className="primary" onClick={() => setEdit(null)}>New item</button></div>
    {rows.length === 0 ? <Empty>No items yet. Start with the things people ask for most: a laptop, access to a folder, a software install, a new account.</Empty>
      : <table><thead><tr><th>Item</th><th>Category</th><th>Questions</th><th>Approval</th><th /></tr></thead><tbody>{rows.map((i) => <tr key={i.id}>
        <td><strong>{i.icon} {i.name}</strong>{!i.active && <> <Badge>Off</Badge></>}{i.staffOnly && <> <Badge>Staff only</Badge></>}</td><td>{i.category}</td><td>{(i.fields as Rec[]).length}</td><td>{i.needsApproval ? 'Yes' : 'No'}</td>
        <td className="actions"><button onClick={() => setEdit(i)}>Edit</button><Act label="Delete" danger confirm={`Delete ${i.name}? If it was used, it is switched off instead.`} run={async () => { await api(`/api/servicedesk/catalog/${i.id}`, { method: 'DELETE' }); list.reload(); }} /></td></tr>)}</tbody></table>}
    {edit !== undefined && <ItemForm item={edit} onClose={() => setEdit(undefined)} onSaved={list.reload} />}
  </Card>;
}

// ------------------------------------------------------------------ problem management: known errors

export function KnownErrors() {
  const [q, setQ] = useState(''); const [go2, setGo2] = useState('');
  const list = useLoad<Rec[]>(`/api/servicedesk/known-errors?q=${encodeURIComponent(go2)}`, [go2]);
  const rows = (list.data ?? []) as Rec[];
  return <>
    <div className="dash-head"><h1>Known errors</h1><p className="muted">Problems with a workaround people can use now, while the cause is fixed.</p></div>
    <div className="tools"><input placeholder="Search symptoms, e.g. outlook password" value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') setGo2(q); }} style={{ minWidth: 300 }} /><button className="primary" onClick={() => setGo2(q)}>Search</button></div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (rows.length === 0 ? <Empty>No known errors{go2 ? ' match' : ' yet'}.</Empty> : rows.map((k) => <Card key={k.id} title={`${k.number} · ${k.title}`}>
      <p><Badge tone={k.status === 'known_error' ? 'warn' : k.status === 'resolved' ? 'ok' : undefined}>{k.status === 'known_error' ? 'Known error' : k.status}</Badge> {k.category && <span className="muted">{k.category}</span>} · {String(k.incidents)} incident(s)</p>
      <p><strong>Workaround:</strong> {k.workaround}</p>
      {k.rootCause && <p className="muted"><strong>Cause:</strong> {k.rootCause}</p>}
      <a href={'#/servicedesk/problems/' + k.id}>Open the problem</a></Card>))}
  </>;
}

/** On a ticket: known errors that may match, to apply the workaround and link the ticket to the problem. */
export function KnownErrorHints({ id }: { id: string }) {
  const h = useLoad<Rec[]>(`/api/servicedesk/tickets/${id}/known-errors`);
  const rows = Array.isArray(h.data) ? (h.data as Rec[]) : [];
  if (rows.length === 0) return null;
  return <Card title="Known errors that may match">
    {rows.map((k) => <div key={k.id} style={{ marginBottom: 10 }}>
      <p><a href={'#/servicedesk/problems/' + k.id}><strong>{k.number}</strong> {k.title}</a> {k.linked && <Badge tone="ok">Linked</Badge>}</p>
      <p className="small"><strong>Workaround:</strong> {k.workaround}</p>
      {!k.linked && <Act label="Link this ticket to the problem" run={async () => { await api(`/api/servicedesk/tickets/${id}/problem`, { method: 'POST', body: { problemId: k.id } }); h.reload(); }} />}
    </div>)}
  </Card>;
}

/** On a problem: impact and urgency, and resolving the incidents it caused. */
export function ProblemExtra({ id, staff, status, open }: { id: string; staff: boolean; status: string; open: number }) {
  const d = useLoad<Rec>(`/api/servicedesk/problems/${id}/impact`);
  const [msg, setMsg] = useState('');
  if (!d.data || typeof d.data.impact !== 'string') return null;
  const save = async (k: string, v: string) => { const body = { impact: d.data!.impact, urgency: d.data!.urgency, [k]: v }; await api(`/api/servicedesk/problems/${id}/impact`, { method: 'PUT', body }); d.reload(); setMsg('Priority follows impact and urgency.'); };
  return <Card title="Impact and urgency">
    <div className="row">
      <Field label="Impact"><select disabled={!staff} value={String(d.data.impact)} onChange={(e) => void save('impact', e.target.value)}><option value="">Not set</option>{['low', 'medium', 'high'].map((x) => <option key={x}>{x}</option>)}</select></Field>
      <Field label="Urgency"><select disabled={!staff} value={String(d.data.urgency)} onChange={(e) => void save('urgency', e.target.value)}><option value="">Not set</option>{['low', 'medium', 'high'].map((x) => <option key={x}>{x}</option>)}</select></Field>
    </div>
    {msg && <p className="muted small">{msg}</p>}
    {staff && ['resolved', 'closed', 'known_error'].includes(status) && open > 0 && <div className="actions">
      <Act label={status === 'known_error' ? `Send the workaround and resolve ${open} incident(s)` : `Resolve ${open} linked incident(s)`} primary confirm="Each linked open ticket gets the resolution as a reply and is resolved. Continue?"
        run={async () => { const r = await api<Rec>(`/api/servicedesk/problems/${id}/resolve-incidents`, { method: 'POST', body: {} }); setMsg(`${r.resolved} incident(s) resolved.`); }} /></div>}
  </Card>;
}

// ------------------------------------------------------------------ change management

/** On a change: category, test plan, the assets it touches, and changes that clash with its window. */
export function ChangeExtra({ id, staff }: { id: string; staff: boolean }) {
  const d = useLoad<Rec>(`/api/servicedesk/changes/${id}/details`);
  const [edit, setEdit] = useState<Rec | null>(null); const [q, setQ] = useState('');
  const found = useLoad<Rec>(edit && q.length >= 2 ? `/api/assets?search=${encodeURIComponent(q)}&take=20` : null, [q]);
  const f = useSubmit(() => { setEdit(null); d.reload(); });
  if (!d.data) return null;
  const x = d.data; const assets = (Array.isArray(edit?.assets ?? x.assets) ? (edit?.assets ?? x.assets) : []) as Rec[];
  return <Card title="Scope">
    {Array.isArray(x.clashes) && (x.clashes as Rec[]).length > 0 && <p className="err">Clashes with {(x.clashes as Rec[]).map((c) => <a key={c.id} href={'#/servicedesk/changes/' + c.id} style={{ marginRight: 8 }}>{c.number}</a>)} (same customer or asset, overlapping window).</p>}
    {!edit ? <>
      <p><strong>Category:</strong> {x.category || '–'}</p>
      <p><strong>Assets (configuration items):</strong> {assets.length === 0 ? '–' : assets.map((a) => <a key={a.id} href={'#/assets/' + a.id} style={{ marginRight: 8 }}>{a.tag} {a.name}</a>)}</p>
      <p><strong>Test plan:</strong> {x.testPlan || '–'}</p>
      {staff && <button onClick={() => setEdit({ category: x.category, testPlan: x.testPlan, assets: x.assets })}>Edit scope</button>}
    </> : <div className="form">
      <Field label="Category"><input value={edit.category} onChange={(e) => setEdit({ ...edit, category: e.target.value })} placeholder="Servers, Network, Applications…" /></Field>
      <Field label="Test plan (how you will know it worked)"><textarea rows={3} value={edit.testPlan} onChange={(e) => setEdit({ ...edit, testPlan: e.target.value })} /></Field>
      <div><span className="lbl">Assets</span><div className="chips">{assets.map((a) => <span key={a.id} className="chip">{a.tag} {a.name}<button aria-label="Remove" onClick={() => setEdit({ ...edit, assets: assets.filter((y) => y.id !== a.id) })}>×</button></span>)}</div>
        <input placeholder="Find an asset to add" value={q} onChange={(e) => setQ(e.target.value)} />
        {((found.data?.items ?? []) as Rec[]).slice(0, 8).map((a) => <button key={a.id} className="link" style={{ display: 'block' }} onClick={() => { if (!assets.some((y) => y.id === a.id)) setEdit({ ...edit, assets: [...assets, { id: a.id, tag: a.tag, name: a.name }] }); setQ(''); }}>+ {a.tag} {a.name}</button>)}</div>
      <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(() => api(`/api/servicedesk/changes/${id}/details`, { method: 'PUT', body: { category: edit.category, testPlan: edit.testPlan, assetIds: assets.map((a) => a.id) } }))}>Save</button><button onClick={() => setEdit(null)}>Cancel</button></div>
    </div>}
  </Card>;
}

const TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { approved: 'ok', scheduled: 'ok', implementing: 'warn', completed: 'ok', failed: 'bad', review: 'warn', draft: undefined, closed: undefined };

export function ChangeCalendar() {
  const [from, setFrom] = useState(() => { const d = new Date(); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() - d.getDay()); return d; });
  const to = new Date(from.getTime() + 28 * 86400000);
  const cal = useLoad<Rec>(`/api/servicedesk/changes/calendar?from=${from.toISOString()}&to=${to.toISOString()}`, [from.getTime()]);
  const items = (cal.data?.items ?? []) as Rec[];
  const days = Array.from({ length: 28 }, (_, i) => new Date(from.getTime() + i * 86400000));
  const on = (d: Date) => items.filter((x) => new Date(x.startAt) < new Date(d.getTime() + 86400000) && new Date(x.endAt) > d);
  return <>
    <div className="dash-head"><h1>Change calendar</h1><p className="muted">Planned changes for four weeks. A red mark means it clashes with another change for the same customer or asset.</p></div>
    <div className="tools"><button onClick={() => setFrom(new Date(from.getTime() - 28 * 86400000))}>← Earlier</button><button onClick={() => { const d = new Date(); d.setHours(0, 0, 0, 0); d.setDate(d.getDate() - d.getDay()); setFrom(d); }}>This week</button><button onClick={() => setFrom(to)}>Later →</button>
      <span className="muted">{from.toLocaleDateString()} – {new Date(to.getTime() - 86400000).toLocaleDateString()}</span></div>
    <Loading loading={cal.loading && !cal.data} error={cal.error} />
    <div className="calendar">{['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map((d) => <div key={d} className="calhead">{d}</div>)}
      {days.map((d) => { const today = d.toDateString() === new Date().toDateString(); return <div key={d.toISOString()} className={'calday' + (today ? ' today' : '')}>
        <span className="calnum">{d.getDate()}</span>
        {on(d).map((x) => <a key={x.id} href={'#/servicedesk/changes/' + x.id} className={'calitem' + ((x.clashes as string[]).length ? ' clash' : '')} title={`${x.number} ${x.title} · ${x.status}${(x.clashes as string[]).length ? ' · clashes with ' + (x.clashes as string[]).join(', ') : ''}`}>
          <Badge tone={TONE[String(x.status)]}>{String(x.number)}</Badge> {String(x.title)}</a>)}</div>; })}</div>
  </>;
}

function TemplateForm({ t, onClose, onSaved }: { t: Rec | null; onClose: () => void; onSaved: () => void }) {
  const [v, setV] = useState<Rec>(t ? { ...t } : { name: '', category: '', description: '', plan: '', rollback: '', testPlan: '', risk: 'low', active: true });
  const f = useSubmit(() => { onSaved(); onClose(); }); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  return <Modal title={t ? 'Edit standard change' : 'New standard change'} onClose={onClose}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} placeholder="Monthly Windows patching" /></Field>
    <div className="grid2"><Field label="Category"><input value={v.category} onChange={(e) => set('category', e.target.value)} /></Field>
      <Field label="Risk"><select value={v.risk} onChange={(e) => set('risk', e.target.value)}><option value="low">Low</option><option value="medium">Medium</option></select></Field></div>
    <Field label="Description"><textarea rows={2} value={v.description} onChange={(e) => set('description', e.target.value)} /></Field>
    <Field label="Implementation plan"><textarea rows={3} value={v.plan} onChange={(e) => set('plan', e.target.value)} /></Field>
    <Field label="Rollback plan"><textarea rows={3} value={v.rollback} onChange={(e) => set('rollback', e.target.value)} /></Field>
    <Field label="Test plan"><textarea rows={2} value={v.testPlan} onChange={(e) => set('testPlan', e.target.value)} /></Field>
    <label className="check"><input type="checkbox" checked={v.active !== false} onChange={(e) => set('active', e.target.checked)} /> Available</label>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(() => t ? api(`/api/servicedesk/change-templates/${t.id}`, { method: 'PUT', body: v }) : api('/api/servicedesk/change-templates', { method: 'POST', body: v }))}>Save</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

function UseTemplate({ t, onClose }: { t: Rec; onClose: () => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [company, setCompany] = useState(''); const [start, setStart] = useState(''); const [end, setEnd] = useState('');
  const f = useSubmit(() => undefined);
  return <Modal title={`Raise: ${t.name}`} onClose={onClose}><div className="form">
    <p className="muted">A standard change is pre-approved: it is ready to carry out as soon as it is raised.</p>
    <Field label="Customer"><select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">Our own company</option>{((companies.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <div className="grid2"><Field label="Starts"><input type="datetime-local" value={start} onChange={(e) => setStart(e.target.value)} /></Field><Field label="Ends"><input type="datetime-local" value={end} onChange={(e) => setEnd(e.target.value)} /></Field></div>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(async () => {
      const r = await api<Rec>(`/api/servicedesk/change-templates/${t.id}/use`, { method: 'POST', body: { companyId: company ? Number(company) : undefined, startAt: start ? new Date(start).toISOString() : '', endAt: end ? new Date(end).toISOString() : '' } });
      go('servicedesk/changes/' + r.id);
    })}>Raise the change</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function StandardChanges({ admin }: { admin: boolean }) {
  const list = useLoad<Rec[]>('/api/servicedesk/change-templates'); const [edit, setEdit] = useState<Rec | null | undefined>(undefined); const [use, setUse] = useState<Rec | null>(null);
  const rows = (list.data ?? []) as Rec[];
  return <>
    <div className="dash-head"><h1>Standard changes</h1><p className="muted">Repeatable, low-risk work with a tested plan. Raised from here, they need no approval.</p></div>
    {admin && <div className="actions"><button className="primary" onClick={() => setEdit(null)}>New standard change</button></div>}
    {list.data && (rows.length === 0 ? <Empty>No standard changes yet.</Empty> : <table><thead><tr><th>Standard change</th><th>Category</th><th>Risk</th><th /></tr></thead><tbody>
      {rows.map((t) => <tr key={t.id}><td><strong>{t.name}</strong>{!t.active && <> <Badge>Off</Badge></>}<div className="muted small">{t.description}</div></td><td>{t.category}</td><td>{t.risk}</td>
        <td className="actions">{t.active && <button className="primary" onClick={() => setUse(t)}>Raise</button>}{admin && <button onClick={() => setEdit(t)}>Edit</button>}</td></tr>)}</tbody></table>)}
    {edit !== undefined && <TemplateForm t={edit} onClose={() => setEdit(undefined)} onSaved={list.reload} />}
    {use && <UseTemplate t={use} onClose={() => setUse(null)} />}
  </>;
}

export function ChangeBoard() {
  const d = useLoad<Rec>('/api/servicedesk/cab'); const [sel, setSel] = useState<string[] | null>(null); const [quorum, setQuorum] = useState<number | null>(null);
  const f = useSubmit(() => { setSel(null); setQuorum(null); d.reload(); });
  if (!d.data) return <Loading loading={d.loading} error={d.error} />;
  const cur = sel ?? ((d.data.members ?? []) as Rec[]).map((m) => String(m.id)); const q = quorum ?? Number(d.data.quorum ?? 2);
  return <Card title="Change advisory board (CAB)">
    <p className="muted">Who may approve normal changes. With nobody chosen, any staff member except the change owner may approve. Emergency changes are approved by an administrator.</p>
    <div className="grid2">{((d.data.staff ?? []) as Rec[]).map((s) => <label key={s.id} className="check"><input type="checkbox" checked={cur.includes(String(s.id))} onChange={() => setSel(cur.includes(String(s.id)) ? cur.filter((x) => x !== String(s.id)) : [...cur, String(s.id)])} /> {s.name} <span className="muted small">{s.role}</span></label>)}</div>
    <Field label="Approvals a normal change needs"><input type="number" min={1} max={9} value={q} onChange={(e) => setQuorum(Number(e.target.value))} style={{ width: 90 }} /></Field>
    <Err text={f.err} /><div className="actions"><button className="primary" disabled={f.busy} onClick={() => void f.submit(() => api('/api/servicedesk/cab', { method: 'PUT', body: { members: cur, quorum: q } }))}>Save</button></div>
  </Card>;
}
