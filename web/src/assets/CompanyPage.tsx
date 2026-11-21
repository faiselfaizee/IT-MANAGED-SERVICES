import { useState } from 'react';
import { api, useHash, useLoad, type Rec } from '../http';
import { useSingleCompany } from '../mode';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';
import { CompanyForm } from './Companies';
import DirectorySetup from './DirectorySetup';
import { Give, Holder } from './Peripherals';
import { GoogleSettings, ImportEntra, SsoSettings } from '../Users';
import { Delegation } from '../admanager/Domains';

const TABS: [string, string][] = [['users', 'Users'], ['signin', 'Sign-in'], ['ad', 'AD permissions'], ['directory', 'Domain controller'], ['details', 'Details']];
const ROLE: Record<string, string> = { cadmin: 'Customer administrator', customer: 'Employee' };

/**
 * Devices setup › Customers › a company: everything kept about one customer in one place: its people (customer administrators and employees,
 * what they may do in the Service Desk, what they hold), its domain controller and its details. With one company it is "Our organisation".
 */
export default function CompanyPage({ id }: { id?: number }) {
  const [, , pid, ptab] = useHash(); const one = useSingleCompany();
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/assets/companies');
  const list = Array.isArray(data) ? data : [];
  const cid = id ?? Number(pid); const co = id == null && one ? list[0] : list.find((c) => c.id === cid);
  const tab = TABS.some(([k]) => k === (id == null && one ? pid : ptab)) ? (id == null && one ? pid : ptab) as string : 'users';
  const base = one ? '#/setup/company' : `#/setup/companies/${co?.id ?? cid}`;
  const [edit, setEdit] = useState(false);
  return <>
    {!one && <nav className="crumbs" aria-label="Breadcrumb"><a href="#/setup/companies">Customers</a><span aria-hidden="true">›</span><span aria-current="page">{co?.name ?? '…'}</span></nav>}
    <h1>{one ? 'Our organisation' : co?.name ?? 'Customer'}</h1>
    <Loading loading={loading && !data} error={error} />
    {data && !co && <Empty>{one ? 'Add your first device and your organisation is created for you; then come back here.' : 'This customer does not exist.'}</Empty>}
    {co && <>
      <div className="tabs" role="tablist">{TABS.map(([k, l]) => <a key={k} role="tab" aria-selected={tab === k} className={tab === k ? 'on' : ''} href={`${base}/${k}`}>{l}</a>)}</div>
      {tab === 'users' && <CompanyUsers company={{ id: co.id, name: co.name }} />}
      {tab === 'signin' && <SignInTab company={{ id: co.id, name: co.name }} />}
      {tab === 'ad' && <AdTab company={{ id: co.id, name: co.name }} />}
      {tab === 'directory' && <DirectoryTab company={{ id: co.id, name: co.name }} />}
      {tab === 'details' && <Card title="Details">
        <dl className="facts"><dt>Name</dt><dd>{co.name}</dd><dt>Code</dt><dd>{co.code || '–'}</dd><dt>Contact</dt><dd>{co.contactName || '–'} <span className="muted">{co.contactEmail} {co.phone}</span></dd>
          <dt>Address</dt><dd>{co.address || '–'}</dd><dt>Assets</dt><dd><a href={`#/assets/list?company=${co.id}`}>{co.assets}</a></dd><dt>Remote access</dt><dd>{co.remoteAllowed ? 'Allowed' : 'Off'}</dd></dl>
        <div className="tools"><button onClick={() => setEdit(true)}>Edit details</button><a href={`#/assets/locations?company=${co.id}`}>Locations</a></div>
      </Card>}
      {edit && <CompanyForm init={co} onClose={(ch) => { setEdit(false); if (ch) reload(); }} />}
    </>}
  </>;
}

