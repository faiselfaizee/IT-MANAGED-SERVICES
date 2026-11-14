import { useState } from 'react';
import { api, useLoad, type Rec } from './http';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, useSubmit } from './ui';

const MODULES: [string, string][] = [['assets', 'Assets'], ['servicedesk', 'Service Desk'], ['monitoring', 'Monitoring']];
const ACTIONS: [string, string][] = [['read', 'View'], ['add', 'Add'], ['modify', 'Change'], ['delete', 'Delete']];
const modName = (m: string) => MODULES.find(([k]) => k === m)?.[1] ?? m;
const actName = (a: string) => ACTIONS.find(([k]) => k === a)?.[1] ?? a;

/** Delegation: security groups, and who may view, add, change or delete in each module. */
export default function Access() {
  const [tab, setTab] = useState<'groups' | 'permissions'>('groups');
  return <>
    <h1>Access</h1>
    <nav className="sub" aria-label="Access">
      <a href="#/access" className={tab === 'groups' ? 'on' : ''} onClick={(e) => { e.preventDefault(); setTab('groups'); }}>Groups</a>
      <a href="#/access" className={tab === 'permissions' ? 'on' : ''} onClick={(e) => { e.preventDefault(); setTab('permissions'); }}>Permissions</a>
    </nav>
    {tab === 'groups' ? <Groups /> : <Permissions />}
  </>;
}

function Groups() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/platform/access/groups');
  const users = useLoad<Rec[]>('/api/platform/users');
  const [edit, setEdit] = useState<Rec | null>(null); const [adding, setAdding] = useState<Rec | null>(null); const [msg, setMsg] = useState('');
  const list = Array.isArray(data) ? data : [];
  const run = async (f: () => Promise<unknown>) => { setMsg(''); try { await f(); reload(); } catch (e) { setMsg((e as Error).message); } };
  return <>
    <div className="tools"><button className="primary" onClick={() => setEdit({ name: '', description: '' })}>New group</button>
      <span className="muted">A group can hold people and other groups. Give permissions to the group, not to each person.</span></div>
    <Loading loading={loading && !data} error={error} /><Err text={msg} />
    {data && (list.length === 0 ? <Empty>No groups yet.</Empty> : list.map((g) => <Card key={g.id} title={g.name}>
      {g.description && <p className="muted">{g.description}</p>}
      {g.members.length === 0 ? <p className="muted">No members.</p> : <ul className="files">{g.members.map((m: Rec) => <li key={m.kind + m.id}>
        {m.name} <Badge>{m.kind === 'group' ? 'Group' : 'Person'}</Badge>{' '}
        <Act label="Remove" confirm={`Remove ${m.name} from ${g.name}?`} run={() => run(() => api(`/api/platform/access/groups/${g.id}/members/${m.kind}/${m.id}`, { method: 'DELETE' }))} /></li>)}</ul>}
      <div className="actions" style={{ justifyContent: 'flex-start' }}>
        <button onClick={() => setAdding(g)}>Add member</button><button onClick={() => setEdit(g)}>Rename</button>
        <Act label="Delete group" danger confirm={`Delete ${g.name}? Its permissions go with it.`} run={() => run(() => api(`/api/platform/access/groups/${g.id}`, { method: 'DELETE' }))} />
      </div>
    </Card>))}
    {edit && <GroupForm init={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
    {adding && <MemberForm group={adding} groups={list} users={Array.isArray(users.data) ? users.data : []} onClose={(ch) => { setAdding(null); if (ch) reload(); }} />}
  </>;
}

function GroupForm({ init, onClose }: { init: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>(init); const f = useSubmit(() => onClose(true));
  return <Modal title={init.id ? 'Rename group' : 'New group'} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => setV({ ...v, name: e.target.value })} autoFocus /></Field>
    <Field label="What is it for?"><input value={v.description} onChange={(e) => setV({ ...v, description: e.target.value })} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim()} onClick={() => f.submit(() => init.id
        ? api(`/api/platform/access/groups/${init.id}`, { method: 'PUT', body: { name: v.name, description: v.description } })
        : api('/api/platform/access/groups', { body: { name: v.name, description: v.description } }))}>{init.id ? 'Save changes' : 'Create group'}</button></div>
  </div></Modal>;
}

