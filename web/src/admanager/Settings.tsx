import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Err, Field, fmtDate, Modal, useSubmit } from '../ui';
import { ALL_FIELDS } from './fields';
import { ObjectPicker, OuPicker, ouPath, rdn } from './pickers';
import { FieldInput } from './UserForm';

type Hit = { dn: string; name: string };

function TemplateForm({ domain, t, onClose, onSaved }: { domain: Rec; t: Rec | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(String(t?.name ?? '')); const [desc, setDesc] = useState(String(t?.description ?? ''));
  const [ou, setOu] = useState(String(t?.ou ?? '')); const [groups, setGroups] = useState<Hit[]>(((t?.groups ?? []) as string[]).map((dn) => ({ dn, name: rdn(dn) })));
  const [attrs, setAttrs] = useState<Rec>({ ...((t?.attrs ?? {}) as Rec) });
  const n = (t?.naming ?? {}) as Rec; const o = (t?.options ?? {}) as Rec;
  const [display, setDisplay] = useState(String(n.display ?? '{first} {last}')); const [sam, setSam] = useState(String(n.sam ?? '{first}.{last}'));
  const [email, setEmail] = useState(String(n.email ?? '')); const [suffix, setSuffix] = useState(String(n.upnSuffix ?? ''));
  const [must, setMust] = useState(o.mustChange ?? true); const [never, setNever] = useState(!!o.pwdNeverExpires); const [days, setDays] = useState(String(o.expiresDays ?? ''));
  const { err, busy, submit } = useSubmit(() => { onSaved(); onClose(); });
  const body = () => ({ name, description: desc, ou, groups: groups.map((g) => g.dn), attrs: Object.fromEntries(Object.entries(attrs).filter(([, v]) => v !== '')),
    naming: { display, sam, email, upnSuffix: suffix }, options: { mustChange: must, pwdNeverExpires: never, expiresDays: days ? Number(days) : undefined } });
  const common = ['title', 'department', 'company', 'division', 'physicalDeliveryOfficeName', 'streetAddress', 'l', 'st', 'postalCode', 'co', 'scriptPath', 'homeDrive', 'homeDirectory', 'profilePath', 'employeeType'];
  return <Modal title={t ? 'Edit template' : 'New user template'} onClose={onClose}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} placeholder="Sales staff" /></Field>
    <Field label="Description"><input value={desc} onChange={(e) => setDesc(e.target.value)} /></Field>
    <div><span className="lbl">New accounts go to</span><OuPicker domain={domain} value={ou} onChange={setOu} /></div>
    <div><span className="lbl">Groups</span><ObjectPicker domain={domain} kind="group" multiple value={groups} onChange={setGroups} label="Groups" /></div>
    <fieldset><legend>Naming</legend><p className="muted small">Use {'{first}'}, {'{last}'}, {'{initials}'}, {'{f}'} (first letter of the first name), {'{l}'}.</p>
      <div className="grid2"><Field label="Display name"><input value={display} onChange={(e) => setDisplay(e.target.value)} /></Field><Field label="Sign-in name"><input value={sam} onChange={(e) => setSam(e.target.value)} /></Field>
        <Field label="Email (optional)"><input value={email} onChange={(e) => setEmail(e.target.value)} placeholder={'{first}.{last}@' + String(domain.dnsName)} /></Field><Field label="UPN suffix (optional)"><input value={suffix} onChange={(e) => setSuffix(e.target.value)} placeholder={String(domain.dnsName)} /></Field></div></fieldset>
    <fieldset><legend>Defaults</legend><div className="grid2">{ALL_FIELDS.filter((f) => common.includes(f.k)).map((f) => <FieldInput key={f.k} f={f} value={String(attrs[f.k] ?? '')} onChange={(v) => setAttrs({ ...attrs, [f.k]: v })} />)}</div></fieldset>
    <fieldset><legend>Account</legend>
      <label className="check"><input type="checkbox" checked={must && !never} disabled={never} onChange={(e) => setMust(e.target.checked)} /> Must change password at first sign-in</label>
      <label className="check"><input type="checkbox" checked={never} onChange={(e) => setNever(e.target.checked)} /> Password never expires</label>
      <Field label="Account expires after (days, empty = never)"><input type="number" min={1} value={days} onChange={(e) => setDays(e.target.value)} /></Field></fieldset>
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy || !name} onClick={() => void submit(() => t ? api(`/api/adsuite/templates/${t.id}`, { method: 'PUT', body: body() }) : api(`/api/adsuite/domains/${domain.id}/templates`, { method: 'POST', body: body() }))}>Save</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function Templates({ domain }: { domain: Rec }) {
  const list = useLoad<Rec[]>(`/api/adsuite/domains/${domain.id}/templates`, [domain.id]); const [edit, setEdit] = useState<Rec | null | undefined>(undefined);
  const rows = (list.data ?? []) as Rec[];
  return <Card title="User templates">
    <p className="muted">A template fills the new-user form: where the account goes, its groups, default fields, how names are made and the account options.</p>
    <div className="actions"><button className="primary" onClick={() => setEdit(null)}>New template</button></div>
    {rows.length === 0 ? <Empty>No templates yet.</Empty> : <table className="adtable"><thead><tr><th>Name</th><th>OU</th><th>Groups</th><th>Naming</th><th /></tr></thead><tbody>
      {rows.map((t) => <tr key={t.id}><td><strong>{t.name}</strong><div className="muted small">{t.description}</div></td><td className="small">{t.ou ? ouPath(t.ou, domain.baseDn) : '–'}</td>
        <td className="small">{((t.groups ?? []) as string[]).map(rdn).join(', ') || '–'}</td><td className="small"><code>{String(t.naming?.sam ?? '{first}.{last}')}</code></td>
        <td className="actions"><button onClick={() => setEdit(t)}>Edit</button><Act label="Delete" danger confirm={`Delete the template ${t.name}?`} run={async () => { await api(`/api/adsuite/templates/${t.id}`, { method: 'DELETE' }); list.reload(); }} /></td></tr>)}</tbody></table>}
    {edit !== undefined && <TemplateForm domain={domain} t={edit} onClose={() => setEdit(undefined)} onSaved={list.reload} />}
  </Card>;
}

