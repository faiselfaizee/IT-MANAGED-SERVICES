import { useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, useSubmit } from './ui';

const ROLES: [string, string][] = [
  ['admin', 'Administrator'], ['engineer', 'Engineer'], ['viewer', 'Viewer'],
  ['cadmin', 'Customer administrator'], ['customer', 'Customer'],
];
const roleName = (r: string) => ROLES.find(([k]) => k === r)?.[1] ?? r;

export default function Users({ meId, embedded = false }: { meId?: string; embedded?: boolean }) {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/platform/users');
  const [form, setForm] = useState<Rec | null>(null); const [pw, setPw] = useState<Rec | null>(null); const [sso, setSso] = useState<'' | 'settings' | 'import' | 'adsettings' | 'adimport'>('');
  const list = Array.isArray(data) ? data : [];
  return <>
    {embedded ? null : <h1>Users</h1>}
    <div className="tools"><button className="primary" onClick={() => setForm({ email: '', name: '', role: 'engineer', password: '' })}>Add user</button>
      <button onClick={() => setSso('import')}>Import from Microsoft</button><button onClick={() => setSso('settings')}>Microsoft sign-in…</button>
      <button onClick={() => setSso('adimport')}>Import from Active Directory</button><button onClick={() => setSso('adsettings')}>Active Directory…</button>
      <span className="muted">Staff sign in as administrator, engineer or viewer. Customer users see only their own company's tickets.</span></div>
    <Loading loading={loading && !data} error={error} />
    {data && (list.length === 0 ? <Empty>No users yet.</Empty> : <table><thead><tr><th>Name</th><th>Role</th><th>Two-step</th><th>Status</th><th /></tr></thead><tbody>
      {list.map((u) => <tr key={u.id}>
        <td>{u.name}<div className="muted">{u.email}</div></td>
        <td>{roleName(u.role)}</td>
        <td>{u.twoStep ? <Badge tone="ok">On</Badge> : <span className="muted">Off</span>}</td>
        <td><Badge tone={u.active ? 'ok' : undefined}>{u.active ? 'Active' : 'Disabled'}</Badge></td>
        <td><button onClick={() => setForm(u)}>Edit</button> <button onClick={() => setPw(u)}>Set password</button>{' '}
          {u.twoStep && <Act label="Reset two-step" confirm={`Turn off two-step sign-in for ${u.name}? They can set it up again.`} run={async () => { await api(`/api/platform/users/${u.id}/two-step/reset`, { method: 'POST', body: {} }); reload(); }} />}{' '}
          {u.id !== meId && <Act label={u.active ? 'Disable' : 'Enable'} danger={u.active} confirm={u.active ? `Disable ${u.name}? They can no longer sign in.` : undefined}
            run={async () => { await api(`/api/platform/users/${u.id}`, { method: 'PATCH', body: { active: !u.active } }); reload(); }} />}</td>
      </tr>)}</tbody></table>)}
    {form && <UserForm init={form} self={form.id === meId} onClose={(ch) => { setForm(null); if (ch) reload(); }} />}
    {pw && <PasswordForm user={pw} onClose={() => setPw(null)} />}
    {sso === 'settings' && <SsoSettings onClose={() => setSso('')} />}
    {sso === 'import' && <ImportEntra onClose={(ch) => { setSso(''); if (ch) reload(); }} />}
    {sso === 'adsettings' && <AdSettings onClose={() => setSso('')} />}
    {sso === 'adimport' && <ImportEntra base="/api/platform/ad" title="Import from Active Directory" onClose={(ch) => { setSso(''); if (ch) reload(); }} />}
  </>;
}