function MemberForm({ group, groups, users, onClose }: { group: Rec; groups: Rec[]; users: Rec[]; onClose: (changed: boolean) => void }) {
  const [pick, setPick] = useState(''); const f = useSubmit(() => onClose(true));
  const taken = new Set((group.members as Rec[]).map((m) => m.kind + ':' + m.id));
  const options = [...users.filter((u) => u.active).map((u) => ({ key: 'user:' + u.id, label: `${u.name} (${u.email})` })),
    ...groups.filter((g) => g.id !== group.id).map((g) => ({ key: 'group:' + g.id, label: `${g.name} (group)` }))].filter((o) => !taken.has(o.key));
  return <Modal title={`Add to ${group.name}`} onClose={() => onClose(false)}><div className="form">
    <Field label="Person or group"><select value={pick} onChange={(e) => setPick(e.target.value)} autoFocus><option value="">Choose…</option>{options.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}</select></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !pick} onClick={() => f.submit(() => { const [kind, id] = pick.split(':'); return api(`/api/platform/access/groups/${group.id}/members`, { body: { kind, id } }); })}>Add</button></div>
  </div></Modal>;
}

function Permissions() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/platform/access/grants');
  const groups = useLoad<Rec[]>('/api/platform/access/groups'); const users = useLoad<Rec[]>('/api/platform/users');
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const [adding, setAdding] = useState(false); const [msg, setMsg] = useState('');
  const list = Array.isArray(data) ? data : []; const cos = Array.isArray(companies.data) ? companies.data : [];
  const coName = (id: number | null) => (id == null ? 'Every company' : cos.find((c) => c.id === id)?.name ?? `Company ${id}`);
  return <>
    <div className="tools"><button className="primary" onClick={() => setAdding(true)}>Give permission</button>
      <span className="muted">Administrators can always do everything. Staff can view Assets; customer administrators view their own company's assets. Everything else is given here.</span></div>
    <Loading loading={loading && !data} error={error} /><Err text={msg} />
    {data && (list.length === 0 ? <Empty>No permissions given yet.</Empty> : <table><thead><tr><th>Who</th><th>Module</th><th>May</th><th>For</th><th /></tr></thead><tbody>
      {list.map((g) => <tr key={g.id}><td>{g.principal} <Badge>{g.kind === 'group' ? 'Group' : 'Person'}</Badge></td><td>{modName(g.module)}</td><td>{actName(g.action)}</td><td>{coName(g.companyId)}</td>
        <td><Act label="Remove" confirm="Remove this permission?" run={async () => { setMsg(''); try { await api(`/api/platform/access/grants/${g.id}`, { method: 'DELETE' }); reload(); } catch (e) { setMsg((e as Error).message); } }} /></td></tr>)}
    </tbody></table>)}
    {adding && <GrantForm groups={Array.isArray(groups.data) ? groups.data : []} users={Array.isArray(users.data) ? users.data : []} companies={cos} onClose={(ch) => { setAdding(false); if (ch) reload(); }} />}
  </>;
}

function GrantForm({ groups, users, companies, onClose }: { groups: Rec[]; users: Rec[]; companies: Rec[]; onClose: (changed: boolean) => void }) {
  const [who, setWho] = useState(''); const [module, setModule] = useState('assets'); const [acts, setActs] = useState<string[]>(['read']); const [company, setCompany] = useState('');
  const f = useSubmit(() => onClose(true));
  const toggle = (a: string) => setActs(acts.includes(a) ? acts.filter((x) => x !== a) : [...acts, a]);
  return <Modal title="Give permission" onClose={() => onClose(false)}><div className="form">
    <Field label="To"><select value={who} onChange={(e) => setWho(e.target.value)} autoFocus><option value="">Choose a group or person…</option>
      <optgroup label="Groups">{groups.map((g) => <option key={g.id} value={'group:' + g.id}>{g.name}</option>)}</optgroup>
      <optgroup label="People">{users.filter((u) => u.active).map((u) => <option key={u.id} value={'user:' + u.id}>{u.name} ({u.email})</option>)}</optgroup></select></Field>
    <Field label="Module"><select value={module} onChange={(e) => setModule(e.target.value)}>{MODULES.map(([k, n]) => <option key={k} value={k}>{n}</option>)}</select></Field>
    <div><span className="muted">May</span><div className="actions" style={{ justifyContent: 'flex-start', flexWrap: 'wrap' }}>
      {ACTIONS.map(([k, n]) => <label key={k}><input type="checkbox" checked={acts.includes(k)} onChange={() => toggle(k)} /> {n}</label>)}</div></div>
    <Field label="For"><select value={company} onChange={(e) => setCompany(e.target.value)}><option value="">Every company</option>{companies.map((c) => <option key={c.id} value={c.id}>{c.name} only</option>)}</select></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !who || acts.length === 0} onClick={() => f.submit(async () => {
        const [kind, principalId] = who.split(':');
        for (const action of acts) await api('/api/platform/access/grants', { body: { kind, principalId, module, action, companyId: company ? Number(company) : null } }).catch((e: Error & { status?: number }) => { if (e.status !== 409) throw e; });
      })}>Give permission</button></div>
  </div></Modal>;
}
