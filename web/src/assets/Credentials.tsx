import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';

const KINDS: [string, string][] = [['windows', 'Windows (user and password)'], ['sshpw', 'Linux (user and SSH password)'], ['ssh', 'Linux (user and SSH private key)'], ['snmp', 'Printer, switch or firewall (SNMP community)']];
const ACCOUNTS: [string, string][] = [['domain', 'Domain account'], ['service', 'Service account'], ['local', 'Local account']];
const kindName = (k: string) => KINDS.find(([x]) => x === k)?.[1] ?? k;
const acctName = (k: string) => ACCOUNTS.find(([x]) => x === k)?.[1] ?? '';

/** Logins the collectors use to read and install on computers. Each belongs to one customer (or is shared by all). */
export default function Credentials() {
  const { data, error, loading, reload } = useLoad<Rec>('/api/assets/discover/creds'); const companies = useLoad<Rec[]>('/api/assets/companies');
  const [edit, setEdit] = useState<Rec | null>(null); const [co, setCo] = useState('');
  const list = Array.isArray(companies.data) ? companies.data : [];
  const items = ((data?.items ?? []) as Rec[]).filter((c) => co === '' || (co === 'shared' ? c.companyId == null : String(c.companyId) === co));
  return <>
    <h1>Credentials</h1>
    <p className="muted">Logins the collectors use to read hardware and software details and to install the agent. A customer can have several (domain, service and local accounts); a login saved for one customer is never used for another. Passwords and keys are encrypted and never shown again.</p>
    <div className="tools"><button className="primary" onClick={() => setEdit({ kind: 'windows', accountType: 'domain', companyId: '' })}>Add credential</button>
      <select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">All customers</option><option value="shared">Shared by every customer</option>{(list as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No credentials here yet.</Empty> : <table><thead><tr><th>Customer</th><th>Name</th><th>Account</th><th>Used for</th><th>User</th><th /></tr></thead><tbody>
      {items.map((c) => <tr key={c.id}><td>{c.company || <span className="muted">Every customer</span>}</td><td>{c.name}</td><td>{acctName(c.accountType)}</td><td>{kindName(c.kind)}</td><td>{c.kind === 'snmp' ? '' : c.username}</td>
        <td><button onClick={() => setEdit({ ...c, companyId: c.companyId ?? '' })}>Edit</button> <Act label="Delete" danger confirm={`Delete the credential "${c.name}"?`} run={async () => { await api(`/api/assets/discover/creds/${c.id}`, { method: 'DELETE' }); reload(); }} /></td></tr>)}</tbody></table>)}
    {edit && <CredForm init={edit} companies={list as Rec[]} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}

function CredForm({ init, companies, onClose }: { init: Rec; companies: Rec[]; onClose: (changed: boolean) => void }) {
  const isNew = !init.id; const [v, setV] = useState({ name: init.name ?? '', kind: init.kind ?? 'windows', accountType: init.accountType ?? 'domain', companyId: String(init.companyId ?? ''), username: init.username ?? '', secret: '' });
  const f = useSubmit(() => onClose(true)); const set = (k: string, x: string) => setV({ ...v, [k]: x });
  const body = { ...v, companyId: v.companyId ? Number(v.companyId) : null };
  return <Modal title={isNew ? 'Add credential' : 'Edit credential'} onClose={() => onClose(false)}><div className="form">
    <Field label="Customer"><select value={v.companyId} onChange={(e) => set('companyId', e.target.value)}><option value="">Every customer (shared)</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} placeholder="Domain admin (read only)" autoFocus /></Field>
    <div className="row"><Field label="Account"><select value={v.accountType} onChange={(e) => set('accountType', e.target.value)}>{ACCOUNTS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
      <Field label="Used for"><select value={v.kind} disabled={!isNew} onChange={(e) => set('kind', e.target.value)}>{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field></div>
    {v.kind !== 'snmp' && <Field label="User name"><input value={v.username} onChange={(e) => set('username', e.target.value)} autoComplete="off" placeholder={v.accountType === 'domain' ? 'DOMAIN\\user' : v.accountType === 'local' ? '.\\Administrator' : ''} /></Field>}
    <Field label={v.kind === 'ssh' ? 'Private key' : v.kind === 'snmp' ? 'Community string' : 'Password'}>{v.kind === 'ssh' ? <textarea rows={5} value={v.secret} onChange={(e) => set('secret', e.target.value)} /> : <input type="password" value={v.secret} onChange={(e) => set('secret', e.target.value)} autoComplete="new-password" />}</Field>
    {!isNew && <p className="muted">Leave the password or key empty to keep the saved one.</p>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim()} onClick={() => f.submit(() => api(isNew ? '/api/assets/discover/creds' : `/api/assets/discover/creds/${init.id}`, { method: isNew ? 'POST' : 'PUT', body }))}>Save</button></div></div></Modal>;
}
