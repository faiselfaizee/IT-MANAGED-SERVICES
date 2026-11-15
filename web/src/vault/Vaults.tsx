import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Empty, fmtDate, Loading, Modal } from '../ui';
import * as S from './session';
import { KIND_WORDS, ROLE_WORDS } from './types';

type Member = { type: 'user' | 'group'; id: string; role: string; name?: string };

function Members({ v, onDone }: { v: S.VaultRow; onDone: () => void }) {
  const m = useLoad<Rec>(`/api/vault/vaults/${v.id}/members`);
  const dir = useLoad<Rec>('/api/vault/users');
  const [list, setList] = useState<Member[] | null>(null); const [add, setAdd] = useState(''); const [err, setErr] = useState(''); const [msg, setMsg] = useState('');
  useEffect(() => { if (m.data && !list) setList((m.data.grants as Rec[]).map((g) => ({ type: g.type, id: g.id, role: g.role, name: g.name }))); }, [m.data, list]);
  const users = (dir.data?.users ?? []) as Rec[]; const groups = (dir.data?.groups ?? []) as Rec[];
  const people = (m.data?.people ?? []) as Rec[];
  const canManage = !!m.data?.canManage || S.current().me?.settings?.role === 'admin';
  if (!list) return <Loading loading error={m.error} />;
  const options = [...groups.map((g) => ({ key: 'group:' + g.id, label: 'Group: ' + g.name })), ...users.map((u) => ({ key: 'user:' + u.id, label: `${u.name} (${u.email})${u.setUp ? '' : ' – has not set up the vault yet'}` }))]
    .filter((o) => !list.some((l) => l.type + ':' + l.id === o.key));
  const save = async () => {
    setErr(''); setMsg('');
    try {
      const r = await api<Rec>(`/api/vault/vaults/${v.id}/members`, { method: 'PUT', body: { members: list.map(({ type, id, role }) => ({ type, id, role })) } });
      const n = await S.givePending();
      setMsg(r.needsRotation ? 'Saved. Someone who held the vault\'s key lost access: change the vault\'s keys so a copy they kept stops working.' : `Saved.${n ? ` ${n} people received the vault's key.` : ''}`);
      m.reload(); onDone();
    } catch (e) { setErr((e as Error).message); }
  };
  return <div className="form">
    <table><thead><tr><th>Who</th><th>May</th><th /></tr></thead>
      <tbody>{list.map((l, i) => <tr key={l.type + l.id}><td>{l.type === 'group' ? 'Group: ' : ''}{l.name ?? (l.type === 'group' ? groups.find((g) => g.id === l.id)?.name : users.find((u) => u.id === l.id)?.name) ?? l.id}</td>
        <td><select disabled={!canManage} value={l.role} onChange={(e) => setList(list.map((x, j) => j === i ? { ...x, role: e.target.value } : x))} aria-label="Role">{Object.entries(ROLE_WORDS).map(([k, w]) => <option key={k} value={k}>{w}</option>)}</select></td>
        <td>{canManage && <button className="small" onClick={() => setList(list.filter((_, j) => j !== i))}>Remove</button>}</td></tr>)}</tbody></table>
    {canManage && <div className="tools"><select value={add} onChange={(e) => setAdd(e.target.value)} aria-label="Add someone"><option value="">Add a person or group…</option>{options.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}</select>
      <button disabled={!add} onClick={() => { const [type, id] = add.split(':'); setList([...list, { type: type as Member['type'], id, role: 'view' }]); setAdd(''); }}>Add</button></div>}
    <p className="muted small">May ask for access: sees item names only and asks to open one. Can view: opens items. Can edit: also changes items and answers requests. Manages: also changes who has access.</p>
    {people.length > 0 && <details open><summary>Who has the key ({people.filter((p) => p.state === 'has_key').length} of {people.length})</summary>
      <ul className="plain">{people.map((p) => <li key={p.id}>{p.name}: <span className={'badge ' + (p.state === 'has_key' ? 'ok' : 'warn')}>{p.state === 'has_key' ? 'has the key' : p.state === 'waiting' ? 'gets the key when a manager next opens the Vault' : 'has not set up the vault yet'}</span></li>)}</ul></details>}
    {err && <p className="err" role="alert">{err}</p>}
    {msg && <p className="note" role="status">{msg}</p>}
    {canManage && <div className="tools"><button className="primary" onClick={() => void save()}>Save members</button></div>}
  </div>;
}

function Folders({ v }: { v: S.VaultRow }) {
  const f = useLoad<Rec>(`/api/vault/vaults/${v.id}/folders`);
  const [names, setNames] = useState<Rec[]>([]); const [n, setN] = useState(''); const [err, setErr] = useState('');
  useEffect(() => { void (async () => { const o = []; for (const x of (f.data?.items ?? []) as Rec[]) o.push({ id: x.id, name: await S.folderName(v.id, x.nameEnc) }); setNames(o); })(); }, [f.data, v.id]);
  const run = async (fn: () => Promise<unknown>) => { setErr(''); try { await fn(); f.reload(); } catch (e) { setErr((e as Error).message); } };
  return <div className="form">
    {names.length === 0 ? <Empty>No folders.</Empty> : <ul className="plain">{names.map((x) => <li key={x.id}>{x.name} <button className="small" onClick={() => void run(async () => { const nn = window.prompt('New name', x.name); if (nn) await api(`/api/vault/folders/${x.id}`, { method: 'PUT', body: { nameEnc: await S.encFolder(v.id, nn) } }); })}>Rename</button>
      <button className="small" onClick={() => void run(async () => { if (window.confirm(`Delete the folder "${x.name}"? Its items stay in the vault.`)) await api(`/api/vault/folders/${x.id}`, { method: 'DELETE' }); })}>Delete</button></li>)}</ul>}
    <div className="tools"><input value={n} onChange={(e) => setN(e.target.value)} placeholder="New folder" aria-label="Folder name" /><button disabled={!n.trim()} onClick={() => void run(async () => { await api(`/api/vault/vaults/${v.id}/folders`, { method: 'POST', body: { nameEnc: await S.encFolder(v.id, n.trim()) } }); setN(''); })}>Add folder</button></div>
    {err && <p className="err" role="alert">{err}</p>}
  </div>;
}

function Log({ v }: { v: S.VaultRow }) {
  const e = useLoad<Rec>(`/api/vault/vaults/${v.id}/events`);
  const list = (e.data?.items ?? []) as Rec[];
  return list.length ? <ul className="plain log">{list.map((x) => <li key={x.id}><time>{fmtDate(x.at)}</time> {x.user}: {String(x.action).replace(/_/g, ' ')}{x.detail ? <span className="muted"> · {x.detail}</span> : null}</li>)}</ul> : <Empty>Nothing yet.</Empty>;
}

function NewVault({ onDone }: { onDone: () => void }) {
  const companies = useLoad<Rec[]>('/api/assets/companies');
  const cos = (Array.isArray(companies.data) ? companies.data : ((companies.data as Rec | null)?.items ?? [])) as Rec[];
  const taken = new Set(S.current().vaults.filter((v) => v.kind === 'customer').map((v) => v.companyId));
  const [kind, setKind] = useState('customer'); const [co, setCo] = useState(''); const [name, setName] = useState(''); const [err, setErr] = useState('');
  return <form className="form" onSubmit={async (e) => { e.preventDefault(); setErr(''); try { await S.createVault(kind, name, kind === 'customer' ? Number(co) : undefined); onDone(); } catch (x) { setErr((x as Error).message); } }}>
    <label className="check"><input type="radio" checked={kind === 'customer'} onChange={() => setKind('customer')} /> For a customer (their servers, network and cloud accounts)</label>
    <label className="check"><input type="radio" checked={kind === 'internal'} onChange={() => setKind('internal')} /> Internal (your own company's accounts)</label>
    {kind === 'customer' && <label><span>Customer</span><select value={co} onChange={(e) => { setCo(e.target.value); setName(cos.find((c) => String(c.id) === e.target.value)?.name ?? ''); }}><option value="">Choose…</option>{cos.map((c) => <option key={c.id} value={c.id} disabled={taken.has(c.id)}>{c.name}{taken.has(c.id) ? ' (has a vault)' : ''}</option>)}</select></label>}
    <label><span>Name</span><input value={name} onChange={(e) => setName(e.target.value)} placeholder={kind === 'internal' ? 'Internal IT' : 'Customer name'} /></label>
    <p className="muted small">You become the vault's first manager. Add people or groups afterwards.</p>
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={kind === 'customer' ? !co : !name.trim()}>Create vault</button></div>
  </form>;
}

export default function Vaults() {
  const session = S.useSession();
  const [open, setOpen] = useState<{ v: S.VaultRow; tab: 'members' | 'folders' | 'log' } | null>(null);
  const [creating, setCreating] = useState(false); const [step, setStep] = useState(''); const [err, setErr] = useState('');
  const me = session.me?.settings ?? {};
  const reload = () => void S.refresh().catch((e: Error) => setErr(e.message));
  const personal = session.vaults.find((v) => v.kind === 'personal');
  return <>
    <div className="dash-head"><h1>Vaults</h1><div className="tools">{me.canCreate && <button className="primary" onClick={() => setCreating(true)}>New vault</button>}
      {!personal && <button onClick={() => void S.createVault('personal', 'My vault').catch((e: Error) => setErr(e.message))}>Create my personal vault</button>}</div></div>
    {err && <p className="err" role="alert">{err}</p>}
    {step && <p className="note" role="status">{step}</p>}
    {session.vaults.length === 0 && <Empty>No vaults yet.</Empty>}
    <div className="vault-cards">{session.vaults.map((v) => {
      const k = S.vaultKeys(v.id);
      return <section key={v.id} className={'vault-card k-' + v.kind}>
        <header><h2>{v.name}</h2><span className="muted small">{KIND_WORDS[v.kind]}{v.company && v.company !== v.name ? ' · ' + v.company : ''}</span></header>
        <p className="figs"><b>{v.items}</b> items · {v.kind === 'personal' ? 'only you' : ROLE_WORDS[v.role].toLowerCase()}</p>
        {!k?.dir && <p className="badge warn">Waiting for a manager to give you the key</p>}
        {v.needsRotation && S.rank(v.role) === 4 && <div className="note rotate-note"><span>Someone who held this vault's key no longer has access.</span>
          <button className="primary small" onClick={async () => { setErr(''); try { await S.rotateVault(v.id, setStep); setStep('The vault has new keys.'); } catch (e) { setStep(''); setErr((e as Error).message); } }}>Change the vault's keys</button></div>}
        <div className="tools">
          <a href={`#/vault?vault=${v.id}`}>Open</a>
          {v.kind !== 'personal' && (S.rank(v.role) === 4 || me.role === 'admin') && <button className="small" onClick={() => setOpen({ v, tab: 'members' })}>Members</button>}
          {S.rank(v.role) >= 3 && k?.item && <button className="small" onClick={() => setOpen({ v, tab: 'folders' })}>Folders</button>}
          {S.rank(v.role) >= 3 && <button className="small" onClick={() => setOpen({ v, tab: 'log' })}>Activity</button>}
          {v.kind !== 'personal' && S.rank(v.role) === 4 && <button className="small" onClick={async () => { const n = window.prompt('New name', v.name); if (n) { await api(`/api/vault/vaults/${v.id}`, { method: 'PUT', body: { name: n } }); reload(); } }}>Rename</button>}
          {v.kind !== 'personal' && (S.rank(v.role) === 4 || me.role === 'admin') && <button className="small danger" onClick={async () => { if (window.prompt(`Delete the vault "${v.name}" and its ${v.items} items for good? Type the vault's name to confirm.`) === v.name) { await api(`/api/vault/vaults/${v.id}`, { method: 'DELETE' }); reload(); } }}>Delete</button>}
        </div>
      </section>;
    })}</div>
    {creating && <Modal title="New vault" onClose={() => setCreating(false)}><NewVault onDone={() => setCreating(false)} /></Modal>}
    {open && <Modal title={`${open.v.name}: ${open.tab === 'members' ? 'members' : open.tab === 'folders' ? 'folders' : 'activity'}`} onClose={() => setOpen(null)}>
      {open.tab === 'members' ? <Members v={open.v} onDone={reload} /> : open.tab === 'folders' ? <Folders v={open.v} /> : <Log v={open.v} />}
      <div className="tools"><button onClick={() => setOpen(null)}>Close</button></div>
    </Modal>}
  </>;
}
