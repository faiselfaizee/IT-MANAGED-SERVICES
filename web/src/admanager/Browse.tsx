import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { DomainPick, useDomain } from './AdApp';

const KINDS: [string, string][] = [['user', 'People'], ['group', 'Groups'], ['computer', 'Computers'], ['ou', 'OUs']];
export const has = (o: Rec | null, r: string) => !!o && ((o.held ?? o.rights ?? []) as string[]).includes(r);

/** Runs an action. A 202 means it was sent for a second person; a password the server made is shown once. */
export async function act(domainId: number, body: Rec): Promise<Rec> {
  return api<Rec>(`/api/adsuite/domains/${domainId}/actions`, { method: 'POST', body });
}

function Said({ r, onClose }: { r: Rec; onClose: () => void }) {
  return <Modal title={r.approval ? 'Sent for approval' : 'Done'} onClose={onClose}>
    {r.message && <p>{String(r.message)}</p>}
    {r.password ? <><p>The password is shown once. Give it to the person safely.</p><p><code className="copy" onClick={() => void navigator.clipboard?.writeText(String(r.password))} title="Click to copy">{String(r.password)}</code></p></> : null}
    {!r.message && !r.password && <p>The change was made.</p>}
    <button className="primary" onClick={onClose}>Close</button></Modal>;
}

function Ask({ title, label, secret, onGo, onClose, optional }: { title: string; label: string; secret?: boolean; optional?: boolean; onGo: (v: string) => Promise<unknown>; onClose: () => void }) {
  const [v, setV] = useState(''); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title={title} onClose={onClose}><Field label={label}><input type={secret ? 'password' : 'text'} autoComplete="off" value={v} onChange={(e) => setV(e.target.value)} /></Field>
    <Err text={err} /><div className="tools"><button className="primary" disabled={busy || (!optional && !v)} onClick={() => void submit(() => onGo(v))}>OK</button><button onClick={onClose}>Cancel</button></div></Modal>;
}

