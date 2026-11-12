import { useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Act, Badge, Empty, Err, Field, Loading, Modal, useSubmit } from './ui';

const ROLES: [string, string][] = [
  ['admin', 'Administrator'], ['engineer', 'Engineer'], ['viewer', 'Viewer'],
  ['cadmin', 'Customer administrator'], ['customer', 'Customer'],
];
const roleName = (r: string) => ROLES.find(([k]) => k === r)?.[1] ?? r;

export default function Users({ meId }: { meId?: string }) {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/platform/users');
  const [form, setForm] = useState<Rec | null>(null); const [pw, setPw] = useState<Rec | null>(null);
  const list = Array.isArray(data) ? data : [];
  return <>
    <h1>Users</h1>
    <div className="tools"><button className="primary" onClick={() => setForm({ email: '', name: '', role: 'engineer', password: '' })}>Add user</button>
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
