import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Badge, Err, Field, Loading, Modal } from '../ui';

const when = (s?: string | null) => (s ? new Date(s).toLocaleString() : 'never');

/** Devices setup › a customer › Domain controller: where DeskAsk reads the customer's people from (read only), through the controller installed in their network. */
export default function DirectorySetup({ company, onClose }: { company: { id: number; name: string }; onClose: () => void }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/directory/companies/${company.id}`);
  const d = data?.domain as Rec | null | undefined;
  const [v, setV] = useState<Rec | null>(null); const [msg, setMsg] = useState(''); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const form = v ?? { servers: (d?.servers ?? '').replace(/\n/g, '\n'), baseDn: d?.baseDn ?? '', bindDn: d?.bindDn ?? '', password: '', controllerId: d?.controllerId ?? (data?.controllers?.length === 1 ? data.controllers[0].id : ''), tls: d?.tls ?? true };
  const set = (k: string, x: unknown) => setV({ ...form, [k]: x });
  const run = async (fn: () => Promise<Rec | void>, done?: (r: Rec) => string) => {
    setBusy(true); setErr(''); setMsg('');
    try { const r = (await fn()) ?? {}; if (done) setMsg(done(r as Rec)); setV(null); reload(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const ctrls = (data?.controllers ?? []) as Rec[];
  return <Modal title={`Domain controller · ${company.name}`} onClose={onClose}><div className="form">
    <p className="muted">DeskAsk reads the people (name, email, department) of this customer's domain controller, only to read, so they can be listed under Inventory › People and chosen when assets are handed out. The controller you pick must be installed in the customer's network.</p>
    <Loading loading={loading && !data} error={error} />
    {data && <>
      {d && <p>{d.lastError ? <Badge>{'Last test failed'}</Badge> : d.lastOk ? <Badge tone="ok">Connected</Badge> : <Badge>Not tested yet</Badge>}{' '}
        <span className="muted">{d.people} people · last read {when(d.syncedAt)}</span></p>}
      {d?.syncError && <Err text={d.syncError} />}
      <Field label="Domain controllers (one per line)"><textarea rows={2} value={form.servers} onChange={(e) => set('servers', e.target.value)} placeholder="dc01.contoso.com" /></Field>
      <Field label="Base DN"><input value={form.baseDn} onChange={(e) => set('baseDn', e.target.value)} placeholder="DC=contoso,DC=com" /></Field>
      <Field label="Read-only account"><input value={form.bindDn} onChange={(e) => set('bindDn', e.target.value)} placeholder="svc-deskask@contoso.com" /></Field>
      <Field label={d ? 'Password (leave empty to keep it)' : 'Password'}><input type="password" autoComplete="new-password" value={form.password} onChange={(e) => set('password', e.target.value)} /></Field>
      <Field label="Controller in the customer's network"><select value={form.controllerId} onChange={(e) => set('controllerId', e.target.value)}>
        <option value="">Choose…</option>{ctrls.map((c) => <option key={c.id} value={c.id}>{c.name}{c.online ? '' : ' (offline)'}</option>)}</select></Field>
      <label><input type="checkbox" checked={form.tls} onChange={(e) => set('tls', e.target.checked)} /> Use a secure connection (LDAPS)</label>
      {msg && <p role="status">{msg}</p>}<Err text={err} />
      <div className="actions">
        {d && <button disabled={busy} onClick={() => { if (confirm('Remove this domain controller? The people read from it are removed too.')) void run(() => api(`/api/directory/companies/${company.id}`, { method: 'DELETE' }) as Promise<Rec>, () => 'Removed.'); }}>Remove</button>}
        {d && <button disabled={busy} onClick={() => void run(() => api<Rec>(`/api/directory/companies/${company.id}/test`, { method: 'POST', body: {} }), (r) => (r.ok ? 'Connected.' : (r.error || 'The test failed.') + (r.hint ? ' ' + r.hint : '')))}>Test</button>}
        {d && <button disabled={busy} onClick={() => void run(() => api<Rec>(`/api/directory/companies/${company.id}/sync`, { method: 'POST', body: {} }), (r) => (r.ok ? `${r.count} people read.` : r.error || 'The read failed.'))}>Sync now</button>}
        <button className="primary" disabled={busy || !form.servers.trim() || !form.baseDn.trim() || !form.bindDn.trim() || !form.controllerId || (!d && !form.password)}
          onClick={() => void run(() => api<Rec>(`/api/directory/companies/${company.id}`, { method: 'PUT', body: { servers: form.servers, baseDn: form.baseDn, bindDn: form.bindDn, password: form.password, controllerId: form.controllerId, tls: form.tls } }), () => 'Saved. Press Test, then Sync now.')}>Save</button>
      </div>
    </>}
  </div></Modal>;
}