function Panel({ domain, dn, onChanged, onOpen }: { domain: Rec; dn: string; onChanged: () => void; onOpen: (dn: string) => void }) {
  const o = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/object?dn=${encodeURIComponent(dn)}`);
  const [ask, setAsk] = useState<string | null>(null); const [said, setSaid] = useState<Rec | null>(null); const [edit, setEdit] = useState(false);
  if (!o.data) return <Loading loading={o.loading} error={o.error} />;
  const x = o.data; const ob = x.obj as Rec; const run = async (body: Rec) => { const r = await act(domain.id, { dn, ...body }); setSaid(r); o.reload(); onChanged(); return r; };
  const user = ob.kind === 'user', group = ob.kind === 'group', comp = ob.kind === 'computer';
  return <Card title={String(ob.name)}>
    <p className="tools">{ob.disabled ? <Badge tone="bad">Disabled</Badge> : <Badge tone="ok">Enabled</Badge>}{ob.locked && <Badge tone="bad">Locked</Badge>}{ob.expired && <Badge tone="warn">Expired</Badge>}
      {x.privileged && <Badge tone="warn">Privileged</Badge>}{x.protectedObject && <Badge>Built in</Badge>}</p>
    <Dl rows={[['Sign-in name', ob.sam], ['Principal name', ob.upn], ['Email', ob.mail], ['Title', ob.title], ['Department', ob.department], ['Description', ob.description], ['Operating system', ob.os], ['DNS name', ob.dns],
      ['Last sign-in', ob.lastLogon ? fmtDate(ob.lastLogon) : ''], ['Password set', x.passwordLastSet ? fmtDate(x.passwordLastSet) : ''], ['Password expires', ob.pwdNeverExpires ? 'Never' : x.passwordExpires ? fmtDate(x.passwordExpires) : ''],
      ['Account expires', ob.accountExpires ? fmtDate(ob.accountExpires) : ''], ['Created', ob.created ? fmtDate(ob.created) : ''], ['Where', <code key="d">{dn}</code>],
      ...Object.entries((x.fields ?? {}) as Rec).map(([k, v]) => [k, String(v)] as [string, string])]} />
    <div className="tools">
      {user && ob.locked && has(x, 'unlock') && <Act label="Unlock" run={() => run({ action: 'unlock' })} />}
      {user && has(x, 'reset') && <button onClick={() => setAsk('reset')}>Reset password</button>}
      {(user || comp) && has(x, 'enable') && (ob.disabled ? <Act label="Enable" run={() => run({ action: 'enable' })} /> : <Act label="Disable" confirm={`Disable ${ob.name}?`} run={() => run({ action: 'disable' })} />)}
      {user && has(x, 'modify') && <button onClick={() => setAsk('expiry')}>Set expiry</button>}
      {has(x, 'modify') && <button onClick={() => setEdit(true)}>Edit details</button>}
      {has(x, 'move') && !x.protectedObject && <button onClick={() => setAsk('move')}>Move</button>}
      {has(x, 'delete') && !x.protectedObject && <Act label="Delete" danger confirm={`Delete ${ob.name}? This cannot be undone and needs a second person's approval.`} run={() => run({ action: 'delete' })} />}
    </div>
    {group && <><h3>Members ({String(x.membersTotal)})</h3>
      {has(x, 'members') && <div className="tools"><button onClick={() => setAsk('add')}>Add a member</button></div>}
      {((x.members ?? []) as Rec[]).length === 0 ? <Empty>No members.</Empty> : <ul className="plain">{((x.members ?? []) as Rec[]).map((m) => <li key={m.dn}><a href="#/admanager" onClick={(e) => { e.preventDefault(); onOpen(m.dn); }}>{m.name}</a>
        {has(x, 'members') && <> <Act label="Remove" confirm={`Remove ${m.name} from ${ob.name}?`} run={() => run({ action: 'member-remove', members: [m.dn] })} /></>}</li>)}</ul>}</>}
    {!group && ((x.memberOf ?? []) as Rec[]).length > 0 && <><h3>Member of</h3><ul className="plain">{((x.memberOf ?? []) as Rec[]).map((m) => <li key={m.dn}><a href="#/admanager" onClick={(e) => { e.preventDefault(); onOpen(m.dn); }}>{m.name}</a></li>)}</ul></>}
    {ask === 'reset' && <Ask title="Reset password" label="New password (leave empty to have one made)" secret optional onClose={() => setAsk(null)} onGo={(v) => run({ action: 'reset-password', password: v, mustChange: true })} />}
    {ask === 'expiry' && <Ask title="Account expiry" label="Last day, year-month-day (empty for never)" optional onClose={() => setAsk(null)} onGo={(v) => run({ action: 'set-expiry', expires: v })} />}
    {ask === 'move' && <Ask title="Move" label="New OU (distinguished name)" onClose={() => setAsk(null)} onGo={(v) => run({ action: 'move', newParent: v })} />}
    {ask === 'add' && <Ask title="Add a member" label="Person or group (distinguished name)" onClose={() => setAsk(null)} onGo={(v) => run({ action: 'member-add', members: [v] })} />}
    {edit && <EditDetails x={x} onClose={() => setEdit(false)} onSave={(attrs) => run({ action: 'update', attrs })} />}
    {said && <Said r={said} onClose={() => setSaid(null)} />}
  </Card>;
}