const KINDS: Record<string, string> = { 'disable-inactive-users': 'Disable inactive users', 'disable-stale-computers': 'Disable stale computers', 'move-disabled-users': 'Move disabled users' };

function PolicyForm({ domain, p, onClose, onSaved }: { domain: Rec; p: Rec | null; onClose: () => void; onSaved: () => void }) {
  const [name, setName] = useState(String(p?.name ?? '')); const [kind, setKind] = useState(String(p?.kind ?? 'disable-inactive-users')); const [days, setDays] = useState(Number(p?.days ?? 90));
  const [ou, setOu] = useState(String(p?.ou ?? '')); const [move, setMove] = useState(String(p?.moveTo ?? '')); const [on, setOn] = useState(!!p?.enabled);
  const { err, busy, submit } = useSubmit(() => { onSaved(); onClose(); });
  const body = { name, kind, days, ou, moveTo: move, enabled: on };
  return <Modal title={p ? 'Edit policy' : 'New automation policy'} onClose={onClose}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} placeholder="Disable after 90 days" /></Field>
    <Field label="What it does"><select value={kind} onChange={(e) => setKind(e.target.value)}>{Object.entries(KINDS).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
    {kind !== 'move-disabled-users' && <Field label="No sign-in for (days, at least 30)"><input type="number" min={30} value={days} onChange={(e) => setDays(Number(e.target.value))} /></Field>}
    <div><span className="lbl">Only in this OU (empty = whole domain)</span><OuPicker domain={domain} value={ou} onChange={setOu} allowDomain /></div>
    <div><span className="lbl">Then move to {kind === 'move-disabled-users' ? '' : '(optional)'}</span><OuPicker domain={domain} value={move} onChange={setMove} /></div>
    <label className="check"><input type="checkbox" checked={on} onChange={(e) => setOn(e.target.checked)} /> Run every day</label>
    <p className="muted small">Built-in, privileged and DeskAsk's own accounts are never touched; at most 200 per run. Use Preview before switching it on.</p>
    <Err text={err} /><div className="actions"><button className="primary" disabled={busy || !name} onClick={() => void submit(() => p ? api(`/api/adsuite/policies/${p.id}`, { method: 'PUT', body }) : api(`/api/adsuite/domains/${domain.id}/policies`, { method: 'POST', body }))}>Save</button><button onClick={onClose}>Cancel</button></div>
  </div></Modal>;
}

