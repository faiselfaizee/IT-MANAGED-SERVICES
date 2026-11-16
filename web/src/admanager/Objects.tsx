import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Dl, Empty, Err, Field, fmtDate, Loading, Modal, useSubmit } from '../ui';
import { ObjectPicker, OuPicker, ouPath } from './pickers';
import { EditUser } from './UserForm';

type Hit = { dn: string; name: string; sub?: string };
export const has = (o: Rec | null, r: string) => !!o && ((o.held ?? o.rights ?? []) as string[]).includes(r);

export async function act(domainId: number, body: Rec): Promise<Rec> {
  return api<Rec>(`/api/adsuite/domains/${domainId}/actions`, { method: 'POST', body });
}

function Said({ r, onClose }: { r: Rec; onClose: () => void }) {
  return <Modal title={r.approval ? 'Sent for approval' : 'Done'} onClose={onClose}>
    {r.message && <p>{String(r.message)}</p>}
    {r.steps && <ul>{(r.steps as string[]).map((s) => <li key={s}>{s}</li>)}</ul>}
    {r.password ? <><p>The password is shown once. Give it to the person safely.</p><p><code className="copy" onClick={() => void navigator.clipboard?.writeText(String(r.password))} title="Click to copy">{String(r.password)}</code></p></> : null}
    {!r.message && !r.password && !r.steps && <p>The change was made.</p>}
    <button className="primary" onClick={onClose}>Close</button></Modal>;
}

function Reset({ onGo, onClose, tls }: { onGo: (b: Rec) => Promise<unknown>; onClose: () => void; tls: boolean }) {
  const [mode, setMode] = useState<'generate' | 'type'>('generate'); const [pw, setPw] = useState(''); const [pw2, setPw2] = useState('');
  const [must, setMust] = useState(true); const [unlock, setUnlock] = useState(true); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title="Reset password" onClose={onClose}><div className="form">
    <label className="check"><input type="radio" checked={mode === 'generate'} onChange={() => setMode('generate')} /> Make a strong password (shown once)</label>
    <label className="check"><input type="radio" checked={mode === 'type'} onChange={() => setMode('type')} /> Type a password</label>
    {mode === 'type' && <><Field label="New password"><input type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} /></Field>
      <Field label="Again"><input type="password" autoComplete="new-password" value={pw2} onChange={(e) => setPw2(e.target.value)} /></Field></>}
    <label className="check"><input type="checkbox" checked={must} onChange={(e) => setMust(e.target.checked)} /> Must change at next sign-in</label>
    <label className="check"><input type="checkbox" checked={unlock} onChange={(e) => setUnlock(e.target.checked)} /> Unlock the account too</label>
    {!tls && <p className="muted small">LDAPS is off: this works only when the site controller runs on Windows.</p>}
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy || (mode === 'type' && (!pw || pw !== pw2))} onClick={() => void submit(() => onGo({ action: 'reset-password', password: mode === 'type' ? pw : '', mustChange: must, unlock }))}>Reset</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

function Offboard({ domain, onGo, onClose }: { domain: Rec; onGo: (b: Rec) => Promise<unknown>; onClose: () => void }) {
  const [pw, setPw] = useState(true); const [groups, setGroups] = useState(true); const [mgr, setMgr] = useState(false); const [to, setTo] = useState(''); const [note, setNote] = useState('');
  const { err, busy, submit } = useSubmit(onClose);
  return <Modal title="Offboard (leaver)" onClose={onClose}><div className="form">
    <p className="muted">Done in one go, in this order. The account is disabled in every case.</p>
    <label className="check"><input type="checkbox" checked={pw} onChange={(e) => setPw(e.target.checked)} /> Replace the password with an unknown one</label>
    <label className="check"><input type="checkbox" checked={groups} onChange={(e) => setGroups(e.target.checked)} /> Remove from every group (Domain Users stays)</label>
    <label className="check"><input type="checkbox" checked={mgr} onChange={(e) => setMgr(e.target.checked)} /> Clear the manager</label>
    <div><span className="lbl">Move to (optional)</span><OuPicker domain={domain} value={to} onChange={setTo} label="Move to" /></div>
    <Field label="Note (kept in the description)"><input value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} placeholder="Last working day, ticket number…" /></Field>
    <Err text={err} /><div className="actions"><button className="primary danger" disabled={busy} onClick={() => void submit(() => onGo({ action: 'offboard', resetPassword: pw, removeGroups: groups, clearManager: mgr, newParent: to, note }))}>Offboard</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