function UserForm({ init, self, onClose }: { init: Rec; self: boolean; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>(init); const f = useSubmit(() => onClose(true)); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const isNew = !init.id;
  const cust = v.role === 'customer' || v.role === 'cadmin';
  const cos = useLoad<Rec[]>(cust ? '/api/assets/companies' : null);
  const people = useLoad<Rec[]>(cust && !isNew ? '/api/servicedesk/people' : null);
  const [co, setCo] = useState<string | null>(null);
  const row = (Array.isArray(people.data) ? people.data : []).find((p) => p.id === init.id);
  const company = co ?? (row?.companyId != null ? String(row.companyId) : '');
  const list = Array.isArray(cos.data) ? cos.data : [];
  const link = async (id: string) => { if (cust && (isNew ? company : co !== null)) await api(`/api/servicedesk/people/${id}`, { method: 'PUT', body: { companyId: company ? Number(company) : null, mayRaise: row?.mayRaise ?? true, mayModify: row?.mayModify ?? true } }); };
  return <Modal title={isNew ? 'Add user' : 'Edit user'} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field>
    <Field label="Email"><input type="email" value={v.email} disabled={!isNew} onChange={(e) => set('email', e.target.value)} /></Field>
    <Field label="Role"><select value={v.role} disabled={self} onChange={(e) => set('role', e.target.value)}>{ROLES.map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
    {cust && <Field label="Company"><select value={company} onChange={(e) => setCo(e.target.value)}><option value="">Not linked yet</option>{list.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>}
    {cust && !cos.loading && list.length === 0 && <p className="muted">There are no companies yet. Add them in Assets, Companies, then link this person here or in Service Desk, Customer users. Until a person is linked to a company they cannot open tickets.</p>}
    {isNew && <Field label="Password (at least 10 characters)"><input type="password" autoComplete="new-password" value={v.password} onChange={(e) => set('password', e.target.value)} /></Field>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim() || !v.email.trim() || (isNew && v.password.length < 10)}
        onClick={() => f.submit(async () => {
          if (isNew) { const r = await api<Rec>('/api/platform/users', { body: { email: v.email, name: v.name, role: v.role, password: v.password } }); await link(String(r.id)); }
          else { await api(`/api/platform/users/${init.id}`, { method: 'PATCH', body: { name: v.name, ...(self ? {} : { role: v.role }) } }); await link(String(init.id)); }
        })}>{isNew ? 'Add user' : 'Save changes'}</button></div>
  </div></Modal>;
}

function PasswordForm({ user, onClose }: { user: Rec; onClose: () => void }) {
  const [p, setP] = useState(''); const f = useSubmit(onClose);
  return <Modal title={`Set password for ${user.name}`} onClose={onClose}><div className="form">
    <Field label="New password (at least 10 characters)"><input type="password" autoComplete="new-password" value={p} onChange={(e) => setP(e.target.value)} autoFocus /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={f.busy || p.length < 10} onClick={() => f.submit(() => api(`/api/platform/users/${user.id}/password`, { body: { password: p } }))}>Set password</button></div>
  </div></Modal>;
}

/** Setting up sign-in with Microsoft Entra ID: the app registration's ids and secret, and who may be created at their first sign-in. */
function SsoSettings({ onClose }: { onClose: () => void }) {
  const { data } = useLoad<Rec>('/api/platform/sso');
  if (!data) return <Modal title="Sign in with Microsoft" onClose={onClose}><Loading loading error="" /></Modal>;
  return <SsoForm init={data} onClose={onClose} />;
}
function SsoForm({ init, onClose }: { init: Rec; onClose: () => void }) {
  const [v, setV] = useState<Rec>({ enabled: init.enabled, tenantId: init.tenantId, clientId: init.clientId, secret: '', domains: init.domains, autoCreate: init.autoCreate, defaultRole: init.defaultRole });
  const [note, setNote] = useState(''); const f = useSubmit(() => onClose()); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const save = () => api('/api/platform/sso', { method: 'PUT', body: { ...v, secret: v.secret || undefined } });
  return <Modal title="Sign in with Microsoft" onClose={onClose}><div className="form">
    <p className="muted small">In the Microsoft Entra admin centre create an app registration (App registrations › New). Add the web redirect address below, create a client secret, and under API permissions add the Microsoft Graph <em>application</em> permission User.Read.All with admin consent (only needed to import users).</p>
    <Field label="Redirect address to register"><input value={init.redirectUri} readOnly onFocus={(e) => e.target.select()} /></Field>
    <Field label="Directory (tenant) id"><input value={v.tenantId} onChange={(e) => set('tenantId', e.target.value)} spellCheck={false} /></Field>
    <Field label="Application (client) id"><input value={v.clientId} onChange={(e) => set('clientId', e.target.value)} spellCheck={false} /></Field>
    <Field label={init.hasSecret ? 'Client secret (saved; type a new one to replace it)' : 'Client secret'}><input type="password" value={v.secret} onChange={(e) => set('secret', e.target.value)} autoComplete="new-password" /></Field>
    <label><input type="checkbox" checked={v.autoCreate} onChange={(e) => set('autoCreate', e.target.checked)} /> Create an account at first sign-in for people who have none</label>
    {v.autoCreate && <div className="row"><Field label="Only these email domains (blank: any in the directory)"><input value={v.domains} onChange={(e) => set('domains', e.target.value)} placeholder="acme.com, acme.in" /></Field>
      <Field label="Role of new people"><select value={v.defaultRole} onChange={(e) => set('defaultRole', e.target.value)}>{ROLES.filter(([k]) => k !== 'admin').map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field></div>}
    <label><input type="checkbox" checked={v.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Show "Sign in with Microsoft" on the sign-in page</label>
    <p className="muted small">People are matched by their Microsoft email. Microsoft's own sign-in, including its multi-factor step, replaces the two-step code of DeskAsk for them. Password sign-in keeps working.</p>
    {note && <p className="note" role="status">{note}</p>}
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
      <button disabled={f.busy} onClick={() => f.submit(async () => { await save(); const r = await api<Rec>('/api/platform/sso/test', { method: 'POST', body: {} }).then(() => 'The app can read the directory.', (e) => (e as Error).message); setNote(r); throw new Error(''); })}>Save and test</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(save)}>Save</button></div>
  </div></Modal>;
}

/** Choose people from the Microsoft directory and give them DeskAsk accounts. */
function ImportEntra({ onClose, base = '/api/platform/sso/entra', title = 'Import from Microsoft' }: { onClose: (changed: boolean) => void; base?: string; title?: string }) {
  const { data, error, loading } = useLoad<Rec>(`${base}/users`);
  const [sel, setSel] = useState<Set<string>>(new Set()); const [role, setRole] = useState('engineer'); const [q, setQ] = useState(''); const [done, setDone] = useState('');
  const f = useSubmit(() => undefined);
  const items = ((data?.items ?? []) as Rec[]).filter((u) => !q || `${u.name} ${u.email}`.toLowerCase().includes(q.toLowerCase()));
  const pick = (u: Rec) => { const n = new Set(sel); if (n.has(u.id)) n.delete(u.id); else n.add(u.id); setSel(n); };
  return <Modal title={title} onClose={() => onClose(done !== '')}><div className="form">
    {done ? <><p>{done}</p><div className="actions"><button className="primary" onClick={() => onClose(true)}>Close</button></div></> : <>
      <Loading loading={loading && !data} error={error} />
      {data && <>
        <Field label="Find"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name or email" /></Field>
        <div style={{ maxHeight: 280, overflow: 'auto', border: '1px solid var(--line)', borderRadius: 6, padding: 6 }}>
          {items.length === 0 && <p className="muted">Nobody found.</p>}
          {items.map((u) => <label key={u.id} style={{ display: 'block' }}><input type="checkbox" disabled={u.exists} checked={sel.has(u.id)} onChange={() => pick(u)} /> {u.name} <span className="muted">{u.email}</span> {u.exists && <Badge tone="ok">has an account</Badge>} {u.guest && <Badge>guest</Badge>} {!u.enabled && <Badge tone="bad">disabled in Microsoft</Badge>}</label>)}
        </div>
        <Field label={`Role for the ${sel.size} chosen`}><select value={role} onChange={(e) => setRole(e.target.value)}>{ROLES.map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
        <p className="muted small">Imported people sign in with their directory account. For customer roles, link the person to a company afterwards (Edit).</p>
        <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
          <button className="primary" disabled={f.busy || sel.size === 0} onClick={() => f.submit(async () => { const r = await api<Rec>(`${base}/import`, { body: { role, users: ((data.items ?? []) as Rec[]).filter((u) => sel.has(u.id)) } }); setDone(`${r.created} added, ${r.linked} linked, ${r.skipped} skipped.`); })}>Import {sel.size}</button></div></>}
    </>}
  </div></Modal>;
}

/** How DeskAsk reaches Active Directory: server, base DN and a read-only account. People imported from it sign in with their AD password. */
function AdSettings({ onClose }: { onClose: () => void }) {
  const { data } = useLoad<Rec>('/api/platform/ad');
  if (!data) return <Modal title="Active Directory" onClose={onClose}><Loading loading error="" /></Modal>;
  return <AdForm init={data} onClose={onClose} />;
}
function AdForm({ init, onClose }: { init: Rec; onClose: () => void }) {
  const [v, setV] = useState<Rec>({ enabled: init.enabled, server: init.server, useSsl: init.useSsl, trustAnyCertificate: init.trustAnyCertificate, baseDn: init.baseDn, bindUser: init.bindUser, bindPassword: '', filter: init.filter, signIn: init.signIn });
  const [note, setNote] = useState(''); const f = useSubmit(() => onClose()); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const save = () => api('/api/platform/ad', { method: 'PUT', body: { ...v, bindPassword: v.bindPassword || undefined } });
  return <Modal title="Active Directory" onClose={onClose}><div className="form">
    <p className="muted small">The DeskAsk server must reach a domain controller. Use LDAPS (port 636) where you can. Give DeskAsk an ordinary read-only account; it only reads users and checks passwords.</p>
    <div className="row"><Field label="Domain controller (address or address:port)"><input value={v.server} onChange={(e) => set('server', e.target.value)} placeholder="dc01.acme.local" spellCheck={false} /></Field>
      <Field label="Base DN to search"><input value={v.baseDn} onChange={(e) => set('baseDn', e.target.value)} placeholder="DC=acme,DC=local" spellCheck={false} /></Field></div>
    <div className="row"><Field label="Read-only account (user@acme.local or full DN)"><input value={v.bindUser} onChange={(e) => set('bindUser', e.target.value)} spellCheck={false} /></Field>
      <Field label={init.hasPassword ? 'Password (saved; type a new one to replace it)' : 'Password'}><input type="password" value={v.bindPassword} onChange={(e) => set('bindPassword', e.target.value)} autoComplete="new-password" /></Field></div>
    <Field label="Extra filter (optional, LDAP form)"><input value={v.filter} onChange={(e) => set('filter', e.target.value)} placeholder="(memberOf=CN=DeskAsk,OU=Groups,DC=acme,DC=local)" spellCheck={false} /></Field>
    <label><input type="checkbox" checked={v.useSsl} onChange={(e) => set('useSsl', e.target.checked)} /> Use LDAPS (encrypted, port 636 unless a port is given)</label>
    {v.useSsl && <label><input type="checkbox" checked={v.trustAnyCertificate} onChange={(e) => set('trustAnyCertificate', e.target.checked)} /> Accept the controller's certificate even if this server does not trust it (less safe)</label>}
    <label><input type="checkbox" checked={v.signIn} onChange={(e) => set('signIn', e.target.checked)} /> People imported from AD sign in with their AD password</label>
    <label><input type="checkbox" checked={v.enabled} onChange={(e) => set('enabled', e.target.checked)} /> Switch on</label>
    {note && <p className="note" role="status">{note}</p>}
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
      <button disabled={f.busy} onClick={() => f.submit(async () => { await save(); const r = await api<Rec>('/api/platform/ad/test', { method: 'POST', body: {} }).then(() => 'Connected: the account can read the directory.', (e) => (e as Error).message); setNote(r); throw new Error(''); })}>Save and test</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(save)}>Save</button></div>
  </div></Modal>;
}