/** How the company's people sign in: their own Microsoft 365 or Google Workspace (found by email domain on the sign-in page), or Windows passwords. */
function SignInTab({ company }: { company: { id: number; name: string } }) {
  const ms = useLoad<Rec>(`/api/companies/${company.id}/sso/microsoft`); const g = useLoad<Rec>(`/api/companies/${company.id}/sso/google`);
  const [open, setOpen] = useState<'' | 'ms' | 'g' | 'msimport' | 'gimport'>('');
  const state = (d?: Rec | null) => d?.enabled ? <Badge tone="ok">On</Badge> : <Badge>Off</Badge>;
  const done = () => { setOpen(''); ms.reload(); g.reload(); };
  return <>
    <Card title="Microsoft 365 (Entra ID)">
      <p>{state(ms.data)} <span className="muted">{ms.data?.domains ? `for ${ms.data.domains}` : 'no domains yet'}</span></p>
      <div className="tools"><button onClick={() => setOpen('ms')}>Set up…</button><button disabled={!ms.data?.tenantId} onClick={() => setOpen('msimport')}>Import people…</button></div>
    </Card>
    <Card title="Google Workspace">
      <p>{state(g.data)} <span className="muted">{g.data?.domains ? `for ${g.data.domains}` : 'no domains yet'}</span></p>
      <div className="tools"><button onClick={() => setOpen('g')}>Set up…</button><button disabled={!g.data?.serviceAccount} onClick={() => setOpen('gimport')}>Import people…</button></div>
    </Card>
    <Card title="Windows (Active Directory)"><p className="muted">People picked from the domain controller on the Users tab sign in with their email and Windows password, checked through the site controller in {company.name}'s network.</p></Card>
    {open === 'ms' && <SsoSettings base={`/api/companies/${company.id}/sso/microsoft`} company={company.name} onClose={done} />}
    {open === 'g' && <GoogleSettings base={`/api/companies/${company.id}/sso/google`} company={company.name} onClose={done} />}
    {open === 'msimport' && <ImportEntra base={`/api/companies/${company.id}/sso/microsoft/entra`} title={`Import ${company.name}'s people from Microsoft`} company onClose={done} />}
    {open === 'gimport' && <ImportEntra base={`/api/companies/${company.id}/sso/google`} title={`Import ${company.name}'s people from Google`} company onClose={done} />}
  </>;
}

/** AD Manager: who of your team may do what in this company's domains (people and groups, per OU). Without the AD Manager licence it says so. */
function AdTab({ company }: { company: { id: number; name: string } }) {
  const { data, error, loading } = useLoad<Rec[]>('/api/adsuite/domains');
  const mine = (Array.isArray(data) ? data : []).filter((d) => Number(d.companyId) === company.id);
  if (error) return <Card title="AD permissions"><p className="muted">AD Manager is not part of your licence, or you may not manage it. With AD Manager, this tab sets who of your team may unlock, reset passwords, create or move objects in {company.name}'s Active Directory.</p></Card>;
  return <>
    <Loading loading={loading && !data} error="" />
    {data && mine.length === 0 && <Card title="AD permissions"><Empty>{company.name} has no domain in AD Manager yet.</Empty><p><a href="#/admanager/domains">Add the domain in AD Manager</a>; its permissions then show here.</p></Card>}
    {mine.map((d) => <Delegation key={d.id} d={d} />)}
  </>;
}

function DirectoryTab({ company }: { company: { id: number; name: string } }) {
  const [open, setOpen] = useState(false);
  return <Card title="Domain controller">
    <p className="muted">Read {company.name}'s people from their domain controller, through the site controller in their network, so they can be chosen when assets are handed out.</p>
    <button className="primary" onClick={() => setOpen(true)}>Set up the domain controller</button>
    {open && <DirectorySetup company={company} onClose={() => setOpen(false)} />}
  </Card>;
}