function MoveTo({ domain, onGo, onClose }: { domain: Rec; onGo: (dn: string) => Promise<unknown>; onClose: () => void }) {
  const [to, setTo] = useState(''); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title="Move" onClose={onClose}><div className="form"><span className="lbl">To</span><OuPicker domain={domain} value={to} onChange={setTo} label="Move to" />
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy || !to} onClick={() => void submit(() => onGo(to))}>Move</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

function PickObjects({ domain, kind, title, onGo, onClose }: { domain: Rec; kind: 'user' | 'group' | 'all'; title: string; onGo: (h: Hit[]) => Promise<unknown>; onClose: () => void }) {
  const [v, setV] = useState<Hit[]>([]); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title={title} onClose={onClose}><div className="form"><ObjectPicker domain={domain} kind={kind} multiple value={v} onChange={setV} />
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy || v.length === 0} onClick={() => void submit(() => onGo(v))}>OK</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

/** One object: what it is, its groups or members, and what this person may do to it. */
export function Panel({ domain, dn, onChanged, onOpen, onClose }: { domain: Rec; dn: string; onChanged: () => void; onOpen: (dn: string) => void; onClose?: () => void }) {
  const o = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/object?dn=${encodeURIComponent(dn)}`, [dn]);
  const [ask, setAsk] = useState<string | null>(null); const [said, setSaid] = useState<Rec | null>(null); const [tab, setTab] = useState('details');
  if (!o.data) return <div className="panel"><Loading loading={o.loading} error={o.error} /></div>;
  const x = o.data; const ob = x.obj as Rec;
  const run = async (body: Rec) => { const r = await act(domain.id, { dn, ...body }); setSaid(r); if (r.dn && r.dn !== dn) onOpen(String(r.dn)); else o.reload(); onChanged(); return r; };
  const user = ob.kind === 'user', group = ob.kind === 'group', comp = ob.kind === 'computer';
  const groupsOf = (x.memberOf ?? []) as Rec[]; const members = (x.members ?? []) as Rec[];
  const v = (x.values ?? {}) as Rec;
  return <div className="panel">
    <div className="panelhead">
      <div><h2>{String(ob.name)}</h2><p className="muted small">{ouPath(dn.replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn)}</p></div>
      {onClose && <button className="link" aria-label="Close" onClick={onClose}>×</button>}
    </div>
    <p className="badges">{ob.kind !== 'ou' && ob.kind !== 'group' && (ob.disabled ? <Badge tone="bad">Disabled</Badge> : <Badge tone="ok">Enabled</Badge>)}{ob.locked && <Badge tone="bad">Locked</Badge>}{ob.expired && <Badge tone="warn">Expired</Badge>}
      {ob.pwdExpired && <Badge tone="warn">Password expired</Badge>}{x.privileged && <Badge tone="warn">Privileged</Badge>}{x.protectedObject && <Badge>Built in</Badge>}</p>
    <div className="toolbar">
      {user && ob.locked && has(x, 'unlock') && <Act label="Unlock" primary run={() => run({ action: 'unlock' })} />}
      {user && has(x, 'reset') && <button onClick={() => setAsk('reset')}>Reset password</button>}
      {(user || comp) && has(x, 'enable') && (ob.disabled ? <Act label="Enable" run={() => run({ action: 'enable' })} /> : <Act label="Disable" confirm={`Disable ${ob.name}?`} run={() => run({ action: 'disable' })} />)}
      {has(x, 'modify') && ob.kind !== 'ou' && <button onClick={() => setAsk('edit')}>Edit</button>}
      {user && has(x, 'modify') && <button onClick={() => setAsk('expiry')}>Expiry</button>}
      {user && has(x, 'modify') && <Act label={ob.pwdNeverExpires ? 'Password may expire' : 'Password never expires'} run={() => run({ action: 'set-flags', pwdNeverExpires: !ob.pwdNeverExpires })} />}
      {user && has(x, 'modify') && <Act label="Must change password" run={() => run({ action: 'set-flags', mustChange: true })} />}
      {user && has(x, 'members') && <button onClick={() => setAsk('addto')}>Add to group</button>}
      {has(x, 'move') && !x.protectedObject && <button onClick={() => setAsk('move')}>Move</button>}
      {user && has(x, 'enable') && !x.protectedObject && <button className="danger" onClick={() => setAsk('offboard')}>Offboard</button>}
      {has(x, 'delete') && !x.protectedObject && <Act label="Delete" danger confirm={`Delete ${ob.name}? This cannot be undone and needs a second person's approval.`} run={() => run({ action: 'delete' })} />}
    </div>
    <div className="tabs" role="tablist">
      {['details', ...(group ? ['members'] : ob.kind === 'ou' ? [] : ['groups'])].map((t) => <button key={t} role="tab" aria-selected={tab === t} className={tab === t ? 'on' : ''} onClick={() => setTab(t)}>
        {t === 'details' ? 'Details' : t === 'members' ? `Members (${String(x.membersTotal)})` : `Member of (${groupsOf.length})`}</button>)}
    </div>
    {tab === 'details' && <Dl rows={[['Sign-in name', ob.sam], ['Principal name', ob.upn], ['Email', ob.mail], ['Title', ob.title], ['Department', ob.department], ['Company', v.company], ['Office', v.physicalDeliveryOfficeName],
      ['Telephone', v.telephoneNumber], ['Mobile', v.mobile], ['Manager', x.manager ? <button key="m" className="link" onClick={() => onOpen(String((x.manager as Rec).dn))}>{String((x.manager as Rec).name)}</button> : ''],
      ['Description', ob.description], ['Employee ID', v.employeeID], ['City', v.l], ['Home folder', v.homeDirectory ? `${v.homeDrive ?? ''} ${v.homeDirectory}` : ''], ['Logon script', v.scriptPath],
      ['Operating system', ob.os], ['DNS name', ob.dns], ['Last sign-in', ob.lastLogon ? fmtDate(ob.lastLogon) : ''], ['Password set', x.passwordLastSet ? fmtDate(x.passwordLastSet) : ''],
      ['Password expires', ob.pwdNeverExpires ? 'Never' : x.passwordExpires ? fmtDate(x.passwordExpires) : ''], ['Account expires', ob.accountExpires ? fmtDate(ob.accountExpires) : ''],
      ['Created', ob.created ? fmtDate(ob.created) : ''], ['Changed', x.changed ? fmtDate(x.changed) : '']].filter(([, val]) => val !== '' && val !== undefined && val !== null) as [string, string][]} />}
    {tab === 'groups' && <>
      {groupsOf.length === 0 ? <Empty>Only the primary group (usually Domain Users).</Empty> : <ul className="plain">{groupsOf.map((m) => <li key={String(m.dn)}>
        <button className="link" onClick={() => onOpen(String(m.dn))}>{String(m.name)}</button>
        {has(x, 'members') && <Act label="Remove" confirm={`Remove ${ob.name} from ${m.name}?`} run={() => act(domain.id, { action: 'member-remove', dn: m.dn, members: [dn] }).then((r) => { setSaid(r); o.reload(); })} />}</li>)}</ul>}
    </>}
    {tab === 'members' && <>
      {has(x, 'members') && <div className="toolbar"><button onClick={() => setAsk('addmember')}>Add members</button></div>}
      {members.length === 0 ? <Empty>No members.</Empty> : <ul className="plain">{members.map((m) => <li key={String(m.dn)}><button className="link" onClick={() => onOpen(String(m.dn))}>{String(m.name)}</button>
        {has(x, 'members') && <Act label="Remove" confirm={`Remove ${m.name} from ${ob.name}?`} run={() => run({ action: 'member-remove', members: [m.dn] })} />}</li>)}</ul>}
    </>}
    {ask === 'reset' && <Reset tls={!!domain.tls} onClose={() => setAsk(null)} onGo={run} />}
    {ask === 'offboard' && <Offboard domain={domain} onClose={() => setAsk(null)} onGo={run} />}
    {ask === 'move' && <MoveTo domain={domain} onClose={() => setAsk(null)} onGo={(to) => run({ action: 'move', newParent: to })} />}
    {ask === 'addmember' && <PickObjects domain={domain} kind="all" title={'Add members to ' + String(ob.name)} onClose={() => setAsk(null)} onGo={(h) => run({ action: 'member-add', members: h.map((m) => m.dn) })} />}
    {ask === 'addto' && <PickObjects domain={domain} kind="group" title={'Add ' + String(ob.name) + ' to groups'} onClose={() => setAsk(null)} onGo={async (h) => {
      let last: Rec = {}; for (const g of h) last = await act(domain.id, { action: 'member-add', dn: g.dn, members: [dn] });
      setSaid(last.approval ? last : { message: `Added to ${h.length} group(s).` }); o.reload(); onChanged(); }} />}
    {ask === 'expiry' && <Expiry onClose={() => setAsk(null)} onGo={(d) => run({ action: 'set-expiry', expires: d })} />}
    {ask === 'edit' && <EditUser x={x} onClose={() => setAsk(null)} onSave={(attrs) => run({ action: 'update', attrs })} />}
    {said && <Said r={said} onClose={() => setSaid(null)} />}
  </div>;
}