export function Automation({ domain }: { domain: Rec }) {
  const list = useLoad<Rec[]>(`/api/adsuite/domains/${domain.id}/policies`, [domain.id]); const [edit, setEdit] = useState<Rec | null | undefined>(undefined); const [pv, setPv] = useState<Rec | null>(null);
  const rows = (list.data ?? []) as Rec[];
  return <Card title="Automation">
    <p className="muted">Clean-up that runs every day: disable accounts and computers nobody has used, and move disabled accounts out of the way.</p>
    <div className="actions"><button className="primary" onClick={() => setEdit(null)}>New policy</button></div>
    {rows.length === 0 ? <Empty>No policies yet.</Empty> : <table className="adtable"><thead><tr><th>Policy</th><th>Where</th><th>Status</th><th>Last run</th><th /></tr></thead><tbody>
      {rows.map((p) => <tr key={p.id}><td><strong>{p.name}</strong><div className="muted small">{KINDS[p.kind]}{p.kind !== 'move-disabled-users' ? `, ${p.days} days` : ''}{p.moveTo ? ` → ${ouPath(p.moveTo, domain.baseDn)}` : ''}</div></td>
        <td className="small">{p.ou ? ouPath(p.ou, domain.baseDn) : 'Whole domain'}</td><td>{p.enabled ? <Badge tone="ok">Daily</Badge> : <Badge>Off</Badge>}</td>
        <td className="small">{p.lastRun ? fmtDate(p.lastRun) : '–'}<div className="muted">{p.lastResult}</div></td>
        <td className="actions"><Act label="Preview" run={async () => setPv({ p, r: await api<Rec>(`/api/adsuite/policies/${p.id}/preview`, { method: 'POST', body: {} }) })} />
          <Act label="Run now" confirm={`Run ${p.name} now?`} run={async () => { await api(`/api/adsuite/policies/${p.id}/run`, { method: 'POST', body: {} }); setTimeout(list.reload, 4000); }} />
          <button onClick={() => setEdit(p)}>Edit</button><Act label="Delete" danger confirm={`Delete ${p.name}?`} run={async () => { await api(`/api/adsuite/policies/${p.id}`, { method: 'DELETE' }); list.reload(); }} /></td></tr>)}</tbody></table>}
    {edit !== undefined && <PolicyForm domain={domain} p={edit} onClose={() => setEdit(undefined)} onSaved={list.reload} />}
    {pv && <Modal title={`Preview: ${pv.p.name}`} onClose={() => setPv(null)}>
      <p>It would change <strong>{(pv.r.would as Rec[]).length}</strong> now{(pv.r.would as Rec[]).length >= 200 ? ' (the first 200; the rest next time)' : ''}.</p>
      <ul className="plain small scroll">{(pv.r.would as Rec[]).map((x) => <li key={String(x.dn)}>{String(x.name)} <span className="muted">{ouPath(String(x.dn).replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn)}</span></li>)}</ul>
      {(pv.r.skipped as Rec[]).length > 0 && <><p>Left alone:</p><ul className="plain small">{(pv.r.skipped as Rec[]).map((x) => <li key={String(x.dn)}>{String(x.name)} <Badge>{String(x.why)}</Badge></li>)}</ul></>}
      <div className="actions"><button className="primary" onClick={() => setPv(null)}>Close</button></div></Modal>}
  </Card>;
}