/** The company's customer administrators and employees: add them, set what they may do with tickets, and see what each one holds. */
export function CompanyUsers({ company }: { company: { id: number; name: string } }) {
  const one = useSingleCompany();
  const users = useLoad<Rec[]>('/api/platform/users');
  const ties = useLoad<Rec[]>('/api/platform/access/user-companies');
  const rights = useLoad<Rec[]>('/api/servicedesk/people');   // only with a Service Desk licence; without it the two switches are not shown
  const holders = useLoad<Rec>(`/api/assets/holders?companyId=${company.id}`);   // only with Assets or Monitoring
  const [add, setAdd] = useState(false); const [link, setLink] = useState(false); const [pick, setPick] = useState(false); const [edit, setEdit] = useState<Rec | null>(null); const [pw, setPw] = useState<Rec | null>(null);
  const [who, setWho] = useState<Rec | null>(null); const [give, setGive] = useState(false); const [msg, setMsg] = useState('');
  const tie = new Map(((Array.isArray(ties.data) ? ties.data : []) as Rec[]).map((t) => [String(t.userId), Number(t.companyId)]));
  const right = new Map(((Array.isArray(rights.data) ? rights.data : []) as Rec[]).map((p) => [String(p.id), p]));
  const sd = Array.isArray(rights.data);
  const companyOf = (u: Rec) => tie.get(String(u.id)) ?? right.get(String(u.id))?.companyId ?? null;
  const all = ((Array.isArray(users.data) ? users.data : []) as Rec[]).filter((u) => u.role === 'customer' || u.role === 'cadmin');
  const mine = all.filter((u) => companyOf(u) === company.id); const loose = all.filter((u) => companyOf(u) == null);
  const held = (holders.data?.items ?? []) as Rec[];
  const count = (u: Rec) => held.filter((h) => [String(u.name).toLowerCase(), String(u.email).toLowerCase()].includes(String(h.name).toLowerCase())).reduce((n, h) => n + Number(h.assets ?? 0), 0);
  const reload = () => { users.reload(); ties.reload(); rights.reload(); holders.reload(); };
  const setRights = async (u: Rec, change: Rec) => {
    setMsg(''); const r = right.get(String(u.id));
    try { await api(`/api/servicedesk/people/${u.id}`, { method: 'PUT', body: { companyId: company.id, mayRaise: r?.mayRaise ?? true, mayModify: r?.mayModify ?? true, ...change } }); rights.reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return <>
    <div className="tools"><button className="primary" onClick={() => setAdd(true)}>Add user</button>
      <button onClick={() => setPick(true)}>Pick from the domain controller</button>
      {loose.length > 0 && <button onClick={() => setLink(true)}>Add an existing user ({loose.length})</button>}
      <span className="muted">{one ? 'Your employees sign in to open and follow their tickets.' : `${company.name}'s people: a customer administrator sees the whole company; an employee sees their own tickets.`}</span></div>
    <Err text={msg} />
    <Loading loading={(users.loading && !users.data) || (ties.loading && !ties.data)} error={users.error ?? ties.error} />
    {users.data && (mine.length === 0 ? <Empty>No users yet. Add the people who should open tickets or hold assets.</Empty> : <table><thead><tr><th>Name</th><th>Role</th>
      {sd && <><th>May open tickets</th><th>May reply and close</th></>}<th className="num">Assets</th><th>Status</th><th /></tr></thead><tbody>
      {mine.map((u) => { const r = right.get(String(u.id)); const n = count(u); return <tr key={u.id} className={who?.id === u.id ? 'on' : ''}>
        <td>{u.name}<div className="muted">{u.email}{u.signIn === 'windows' ? ' · signs in with Windows' : ''}</div></td><td>{ROLE[u.role] ?? u.role}</td>
        {sd && <><td><input type="checkbox" checked={r?.mayRaise ?? true} aria-label={`${u.name} may open tickets`} onChange={(e) => void setRights(u, { mayRaise: e.target.checked })} /></td>
          <td><input type="checkbox" checked={r?.mayModify ?? true} aria-label={`${u.name} may reply and close`} onChange={(e) => void setRights(u, { mayModify: e.target.checked })} /></td></>}
        <td className="num">{holders.data ? <a href="#" aria-label={`${n} assets held by ${u.name}`} onClick={(e) => { e.preventDefault(); setWho(u); }}>{n}</a> : '–'}</td>
        <td><Badge tone={u.active ? 'ok' : undefined}>{u.active ? 'Active' : 'Disabled'}</Badge></td>
        <td><button onClick={() => setEdit(u)}>Edit</button> <button onClick={() => setPw(u)}>Set password</button>{' '}
          <Act label={u.active ? 'Disable' : 'Enable'} danger={u.active} confirm={u.active ? `Disable ${u.name}? They can no longer sign in.` : undefined} run={async () => { await api(`/api/platform/users/${u.id}`, { method: 'PATCH', body: { active: !u.active } }); reload(); }} />{' '}
          {!one && <Act label="Remove from company" confirm={`Take ${u.name} out of ${company.name}? They keep their account but see no company's tickets or assets.`} run={async () => { await api(`/api/platform/access/user-companies/${u.id}`, { method: 'PUT', body: { companyId: null } }); if (sd) await api(`/api/servicedesk/people/${u.id}`, { method: 'PUT', body: { companyId: null, mayRaise: r?.mayRaise ?? true, mayModify: r?.mayModify ?? true } }); reload(); }} />}</td>
      </tr>; })}</tbody></table>)}
    {who && holders.data && <Holder key={String(who.id)} who={{ name: who.name, also: who.email, companyId: company.id, company: company.name }} admin onChanged={holders.reload} onGive={() => setGive(true)} />}
    {give && who && <Give to={who.name} companyId={company.id} onClose={(ch) => { setGive(false); if (ch) { holders.reload(); setWho({ ...who }); } }} />}
    {add && <AddUser company={company} sd={sd} onClose={(ch) => { setAdd(false); if (ch) reload(); }} />}
    {pick && <PickFromDirectory company={company} have={new Set(all.map((u) => String(u.email).toLowerCase()))} onClose={(ch) => { setPick(false); if (ch) reload(); }} />}
    {link && <LinkUser company={company} people={loose} sd={sd} onClose={(ch) => { setLink(false); if (ch) reload(); }} />}
    {edit && <EditUser user={edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
    {pw && <SetPassword user={pw} onClose={() => setPw(null)} />}
  </>;
}

/** Ties the person to the company (Access decides what they see) and records what they may do with tickets. */
async function tieTo(userId: string, companyId: number, sd: boolean, mayRaise = true, mayModify = true) {
  await api(`/api/platform/access/user-companies/${userId}`, { method: 'PUT', body: { companyId } });
  if (sd) await api(`/api/servicedesk/people/${userId}`, { method: 'PUT', body: { companyId, mayRaise, mayModify } });
}

function AddUser({ company, sd, onClose }: { company: { id: number; name: string }; sd: boolean; onClose: (changed: boolean) => void }) {
  const one = useSingleCompany();
  const [v, setV] = useState({ name: '', email: '', role: 'customer', password: '', mayRaise: true, mayModify: true }); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const f = useSubmit(() => onClose(true));
  return <Modal title={`Add a user to ${company.name}`} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field>
    <Field label="Email"><input type="email" value={v.email} onChange={(e) => set('email', e.target.value)} /></Field>
    {!one && <Field label="Role"><select value={v.role} onChange={(e) => set('role', e.target.value)}><option value="customer">Employee (their own tickets)</option><option value="cadmin">Customer administrator (the whole company)</option></select></Field>}
    <Field label="Password (at least 10 characters)"><input type="password" autoComplete="new-password" value={v.password} onChange={(e) => set('password', e.target.value)} /></Field>
    {sd && <><label><input type="checkbox" checked={v.mayRaise} onChange={(e) => set('mayRaise', e.target.checked)} /> May open tickets</label>
      <label><input type="checkbox" checked={v.mayModify} onChange={(e) => set('mayModify', e.target.checked)} /> May reply and close</label></>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.name.trim() || !v.email.trim() || v.password.length < 10} onClick={() => void f.submit(async () => {
        const r = await api<Rec>('/api/platform/users', { body: { email: v.email, name: v.name, role: v.role, password: v.password } });
        await tieTo(String(r.id), company.id, sd, v.mayRaise, v.mayModify);
      })}>Add user</button></div></div></Modal>;
}

/** The company's people from its domain controller: the chosen ones get an account and sign in with their Windows password. */
function PickFromDirectory({ company, have, onClose }: { company: { id: number; name: string }; have: Set<string>; onClose: (changed: boolean) => void }) {
  const one = useSingleCompany();
  const [q, setQ] = useState(''); const [sel, setSel] = useState<Map<string, Rec>>(new Map()); const [role, setRole] = useState('customer'); const [done, setDone] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/assets/directory-people?companyId=${company.id}&limit=500&search=${encodeURIComponent(q)}`, [q]);
  const people = ((data?.items ?? []) as Rec[]).filter((p) => p.companyId === company.id);
  const toggle = (p: Rec) => setSel((s) => { const n = new Map(s); if (n.has(p.account)) n.delete(p.account); else n.set(p.account, p); return n; });
  const f = useSubmit(() => undefined);
  return <Modal title={`Pick from ${company.name}'s domain controller`} onClose={() => onClose(!!done)}><div className="form">
    <p className="muted">The people you choose get an account and sign in to DeskAsk with their email and Windows password. The password is checked by their domain controller, through the site controller in {company.name}'s network, and is never kept. People without an email in the directory cannot be chosen.</p>
    <Field label="Search"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, account or email" autoFocus /></Field>
    <Loading loading={loading && !data} error={error} />
    {data && (people.length === 0 ? <Empty>Nobody found. Set up the domain controller on the Domain controller tab and read it once.</Empty> : <table><thead><tr><th /><th>Name</th><th>Email</th><th>Department</th></tr></thead><tbody>
      {people.map((p) => { const has = have.has(String(p.email).toLowerCase()); const can = !!p.email && !has && p.enabled !== false;
        return <tr key={p.account}><td><input type="checkbox" aria-label={'Choose ' + p.name} disabled={!can} checked={sel.has(p.account)} onChange={() => toggle(p)} /></td>
          <td>{p.name}<div className="muted">{p.account}{p.enabled === false ? ' · disabled in Windows' : ''}</div></td><td>{p.email || <span className="muted">no email</span>}{has && <Badge>has an account</Badge>}</td><td>{p.department}</td></tr>; })}</tbody></table>)}
    {!one && <Field label={`Role for the ${sel.size} chosen`}><select value={role} onChange={(e) => setRole(e.target.value)}><option value="customer">Employee (their own tickets)</option><option value="cadmin">Customer administrator (the whole company)</option></select></Field>}
    {done && <p role="status">{done}</p>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(!!done)}>{done ? 'Close' : 'Cancel'}</button>
      <button className="primary" disabled={f.busy || sel.size === 0} onClick={() => void f.submit(async () => {
        const r = await api<Rec>(`/api/companies/${company.id}/users/from-directory`, { body: { role, people: [...sel.values()].map((p) => ({ account: p.account, name: p.name, email: p.email })) } });
        setDone(`${r.created} added, ${r.linked} linked, ${r.skipped} skipped.`); setSel(new Map());
      })}>Add {sel.size || ''}</button></div></div></Modal>;
}

function LinkUser({ company, people, sd, onClose }: { company: { id: number; name: string }; people: Rec[]; sd: boolean; onClose: (changed: boolean) => void }) {
  const [sel, setSel] = useState<Set<string>>(new Set()); const f = useSubmit(() => onClose(true));
  const toggle = (id: string) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return <Modal title={`Add existing users to ${company.name}`} onClose={() => onClose(false)}><div className="form">
    <p className="muted">These customer users belong to no company yet, so they see no tickets or assets.</p>
    <table><thead><tr><th /><th>Name</th><th>Role</th></tr></thead><tbody>{people.map((u) => <tr key={u.id}><td><input type="checkbox" aria-label={'Choose ' + u.name} checked={sel.has(String(u.id))} onChange={() => toggle(String(u.id))} /></td>
      <td>{u.name}<div className="muted">{u.email}</div></td><td>{ROLE[u.role] ?? u.role}</td></tr>)}</tbody></table>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || sel.size === 0} onClick={() => void f.submit(async () => { for (const id of sel) await tieTo(id, company.id, sd); })}>Add {sel.size || ''}</button></div></div></Modal>;
}

function EditUser({ user, onClose }: { user: Rec; onClose: (changed: boolean) => void }) {
  const one = useSingleCompany();
  const [name, setName] = useState<string>(user.name); const [role, setRole] = useState<string>(user.role); const f = useSubmit(() => onClose(true));
  return <Modal title={`Edit ${user.name}`} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} autoFocus /></Field>
    <Field label="Email"><input value={user.email} disabled /></Field>
    {!one && <Field label="Role"><select value={role} onChange={(e) => setRole(e.target.value)}><option value="customer">Employee (their own tickets)</option><option value="cadmin">Customer administrator (the whole company)</option></select></Field>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !name.trim()} onClick={() => void f.submit(() => api(`/api/platform/users/${user.id}`, { method: 'PATCH', body: { name: name.trim(), role } }))}>Save changes</button></div></div></Modal>;
}

function SetPassword({ user, onClose }: { user: Rec; onClose: () => void }) {
  const [p, setP] = useState(''); const f = useSubmit(onClose);
  return <Modal title={`New password for ${user.name}`} onClose={onClose}><div className="form">
    <Field label="Password (at least 10 characters)"><input type="password" autoComplete="new-password" value={p} onChange={(e) => setP(e.target.value)} autoFocus /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
      <button className="primary" disabled={f.busy || p.length < 10} onClick={() => void f.submit(() => api(`/api/platform/users/${user.id}/password`, { body: { password: p } }))}>Set password</button></div></div></Modal>;
}
