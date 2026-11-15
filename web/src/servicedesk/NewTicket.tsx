import { useState } from 'react';
import { api, go, useLoad, type Rec } from '../http';
import { Err, Field, Card, useSubmit } from '../ui';
import { KIND, LEVEL, PRIORITY } from './List';
import { FieldInput, missingRequired, sendable, useFields } from './Fields';

/** The same table the server uses (ITIL): how many people it hurts and how soon it must be fixed. */
const PRIORITY_OF: Record<string, Record<string, string>> = { high: { high: 'critical', medium: 'high', low: 'medium' }, medium: { high: 'high', medium: 'medium', low: 'low' }, low: { high: 'medium', medium: 'low', low: 'low' } };

export default function NewTicket({ me }: { me: Rec | null }) {
  const staff = !!me?.staff;
  const settings = useLoad<Rec>('/api/servicedesk/settings');
  const companies = useLoad<Rec[]>(staff ? '/api/assets/companies' : null);
  const wanted = new URLSearchParams((location.hash.split('?')[1] ?? '')).get('kind') === 'request' ? 'request' : 'incident';
  const [v, setV] = useState<Rec>({ kind: wanted, impact: '', urgency: '', priorityChosen: false, subject: '', description: '', priority: 'medium', category: '', companyId: '', requesterName: '', requesterEmail: '' });
  const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  // impact and urgency choose the priority until someone picks one by hand
  const levels = (k: 'impact' | 'urgency', x: string) => { const n = { ...v, [k]: x }; if (!n.priorityChosen && n.impact && n.urgency) n.priority = PRIORITY_OF[n.impact]?.[n.urgency] ?? n.priority; setV(n); };
  const f = useSubmit(() => undefined); const defs = useFields(); const [cv, setCv] = useState<Rec>({});
  const cats = (settings.data?.categories ?? []) as string[]; const cos = Array.isArray(companies.data) ? companies.data : [];
  if (me && !me.canRaise) return <><h1>New ticket</h1><p className="muted">{me.customer && !me.companyId ? 'Your account is not linked to a company yet. Ask your administrator.' : 'Your account cannot open tickets.'}</p></>;
  return <>
    <h1>{v.kind === 'request' ? 'New service request' : 'New incident'}</h1>
    <Card><div className="form">
      <Field label="Type"><select value={v.kind} onChange={(e) => set('kind', e.target.value)}>{Object.entries(KIND).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
      <Field label="Subject"><input value={v.subject} onChange={(e) => set('subject', e.target.value)} maxLength={200} autoFocus /></Field>
      <Field label="What is happening?"><textarea rows={6} value={v.description} onChange={(e) => set('description', e.target.value)} /></Field>
      {v.kind === 'incident' && <div className="row">
        <Field label="Impact (how many people or services are hit)"><select value={v.impact} onChange={(e) => levels('impact', e.target.value)}><option value="">Not sure</option>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <Field label="Urgency (how soon it must be fixed)"><select value={v.urgency} onChange={(e) => levels('urgency', e.target.value)}><option value="">Not sure</option>{Object.entries(LEVEL).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
      </div>}
      <div className="row">
        <Field label="Priority"><select value={v.priority} onChange={(e) => setV({ ...v, priority: e.target.value, priorityChosen: true })}>{Object.entries(PRIORITY).map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <Field label="Category"><select value={v.category} onChange={(e) => set('category', e.target.value)}><option value="">Not sure</option>{cats.map((c) => <option key={c}>{c}</option>)}</select></Field>
      </div>
      {defs.map((d) => <FieldInput key={d.key} def={d} value={cv[d.key]} onChange={(x) => setCv({ ...cv, [d.key]: x })} />)}
      {staff && <>
        <Field label="Company"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">None</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
        <div className="row"><Field label="Raised by (name)"><input value={v.requesterName} onChange={(e) => set('requesterName', e.target.value)} /></Field><Field label="Raised by (email)"><input type="email" value={v.requesterEmail} onChange={(e) => set('requesterEmail', e.target.value)} /></Field></div>
      </>}
      <Err text={f.err} />
      <div className="actions"><button onClick={() => go('servicedesk')}>Cancel</button>
        <button className="primary" disabled={f.busy || !v.subject.trim() || !!missingRequired(defs, cv, staff)} onClick={() => f.submit(async () => {
          const body: Rec = { subject: v.subject, description: v.description, category: v.category, kind: v.kind, priority: v.priority };
          if (v.kind === 'incident') { if (v.impact) body.impact = v.impact; if (v.urgency) body.urgency = v.urgency; } const cu = sendable(defs, cv); if (Object.keys(cu).length) body.custom = cu;
          if (staff) { if (v.companyId) body.companyId = Number(v.companyId); if (v.requesterName || v.requesterEmail) { body.requesterName = v.requesterName; body.requesterEmail = v.requesterEmail; } }
          const r = await api<Rec>('/api/servicedesk/tickets', { body }); go('servicedesk/' + r.id);
        })}>Open ticket</button></div>
    </div></Card>
  </>;
}