const FIELDS: [string, string][] = [['displayName', 'Display name'], ['givenName', 'First name'], ['sn', 'Last name'], ['description', 'Description'], ['mail', 'Email'], ['telephoneNumber', 'Phone'], ['mobile', 'Mobile'], ['title', 'Title'], ['department', 'Department'], ['company', 'Company'], ['physicalDeliveryOfficeName', 'Office']];
function EditDetails({ x, onClose, onSave }: { x: Rec; onClose: () => void; onSave: (a: Rec) => Promise<unknown> }) {
  const ob = x.obj as Rec; const start: Rec = { displayName: ob.name, description: ob.description, mail: ob.mail, title: ob.title, department: ob.department, ...(x.fields as Rec) };
  const [f, setF] = useState<Rec>(start); const { err, busy, submit } = useSubmit(onClose);
  const user = ob.kind === 'user';
  return <Modal title="Edit details" onClose={onClose}><div className="form">
    {FIELDS.filter(([k]) => user || ['description', 'mail', 'displayName'].includes(k)).map(([k, l]) => <Field key={k} label={l}><input value={f[k] ?? ''} onChange={(e) => setF({ ...f, [k]: e.target.value })} /></Field>)}
    <Err text={err} /><div className="tools"><button className="primary" disabled={busy} onClick={() => void submit(() => {
      const changed: Rec = {}; for (const [k] of FIELDS) if ((f[k] ?? '') !== (start[k] ?? '')) changed[k] = f[k] ?? '';
      return Object.keys(changed).length ? onSave(changed) : Promise.resolve();
    })}>Save</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

function Create({ domain, ou, onClose, onDone }: { domain: Rec; ou: string; onClose: () => void; onDone: () => void }) {
  const [kind, setKind] = useState('create-user'); const [f, setF] = useState<Rec>({ expires: '' }); const [said, setSaid] = useState<Rec | null>(null);
  const { err, busy, submit } = useSubmit(() => undefined); const set = (k: string, v: string) => setF({ ...f, [k]: v });
  if (said) return <Said r={said} onClose={() => { onDone(); onClose(); }} />;
  return <Modal title="Create" onClose={onClose}><div className="form">
    <Field label="What"><select value={kind} onChange={(e) => setKind(e.target.value)}><option value="create-user">Person</option><option value="create-group">Group</option><option value="create-ou">OU</option></select></Field>
    <p className="muted small">In <code>{ou}</code></p>
    {kind === 'create-user' && <><Field label="First name"><input value={f.first ?? ''} onChange={(e) => set('first', e.target.value)} /></Field><Field label="Last name"><input value={f.last ?? ''} onChange={(e) => set('last', e.target.value)} /></Field>
      <Field label="Sign-in name"><input value={f.sam ?? ''} onChange={(e) => set('sam', e.target.value)} /></Field><Field label="Email"><input value={f.email ?? ''} onChange={(e) => set('email', e.target.value)} /></Field>
      <Field label="Account expires (year-month-day, optional)"><input value={f.expires} onChange={(e) => set('expires', e.target.value)} /></Field>
      <Field label="Password (leave empty to have one made)"><input type="password" autoComplete="new-password" value={f.password ?? ''} onChange={(e) => set('password', e.target.value)} /></Field></>}
    {kind === 'create-group' && <><Field label="Name"><input value={f.name ?? ''} onChange={(e) => set('name', e.target.value)} /></Field><Field label="Description"><input value={f.description ?? ''} onChange={(e) => set('description', e.target.value)} /></Field></>}
    {kind === 'create-ou' && <><Field label="Name"><input value={f.name ?? ''} onChange={(e) => set('name', e.target.value)} /></Field><Field label="Description"><input value={f.description ?? ''} onChange={(e) => set('description', e.target.value)} /></Field></>}
    <Err text={err} /><div className="tools"><button className="primary" disabled={busy} onClick={() => void submit(async () => setSaid(await act(domain.id, { action: kind, ou, ...f })))}>Create</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

function Tree({ domain, base, onPick, onBase }: { domain: Rec; base: string; onPick: (dn: string) => void; onBase: (dn: string) => void }) {
  const t = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/tree?base=${encodeURIComponent(base)}`);
  const items = ((t.data?.items ?? []) as Rec[]);
  const here = (t.data?.baseDn as string) || base;
  const up = here && here.toLowerCase() !== (domain.baseDn as string).toLowerCase() && !domain.scoped ? here.replace(/^[^,]*(?:\\,[^,]*)*?,/, '') : '';
  return <Card title="Folders"><Loading loading={t.loading && !t.data} error={t.error} />
    {here && <p className="small"><code>{here}</code></p>}
    {up && <p><a href="#/admanager" onClick={(e) => { e.preventDefault(); onBase(up); }}>↑ Up one level</a></p>}
    <ul className="plain">{items.map((i) => <li key={i.dn as string}><a href="#/admanager" onClick={(e) => { e.preventDefault(); onBase(i.dn as string); }}>📁 {i.name}</a></li>)}</ul>
    {t.data && items.length === 0 && <Empty>No folders here.</Empty>}
    <button onClick={() => onPick(here)}>Show what is in here</button></Card>;
}

export default function Browse({ domains }: { domains: Rec[] }) {
  const [domain, pick] = useDomain(domains);
  const [kind, setKind] = useState('user'); const [q, setQ] = useState(''); const [go, setGo] = useState(''); const [base, setBase] = useState(''); const [scope, setScope] = useState(''); const [dn, setDn] = useState(''); const [creating, setCreating] = useState(false);
  const url = domain ? `/api/adsuite/domains/${domain.id}/search?kind=${kind}&q=${encodeURIComponent(go)}${scope ? '&base=' + encodeURIComponent(scope) : ''}` : null;
  const res = useLoad<Rec>(url, [go]);
  if (!domain) return null;
  const items = (res.data?.items ?? []) as Rec[];
  const canCreate = has(domain, 'create');
  return <>
    <div className="tools"><DomainPick domains={domains} value={domain} onPick={(n) => { pick(n); setBase(''); setScope(''); setDn(''); }} />
      <select aria-label="Look for" value={kind} onChange={(e) => setKind(e.target.value)}>{KINDS.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
      <input aria-label="Search" placeholder="Name, sign-in name or email" value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') setGo(q); }} />
      <button className="primary" onClick={() => setGo(q)}>Search</button>
      {canCreate && <button onClick={() => setCreating(true)}>Create…</button>}</div>
    {!domain.enabled && <p className="err">This domain is switched off.</p>}
    <div className="split">
      <div><Tree domain={domain} base={base} onBase={setBase} onPick={(b) => setScope(b)} />
        <Card title={scope ? 'In this folder' : 'Results'}>
          <Loading loading={res.loading} error={res.error} />
          {res.data && (items.length === 0 ? <Empty>Nothing found.</Empty> : <table><thead><tr><th>Name</th><th>Details</th><th /></tr></thead><tbody>
            {items.map((i) => <tr key={i.dn as string} onClick={() => setDn(i.dn as string)} className={dn === i.dn ? 'on' : ''} style={{ cursor: 'pointer' }}>
              <td>{i.name}</td><td className="muted small">{i.sam || i.mail || i.description || i.dns}</td>
              <td>{i.disabled && <Badge tone="bad">Disabled</Badge>}{i.locked && <Badge tone="bad">Locked</Badge>}</td></tr>)}</tbody></table>)}
          {res.data?.truncated ? <p className="muted small">Only the first results are shown. Narrow the search.</p> : null}
          {scope && <p><a href="#/admanager" onClick={(e) => { e.preventDefault(); setScope(''); }}>Clear folder filter</a></p>}
        </Card></div>
      <div>{dn ? <Panel key={dn} domain={domain} dn={dn} onChanged={res.reload} onOpen={setDn} /> : <Empty>Choose someone or something to see the details.</Empty>}</div>
    </div>
    {creating && <Create domain={domain} ou={scope || base || (domain.baseDn as string)} onClose={() => setCreating(false)} onDone={() => { res.reload(); }} />}
  </>;
}
