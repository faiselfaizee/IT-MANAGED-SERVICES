import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Modal, fmtDate, useSubmit, str } from '../ui';

const RIGHTS: [string, string][] = [['read', 'Look'], ['unlock', 'Unlock'], ['reset', 'Reset passwords'], ['enable', 'Enable / disable'], ['modify', 'Change details'], ['members', 'Group members'], ['create', 'Create'], ['move', 'Move'], ['delete', 'Delete'], ['approve', 'Approve others']];

function Form({ d, onClose, onSaved }: { d: Rec | null; onClose: () => void; onSaved: () => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [companyId, setCompanyId] = useState(d ? Number(d.companyId) : 0);
  const ctrls = useLoad<Rec[]>(`/api/adsuite/controllers?companyId=${companyId}`);
  const [f, setF] = useState<Rec>({ name: d?.name ?? '', baseDn: d?.baseDn ?? '', servers: d?.servers ?? '', tls: d?.tls ?? true, skipVerify: d?.skipVerify ?? false, bindDn: d?.bindDn ?? '', password: '', controllerId: d?.controllerId ?? '', enabled: d?.enabled ?? true });
  const set = (k: string, v: unknown) => setF({ ...f, [k]: v });
  const { err, busy, submit } = useSubmit(() => { onSaved(); onClose(); });
  return <Modal title={d ? 'Edit domain' : 'Add a domain'} onClose={onClose}>
    <div className="form">
      <Field label="Company"><select value={companyId} disabled={!!d} onChange={(e) => setCompanyId(Number(e.target.value))}><option value={0}>Choose…</option>{((companies.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
      <Field label="Name"><input value={f.name} onChange={(e) => set('name', e.target.value)} placeholder="corp.example.com" /></Field>
      <Field label="Base DN"><input value={f.baseDn} onChange={(e) => set('baseDn', e.target.value)} placeholder="DC=corp,DC=example,DC=com" /></Field>
      <Field label="Domain controllers"><input value={f.servers} onChange={(e) => set('servers', e.target.value)} placeholder="dc01.corp.example.com, dc02" /></Field>
      <label className="check"><input type="checkbox" checked={f.tls} onChange={(e) => set('tls', e.target.checked)} /> Use LDAPS (needed to set passwords)</label>
      <label className="check"><input type="checkbox" checked={f.skipVerify} onChange={(e) => set('skipVerify', e.target.checked)} /> Do not check the domain controller's certificate (not recommended; use "Trust this certificate" after a test instead)</label>
      <p className="muted small">Without LDAPS, passwords can still be set when the site controller runs on Windows: it uses Windows' own signed and sealed channel. Give the service account as user@domain or DOMAIN\user for that.</p>
      <Field label="Service account"><input value={f.bindDn} onChange={(e) => set('bindDn', e.target.value)} placeholder="svc-deskask@corp.example.com" /></Field>
      <Field label={d ? 'Password (leave empty to keep)' : 'Password'}><input type="password" autoComplete="new-password" value={f.password} onChange={(e) => set('password', e.target.value)} /></Field>
      <Field label="Site controller"><select value={f.controllerId} onChange={(e) => set('controllerId', e.target.value)}><option value="">Choose…</option>
        {((ctrls.data ?? []) as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}{c.online ? '' : ' (offline)'}</option>)}</select></Field>
      <label className="check"><input type="checkbox" checked={f.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Switched on</label>
      <Err text={err} />
      <div className="tools"><button className="primary" disabled={busy || !companyId} onClick={() => void submit(() => d
        ? api(`/api/adsuite/domains/${d.id}`, { method: 'PUT', body: { ...f, companyId } })
        : api('/api/adsuite/domains', { method: 'POST', body: { ...f, companyId } }))}>Save</button><button onClick={onClose}>Cancel</button></div>
    </div>
  </Modal>;
}

/** Who may do what in one domain (also shown on the company's AD permissions tab). */
export function Delegation({ d }: { d: Rec }) {
  const rows = useLoad<Rec[]>(`/api/adsuite/domains/${d.id}/delegations`);
  const who = useLoad<Rec>('/api/adsuite/principals');
  const [kind, setKind] = useState('user'); const [pid, setPid] = useState(''); const [ou, setOu] = useState(d.baseDn as string); const [rights, setRights] = useState<string[]>(['read']);
  const people = ((kind === 'user' ? who.data?.users : who.data?.groups) ?? []) as Rec[];
  const { err, submit } = useSubmit(() => rows.reload());
  const toggle = (r: string) => setRights(rights.includes(r) ? rights.filter((x) => x !== r) : [...rights, r]);
  return <Card title={`Who may do what in ${d.name}`}>
    {((rows.data ?? []) as Rec[]).length === 0 ? <Empty>Only administrators can use this domain until you add someone.</Empty> :
      <table><thead><tr><th>Person or group</th><th>Where</th><th>May</th><th /></tr></thead><tbody>
        {((rows.data ?? []) as Rec[]).map((r) => <tr key={r.id}><td>{r.name} <span className="muted">({r.kind})</span></td><td><code>{r.ou}</code></td>
          <td>{(r.rights as string[]).map((x) => RIGHTS.find(([k]) => k === x)?.[1] ?? x).join(', ')}</td>
          <td><Act label="Remove" danger confirm="Remove this?" run={async () => { await api(`/api/adsuite/delegations/${r.id}`, { method: 'DELETE' }); rows.reload(); }} /></td></tr>)}</tbody></table>}
    <div className="tools" style={{ marginTop: 12 }}>
      <select aria-label="Kind" value={kind} onChange={(e) => { setKind(e.target.value); setPid(''); }}><option value="user">Person</option><option value="group">Group</option></select>
      <select aria-label="Who" value={pid} onChange={(e) => setPid(e.target.value)}><option value="">Choose…</option>{people.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}</select>
      <input aria-label="Where" value={ou} onChange={(e) => setOu(e.target.value)} style={{ minWidth: 280 }} />
    </div>
    <div className="tools">{RIGHTS.map(([k, l]) => <label key={k} className="check"><input type="checkbox" checked={rights.includes(k)} disabled={k === 'read'} onChange={() => toggle(k)} /> {l}</label>)}</div>
    <p className="muted small">Deleting, and changing privileged accounts or groups, always waits for a second person who may approve.</p>
    <Err text={err} />
    <button className="primary" disabled={!pid} onClick={() => void submit(() => api(`/api/adsuite/domains/${d.id}/delegations`, { method: 'POST', body: { kind, principalId: pid, ou, rights } }))}>Add</button>
  </Card>;
}

export default function Domains({ domains, onChanged }: { domains: Rec[]; onChanged: () => void }) {
  const [edit, setEdit] = useState<Rec | null | undefined>(undefined); const [res, setRes] = useState<Record<number, Rec>>({}); const [open, setOpen] = useState<number | null>(null);
  return <>
    <div className="tools"><button className="primary" onClick={() => setEdit(null)}>Add a domain</button></div>
    {domains.length === 0 ? <Empty>Add the customer's domain, the site controller that can reach it and a service account.</Empty> :
      <table><thead><tr><th>Domain</th><th>Company</th><th>Controller</th><th>Status</th><th /></tr></thead><tbody>
        {domains.map((d) => <tr key={d.id}><td><strong>{d.name}</strong><div className="muted small">{d.servers}</div></td><td>{d.company}</td>
          <td>{d.controller || '–'} {d.controller && <Badge tone={d.controllerOnline ? 'ok' : 'bad'}>{d.controllerOnline ? 'Online' : 'Offline'}</Badge>}</td>
          <td>{!d.enabled ? <Badge>Off</Badge> : d.lastError ? <Badge tone="bad">Failed</Badge> : d.lastOk ? <Badge tone="ok">Working</Badge> : <Badge>Not tested</Badge>}
            {(res[d.id] ?? null) && <TestResult r={res[d.id]} d={d} onTrusted={async () => { const r = await api<Rec>(`/api/adsuite/domains/${d.id}/test`, { method: 'POST', body: {} }); setRes({ ...res, [d.id]: r }); onChanged(); }} />}
            {!res[d.id] && d.lastError && <div className="err">{d.lastError}</div>}</td>
          <td className="tools"><Act label="Test" run={async () => { const r = await api<Rec>(`/api/adsuite/domains/${d.id}/test`, { method: 'POST', body: {} }); setRes({ ...res, [d.id]: r }); onChanged(); }} />
            <button onClick={() => setEdit(d)}>Edit</button><button onClick={() => setOpen(open === d.id ? null : d.id)}>Who may</button>
            <Act label="Delete" danger confirm={`Remove ${d.name} from DeskAsk? Nothing is deleted in the directory.`} run={async () => { await api(`/api/adsuite/domains/${d.id}`, { method: 'DELETE' }); onChanged(); }} /></td></tr>)}</tbody></table>}
    {open !== null && domains.find((d) => d.id === open) && <Delegation d={domains.find((d) => d.id === open)!} />}
    {edit !== undefined && <Form d={edit} onClose={() => setEdit(undefined)} onSaved={onChanged} />}
  </>;
}

const PROBLEM: Record<string, string> = {
  no_certificate: 'The domain controller has no LDAPS certificate',
  name_mismatch: 'The certificate is for another name',
  untrusted: 'The certificate is not trusted by the controller',
  expired: 'The certificate has expired',
  pin_changed: 'The certificate changed since you trusted it',
  tls_version: 'The domain controller offers an old TLS version',
};

/** The result of Test, with the certificate when LDAPS failed on it, and a way to trust that certificate. */
function TestResult({ r, d, onTrusted }: { r: Rec; d: Rec; onTrusted: () => Promise<void> }) {
  const cert = r.cert as Rec | null; const kind = str(r, 'tlsProblem');
  if (r.ok) return <div className="muted small">Signed in to {str(r, 'host') || str(r, 'server')}{r.tls ? ' over LDAPS' : ' without encryption'}.{r.trusted ? ' Using the certificate you chose to trust.' : ''}</div>;
  return <div className="err small">
    {kind && <strong>{PROBLEM[kind] ?? 'LDAPS failed'}. </strong>}{str(r, 'error')} {str(r, 'hint')}
    {cert && <div className="card" style={{ marginTop: 8, color: 'var(--text)' }}>
      <Dl rows={[['Issued to', String(cert.subject ?? '')], ['Names', ((cert.names ?? []) as string[]).join(', ')], ['Issued by', String(cert.issuer ?? '')], ['Valid until', cert.notAfter ? fmtDate(cert.notAfter) : ''], ['SHA-256', <code key="f" style={{ wordBreak: 'break-all' }}>{String(cert.sha256 ?? '')}</code>]]} />
      {(kind === 'untrusted' || kind === 'name_mismatch' || kind === 'pin_changed') && <>
        <p className="muted small">Compare the SHA-256 with the certificate on the domain controller (certlm.msc › Personal › Certificates) before trusting it.</p>
        <Act label="Trust this certificate" primary run={async () => { await api(`/api/adsuite/domains/${d.id}/trust`, { method: 'POST', body: { sha256: cert.sha256 } }); await onTrusted(); }} /></>}
    </div>}
  </div>;
}
