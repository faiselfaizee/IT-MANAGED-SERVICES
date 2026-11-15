import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDate, useSubmit } from '../ui';
import { DomainPick, useDomain } from './AdApp';

const tone = (s: string) => (s === 'approved' || s === 'done' ? 'ok' : s === 'rejected' || s === 'failed' ? 'bad' : 'warn') as 'ok' | 'warn' | 'bad';

function Shown({ r, onClose }: { r: Rec; onClose: () => void }) {
  return <Modal title="Done" onClose={onClose}>{r.dn && <p>Created <code>{String(r.dn)}</code>.</p>}
    {r.password ? <><p>The password is shown once. Give it to the person safely.</p><p><code className="copy">{String(r.password)}</code></p></> : null}
    <button className="primary" onClick={onClose}>Close</button></Modal>;
}

function New({ domains, onClose, onSaved }: { domains: Rec[]; onClose: () => void; onSaved: () => void }) {
  const [domain, pick] = useDomain(domains);
  const [f, setF] = useState<Rec>({ kind: 'internal', ou: '', sam: '', first: '', last: '', email: '', title: '', department: '', expires: '', notes: '' });
  const set = (k: string, v: string) => setF({ ...f, [k]: v });
  const { err, busy, submit } = useSubmit(() => { onSaved(); onClose(); });
  return <Modal title="Ask for a new account" onClose={onClose}><div className="form">
    <DomainPick domains={domains} value={domain} onPick={pick} />
    <Field label="Kind"><select value={f.kind} onChange={(e) => set('kind', e.target.value)}><option value="internal">Employee</option><option value="external">External (needs an end date)</option><option value="service">Service account</option></select></Field>
    <Field label="OU for the account"><input value={f.ou} onChange={(e) => set('ou', e.target.value)} placeholder={domain ? `OU=Staff,${domain.baseDn}` : ''} /></Field>
    {f.kind !== 'service' && <><Field label="First name"><input value={f.first} onChange={(e) => set('first', e.target.value)} /></Field><Field label="Last name"><input value={f.last} onChange={(e) => set('last', e.target.value)} /></Field>
      <Field label="Email"><input value={f.email} onChange={(e) => set('email', e.target.value)} /></Field><Field label="Title"><input value={f.title} onChange={(e) => set('title', e.target.value)} /></Field>
      <Field label="Department"><input value={f.department} onChange={(e) => set('department', e.target.value)} /></Field></>}
    <Field label="Sign-in name"><input value={f.sam} onChange={(e) => set('sam', e.target.value)} /></Field>
    {f.kind !== 'service' && <Field label="Last day, year-month-day"><input value={f.expires} onChange={(e) => set('expires', e.target.value)} /></Field>}
    <Field label={f.kind === 'service' ? 'What it is for' : 'Notes'}><input value={f.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    <Err text={err} /><div className="tools"><button className="primary" disabled={busy || !domain} onClick={() => void submit(() => api('/api/adsuite/requests', { method: 'POST', body: { ...f, domainId: domain!.id } }))}>Send</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

function Reject({ onGo, onClose }: { onGo: (note: string) => Promise<unknown>; onClose: () => void }) {
  const [n, setN] = useState(''); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title="Turn down" onClose={onClose}><Field label="Why (optional)"><input value={n} onChange={(e) => setN(e.target.value)} /></Field><Err text={err} />
    <div className="tools"><button className="primary" disabled={busy} onClick={() => void submit(() => onGo(n))}>Turn down</button><button onClick={onClose}>Cancel</button></div></Modal>;
}

export default function Requests({ domains, mode }: { domains: Rec[]; mode: 'requests' | 'approvals' }) {
  const list = useLoad<Rec[]>(`/api/adsuite/${mode}`);
  const [adding, setAdding] = useState(false); const [rej, setRej] = useState<Rec | null>(null); const [shown, setShown] = useState<Rec | null>(null);
  const base = `/api/adsuite/${mode}`; const rows = (list.data ?? []) as Rec[];
  const decide = async (r: Rec, what: 'approve' | 'reject', body: Rec = {}) => { const out = await api<Rec>(`${base}/${r.id}/${what}`, { method: 'POST', body }); list.reload(); return out; };
  return <>
    {mode === 'requests' && <div className="tools"><button className="primary" onClick={() => setAdding(true)}>Ask for a new account</button></div>}
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (rows.length === 0 ? <Empty>{mode === 'requests' ? 'No account requests.' : 'Nothing is waiting for approval.'}</Empty> :
      <Card><table><thead><tr><th>{mode === 'requests' ? 'Account' : 'Change'}</th><th>Domain</th><th>Asked by</th><th>Status</th><th /></tr></thead><tbody>
        {rows.map((r) => <tr key={r.id as number}>
          <td>{mode === 'requests' ? <><strong>{r.display || `${r.first} ${r.last}`.trim() || r.sam}</strong> <span className="muted">({r.kind})</span><div className="muted small">{r.sam} · <code>{r.ou}</code>{r.expires ? ` · until ${r.expires}` : ''}</div>{r.notes && <div className="small">{r.notes}</div>}</>
            : <><strong>{String(r.action).replace(/-/g, ' ')}</strong> <span className="muted">{r.target}</span>{r.reason && <div className="small">{r.reason}</div>}</>}</td>
          <td>{r.domain}</td><td>{r.requestedBy}<div className="muted small">{fmtDate(r.createdAt)}</div></td>
          <td><Badge tone={tone(String(r.status))}>{r.status}</Badge>{r.decidedBy && <div className="muted small">{r.decidedBy}{r.decisionNote ? `: ${r.decisionNote}` : ''}</div>}{r.error && <div className="err">{r.error}</div>}</td>
          <td className="tools">{r.status === 'pending' && r.canApprove && <>
            <Act label="Approve" primary run={async () => { const out = await decide(r, 'approve'); if (out.password || out.dn) setShown(out); }} /><button onClick={() => setRej(r)}>Turn down</button></>}
            {r.status === 'pending' && !r.canApprove && r.mine && <span className="muted small">Waiting for someone else</span>}</td></tr>)}</tbody></table></Card>)}
    {adding && <New domains={domains} onClose={() => setAdding(false)} onSaved={list.reload} />}
    {rej && <Reject onClose={() => setRej(null)} onGo={(note) => decide(rej, 'reject', { note })} />}
    {shown && <Shown r={shown} onClose={() => setShown(null)} />}
  </>;
}