function Expiry({ onGo, onClose }: { onGo: (d: string) => Promise<unknown>; onClose: () => void }) {
  const [d, setD] = useState(''); const { err, busy, submit } = useSubmit(onClose);
  return <Modal title="Account expiry" onClose={onClose}><div className="form"><Field label="Last day (empty = never expires)"><input type="date" value={d} onChange={(e) => setD(e.target.value)} /></Field>
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy} onClick={() => void submit(() => onGo(d))}>Save</button><button onClick={onClose}>Cancel</button></div></div></Modal>;
}

const STATUS: Record<string, [string, string][]> = {
  user: [['', 'All'], ['enabled', 'Enabled'], ['disabled', 'Disabled'], ['locked', 'Locked'], ['expired', 'Expired'], ['neverexpires', 'Password never expires']],
  computer: [['', 'All'], ['enabled', 'Enabled'], ['disabled', 'Disabled']],
  group: [], ou: [],
};
const TITLES: Record<string, string> = { user: 'Users', group: 'Groups', computer: 'Computers', ou: 'Organisational units' };

/** A list of users, groups, computers or OUs: search, filter, choose several, act on all of them. */
export function ObjectList({ domain, kind, open, onOpen }: { domain: Rec; kind: 'user' | 'group' | 'computer' | 'ou'; open: string; onOpen: (dn: string) => void }) {
  const [q, setQ] = useState(''); const [go, setGo] = useState(''); const [status, setStatus] = useState(''); const [scope, setScope] = useState('');
  const [sel, setSel] = useState<string[]>([]); const [bulk, setBulk] = useState<string | null>(null); const [progress, setProgress] = useState<Rec | null>(null);
  const url = `/api/adsuite/domains/${domain.id}/search?kind=${kind}&q=${encodeURIComponent(go)}${status ? '&status=' + status : ''}${scope ? '&base=' + encodeURIComponent(scope) : ''}&limit=1000`;
  const res = useLoad<Rec>(url, [url]);
  const items = (res.data?.items ?? []) as Rec[];
  const all = items.length > 0 && items.every((i) => sel.includes(String(i.dn)));
  const toggle = (dn: string) => setSel(sel.includes(dn) ? sel.filter((d) => d !== dn) : [...sel, dn]);

  // a bulk action runs one object at a time so each gets its own result and the person sees the progress
  const runBulk = async (label: string, body: (dn: string) => Rec) => {
    const out: Rec[] = []; setProgress({ label, done: 0, total: sel.length, out });
    for (const dn of sel) {
      try { const r = await act(domain.id, body(dn)); out.push({ dn, ok: true, approval: !!r.approval, password: r.password }); }
      catch (e) { out.push({ dn, ok: false, error: e instanceof Error ? e.message : String(e) }); }
      setProgress({ label, done: out.length, total: sel.length, out: [...out] });
    }
    res.reload(); setSel([]);
  };
  const userish = kind === 'user' || kind === 'computer';
  return <div className="listpane">
    <h1>{TITLES[kind]}</h1>
    <div className="filters">
      <input aria-label="Search" placeholder={kind === 'user' ? 'Name, sign-in name or email' : 'Name'} value={q} onChange={(e) => setQ(e.target.value)} onKeyDown={(e) => { if (e.key === 'Enter') setGo(q); }} />
      <button className="primary" onClick={() => setGo(q)}>Search</button>
      {STATUS[kind].length > 0 && <select aria-label="Status" value={status} onChange={(e) => setStatus(e.target.value)}>{STATUS[kind].map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>}
      <div className="scope"><OuPicker domain={domain} value={scope} onChange={setScope} label="In OU" allowDomain />{scope && <button className="link" onClick={() => setScope('')}>Clear</button>}</div>
    </div>
    {sel.length > 0 && <div className="bulkbar"><strong>{sel.length} chosen</strong>
      {userish && <button onClick={() => void runBulk('Enabling', (dn) => ({ action: 'enable', dn }))}>Enable</button>}
      {userish && <button onClick={() => { if (confirm(`Disable ${sel.length} account(s)?`)) void runBulk('Disabling', (dn) => ({ action: 'disable', dn })); }}>Disable</button>}
      {kind === 'user' && <button onClick={() => void runBulk('Unlocking', (dn) => ({ action: 'unlock', dn }))}>Unlock</button>}
      {kind === 'user' && <button onClick={() => { if (confirm(`Give ${sel.length} account(s) new passwords? They are shown once.`)) void runBulk('Resetting passwords', (dn) => ({ action: 'reset-password', dn, mustChange: true })); }}>Reset passwords</button>}
      {kind !== 'ou' && <button onClick={() => setBulk('addto')}>Add to group</button>}
      <button onClick={() => setBulk('move')}>Move</button>
      <button className="danger" onClick={() => { if (confirm(`Delete ${sel.length} object(s)? Each waits for a second person's approval.`)) void runBulk('Deleting', (dn) => ({ action: 'delete', dn })); }}>Delete</button>
      <button className="link" onClick={() => setSel([])}>Clear</button></div>}
    <Loading loading={res.loading} error={res.error} />
    {res.data && (items.length === 0 ? <Empty>Nothing found.</Empty> : <div className="tablewrap"><table className="grid">
      <thead><tr><th style={{ width: 32 }}><input type="checkbox" aria-label="Choose all" checked={all} onChange={() => setSel(all ? [] : items.map((i) => String(i.dn)))} /></th>
        <th>Name</th>{kind === 'user' && <><th>Sign-in</th><th>Title / department</th><th>Last sign-in</th></>}{kind === 'computer' && <><th>Operating system</th><th>Last sign-in</th></>}
        {kind === 'group' && <th>Description</th>}<th>OU</th><th /></tr></thead>
      <tbody>{items.map((i) => { const dn = String(i.dn); return <tr key={dn} className={open === dn ? 'on' : ''} onClick={() => onOpen(dn)}>
        <td onClick={(e) => e.stopPropagation()}><input type="checkbox" aria-label={'Choose ' + i.name} checked={sel.includes(dn)} onChange={() => toggle(dn)} /></td>
        <td><strong>{String(i.name)}</strong>{i.mail && kind === 'user' ? <div className="muted small">{String(i.mail)}</div> : null}</td>
        {kind === 'user' && <><td>{String(i.sam ?? '')}</td><td className="small">{[i.title, i.department].filter(Boolean).join(' · ')}</td><td className="small">{i.lastLogon ? fmtDate(i.lastLogon) : 'Never'}</td></>}
        {kind === 'computer' && <><td className="small">{String(i.os ?? '')}</td><td className="small">{i.lastLogon ? fmtDate(i.lastLogon) : 'Never'}</td></>}
        {kind === 'group' && <td className="small">{String(i.description ?? '')}</td>}
        <td className="small muted">{ouPath(dn.replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn)}</td>
        <td>{i.disabled && <Badge tone="bad">Disabled</Badge>}{i.locked && <Badge tone="bad">Locked</Badge>}{i.expired && <Badge tone="warn">Expired</Badge>}</td></tr>; })}</tbody></table></div>)}
    {res.data?.truncated ? <p className="muted small">Only the first 1,000 are shown. Narrow the search or choose an OU.</p> : null}
    {bulk === 'move' && <MoveTo domain={domain} onClose={() => setBulk(null)} onGo={(to) => runBulk('Moving', (dn) => ({ action: 'move', dn, newParent: to }))} />}
    {bulk === 'addto' && <PickObjects domain={domain} kind="group" title={`Add ${sel.length} to groups`} onClose={() => setBulk(null)} onGo={async (h) => {
      const dns = [...sel]; const out: Rec[] = []; setProgress({ label: 'Adding to groups', done: 0, total: h.length, out });
      for (const g of h) { try { const r = await act(domain.id, { action: 'member-add', dn: g.dn, members: dns }); out.push({ dn: g.dn, ok: true, approval: !!r.approval }); } catch (e) { out.push({ dn: g.dn, ok: false, error: e instanceof Error ? e.message : String(e) }); } setProgress({ label: 'Adding to groups', done: out.length, total: h.length, out: [...out] }); }
      setSel([]); }} />}
    {progress && <Progress p={progress} onClose={() => setProgress(null)} />}
  </div>;
}

export function Progress({ p, onClose }: { p: Rec; onClose: () => void }) {
  const out = (p.out ?? []) as Rec[]; const finished = p.done >= p.total;
  const pws = out.filter((o) => o.password);
  return <Modal title={`${p.label}: ${p.done} of ${p.total}`} onClose={() => finished && onClose()}>
    <div className="bar"><i style={{ width: `${(100 * p.done) / Math.max(1, p.total)}%` }} /></div>
    <p>{out.filter((o) => o.ok && !o.approval).length} done, {out.filter((o) => o.approval).length} sent for approval, {out.filter((o) => !o.ok).length} failed.</p>
    {out.some((o) => !o.ok) && <ul className="plain small">{out.filter((o) => !o.ok).map((o) => <li key={String(o.dn)}><code>{ouPath(String(o.dn)).split(' › ').pop()}</code>: <span className="err">{String(o.error)}</span></li>)}</ul>}
    {pws.length > 0 && <><p>New passwords, shown once:</p><table className="grid small"><tbody>{pws.map((o) => <tr key={String(o.dn)}><td>{ouPath(String(o.dn)).split(' › ').pop()}</td><td><code>{String(o.password)}</code></td></tr>)}</tbody></table></>}
    {finished && <div className="actions"><button className="primary" onClick={onClose}>Close</button></div>}
  </Modal>;
}
