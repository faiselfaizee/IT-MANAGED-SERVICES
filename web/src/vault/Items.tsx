import { useEffect, useMemo, useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { ago, copyText, Empty, fmtDate, Loading, Modal } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { customOf, KIND_WORDS, TYPES, typeDef, type Custom, type FieldDef } from './types';
import { Strength } from './Gate';
import { Generator } from './Tools';

const GLYPH: Record<string, string> = { login: 'L', server: 'S', network: 'N', database: 'D', wifi: 'W', ssh: 'K', apikey: 'A', card: 'C', identity: 'I', note: '¶' };

/** A secret value: hidden until shown, copied without showing; both are written to the item's log. */
function Secret({ itemId, field, value, multiline }: { itemId: number; field: string; value: string; multiline?: boolean }) {
  const [shown, setShown] = useState(false); const [copied, setCopied] = useState(false);
  return <span className="secret">
    {shown ? (multiline ? <pre>{value}</pre> : <code>{value}</code>) : <code aria-label="Hidden">{'•'.repeat(Math.min(Math.max(value.length, 8), 16))}</code>}
    <button type="button" className="small" onClick={() => { if (!shown) S.note(itemId, 'revealed', field); setShown(!shown); }}>{shown ? 'Hide' : 'Show'}</button>
    <button type="button" className="small" onClick={() => { copyText(value); S.note(itemId, 'copied', field); setCopied(true); setTimeout(() => setCopied(false), 1500); clearLater(value); }}>{copied ? 'Copied' : 'Copy'}</button>
  </span>;
}
/** Clears the clipboard after 30 seconds if it still holds the secret. */
function clearLater(v: string) {
  setTimeout(async () => { try { if ((await navigator.clipboard.readText()) === v) await navigator.clipboard.writeText(''); } catch { /* not allowed: leave it */ } }, 30_000);
}

function Totp({ secret, itemId }: { secret: string; itemId: number }) {
  const [c, setC] = useState<{ code: string; left: number } | null>(null);
  useEffect(() => { let live = true; const tick = () => void C.totp(secret).then((x) => live && setC(x)); tick(); const t = setInterval(tick, 1000); return () => { live = false; clearInterval(t); }; }, [secret]);
  if (!c) return <span className="muted">Not a valid one-time code key</span>;
  return <span className="secret"><code className="totp">{c.code.replace(/(\d{3})(\d+)/, '$1 $2')}</code><span className="muted small">{c.left}s</span>
    <button type="button" className="small" onClick={() => { copyText(c.code); S.note(itemId, 'copied', 'one-time code'); }}>Copy</button></span>;
}

function FieldValue({ f, v, itemId }: { f: FieldDef; v: string; itemId: number }) {
  if (f.kind === 'totp') return <Totp secret={v} itemId={itemId} />;
  if (f.secret) return <Secret itemId={itemId} field={f.label} value={v} multiline={f.multiline} />;
  if (f.kind === 'url') { const href = /^https?:\/\//i.test(v) ? v : 'https://' + v; return <span className="secret"><a href={href} target="_blank" rel="noreferrer noopener" onClick={() => S.note(itemId, 'opened', f.label)}>{v}</a><button type="button" className="small" onClick={() => copyText(v)}>Copy</button></span>; }
  return <span className="secret">{f.multiline ? <span className="multi">{v}</span> : <span>{v}</span>}<button type="button" className="small" onClick={() => { copyText(v); }}>Copy</button></span>;
}

/** The decrypted fields of an item. */
export function Fields({ type, data, itemId }: { type: string; data: S.Data; itemId: number }) {
  const t = typeDef(type);
  const rows = t.fields.filter((f) => data[f.k]);
  const custom = customOf(data);
  return <dl className="kv vault-fields">
    {rows.map((f) => <div key={f.k} className="kv-row"><dt>{f.label}</dt><dd><FieldValue f={f} v={data[f.k]} itemId={itemId} /></dd></div>)}
    {custom.map((c, i) => <div key={'c' + i} className="kv-row"><dt>{c.name}</dt><dd><FieldValue f={{ k: c.name, label: c.name, secret: c.hidden }} v={c.value} itemId={itemId} /></dd></div>)}
  </dl>;
}

function Managed({ item, data, onChange }: { item: S.Item; data: S.Data | null; onChange: () => void }) {
  const m = useLoad<Rec>(item.managed ? `/api/vault/items/${item.id}/managed` : null, [item.id, item.managed]);
  const [secret, setSecret] = useState<Rec | null>(null); const [err, setErr] = useState(''); const [form, setForm] = useState(false);
  const [f, setF] = useState<Rec>({ account: data?.username ?? '', target: 'local', rotateDays: 30, resetAfterUse: false, rotateNow: false });
  const acc = m.data?.account as Rec | undefined;
  useInterval(() => { if (acc?.state === 'rotating') m.reload(); }, acc?.state === 'rotating' ? 3000 : null);
  const run = async (fn: () => Promise<unknown>) => { setErr(''); try { await fn(); } catch (e) { setErr((e as Error).message); } };
  if (!item.managed) {
    if (!item.canEdit || !item.assetId || !['server', 'login', 'database', 'network'].includes(item.type) || !data?.password) return null;
    return <section className="managed-box">
      <h3>Password changes</h3>
      {!form ? <><p className="muted">DeskAsk can change this password on a schedule through the agent on the linked asset. The server then holds the password so the agent can set it; reading it is always logged.</p>
        <button onClick={() => setForm(true)}>Let DeskAsk change this password</button></> : <form className="form" onSubmit={(e) => { e.preventDefault(); void run(async () => {
          await api(`/api/vault/items/${item.id}/managed`, { method: 'POST', body: { ...f, rotateDays: Number(f.rotateDays), password: data.password } });
          // the password now lives sealed on the server: take it out of the encrypted item
          const { password: _drop, ...rest } = data; void _drop;
          const enc = await S.encItem(item.vaultId, await S.title(item), rest as S.Data);
          await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: item.revision } });
          setForm(false); onChange();
        }); }}>
          <label><span>Account name (as the computer knows it, no domain prefix)</span><input value={f.account} onChange={(e) => setF({ ...f, account: e.target.value })} /></label>
          <label><span>Kind of account</span><select value={f.target} onChange={(e) => setF({ ...f, target: e.target.value })}><option value="local">Windows local account</option><option value="domain">Active Directory account (agent on a domain controller)</option><option value="linux">Linux account</option></select></label>
          <label><span>Change it every (days; 0 = only when asked)</span><input type="number" min={0} max={365} value={f.rotateDays} onChange={(e) => setF({ ...f, rotateDays: e.target.value })} /></label>
          <label className="check"><input type="checkbox" checked={f.resetAfterUse} onChange={(e) => setF({ ...f, resetAfterUse: e.target.checked })} /> Change it after each use (an hour after someone reads it, or when an approved request ends)</label>
          <label className="check"><input type="checkbox" checked={f.rotateNow} onChange={(e) => setF({ ...f, rotateNow: e.target.checked })} /> Change it now</label>
          {err && <p className="err" role="alert">{err}</p>}
          <div className="tools"><button className="primary">Start managing</button><button type="button" onClick={() => setForm(false)}>Cancel</button></div>
        </form>}
    </section>;
  }
  const stateWords: Record<string, string> = { ok: 'Up to date', rotating: 'Changing now…', failed: 'Last change failed', unknown: 'Unsure: the agent did not report back' };
  return <section className="managed-box">
    <h3>Managed by DeskAsk</h3>
    <Loading loading={m.loading && !m.data} error={m.error} />
    {acc && <>
      <dl className="kv">
        <dt>Account</dt><dd>{String(acc.account)} <span className="muted">({acc.target === 'domain' ? 'Active Directory' : acc.target === 'linux' ? 'Linux' : 'Windows local'}) on <a href={`#/assets/${acc.assetId}`}>{String(acc.asset || 'asset ' + acc.assetId)}</a></span></dd>
        <dt>Status</dt><dd><span className={'badge ' + (acc.state === 'ok' ? 'ok' : acc.state === 'rotating' ? '' : 'bad')}>{stateWords[acc.state] ?? acc.state}</span>{acc.lastError ? <div className="muted small">{String(acc.lastError)}</div> : null}</dd>
        <dt>Last changed</dt><dd>{acc.lastRotatedAt ? ago(acc.lastRotatedAt) : 'Not by DeskAsk yet'}</dd>
        <dt>Next change</dt><dd>{acc.nextAt ? fmtDate(acc.nextAt) : 'Only when asked'}{acc.resetAfterUse ? ' · and after each use' : ''}</dd>
        <dt>Password</dt><dd>{secret ? <><Secret itemId={item.id} field="managed password" value={String(secret.password)} />{secret.possiblePassword ? <div className="muted small">Or possibly: <Secret itemId={item.id} field="possible password" value={String(secret.possiblePassword)} /></div> : null}</>
          : <button onClick={() => void run(async () => setSecret(await api<Rec>(`/api/vault/items/${item.id}/secret`)))}>Show password</button>}</dd>
      </dl>
      {item.canEdit && <div className="tools">
        <button disabled={acc.state === 'rotating'} onClick={() => void run(async () => { await api(`/api/vault/items/${item.id}/rotate`, { method: 'POST', body: {} }); setSecret(null); m.reload(); })}>Change the password now</button>
        <button onClick={() => void run(async () => {
          if (!window.confirm('Stop managing this account? The current password goes back into the encrypted item and the server forgets it.')) return;
          const r = await api<Rec>(`/api/vault/items/${item.id}/managed`, { method: 'DELETE' });
          const fresh = await api<S.Item>(`/api/vault/items/${item.id}`);
          const enc = await S.encItem(item.vaultId, await S.title(item), { ...((await S.data(fresh)) ?? {}), password: String(r.password) });
          await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: fresh.revision, passwordChanged: true } });
          onChange();
        })}>Stop managing</button>
      </div>}
      {((m.data?.history ?? []) as Rec[]).length > 0 && <details><summary>Earlier passwords ({(m.data!.history as Rec[]).length})</summary><ul className="plain">{(m.data!.history as Rec[]).map((h) => <li key={h.id}>{fmtDate(h.at)}: {h.note === 'maybe' ? 'possibly set (the agent did not report)' : 'replaced'}</li>)}</ul></details>}
    </>}
    {err && <p className="err" role="alert">{err}</p>}
  </section>;
}

function RequestAccess({ item, onDone }: { item: S.Item; onDone: () => void }) {
  const [reason, setReason] = useState(''); const [ticket, setTicket] = useState(''); const [minutes, setMinutes] = useState(60); const [err, setErr] = useState(''); const [sent, setSent] = useState(false);
  const max = Number(S.current().me?.settings?.requestMaxMinutes ?? 480);
  if (sent) return <p className="note">Request sent. You get an email when it is answered; approved items appear under Requests.</p>;
  return <form className="form" onSubmit={async (e) => { e.preventDefault(); setErr(''); try { await api(`/api/vault/items/${item.id}/requests`, { method: 'POST', body: { reason, ticket, minutes } }); setSent(true); onDone(); } catch (x) { setErr((x as Error).message); } }}>
    <p>{item.requiresApproval ? 'This item needs approval before it is opened.' : 'You may see that this item exists, but not open it.'} Ask the vault's editors for access.</p>
    <label><span>Why do you need it?</span><input value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Replacing the firewall's certificate tonight" /></label>
    <label><span>Ticket (optional)</span><input value={ticket} onChange={(e) => setTicket(e.target.value)} placeholder="SD-1042" /></label>
    <label><span>For how long</span><select value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}>{[15, 30, 60, 120, 240, 480, 1440].filter((m) => m <= max).map((m) => <option key={m} value={m}>{m < 60 ? `${m} minutes` : `${m / 60} hour${m > 60 ? 's' : ''}`}</option>)}</select></label>
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={reason.trim().length < 3}>Ask for access</button></div>
  </form>;
}

function History({ item }: { item: S.Item }) {
  const h = useLoad<Rec>(`/api/vault/items/${item.id}/history`);
  const [open, setOpen] = useState<Record<number, S.Data>>({});
  const list = (h.data?.items ?? []) as Rec[];
  if (!list.length) return <Empty>No earlier versions.</Empty>;
  return <ul className="plain">{list.map((v) => <li key={v.revision}>Version {v.revision} by {v.changedBy || 'someone'}, {fmtDate(v.at)}{' '}
    {open[v.revision] ? <Fields type={item.type} data={open[v.revision]} itemId={item.id} /> : <button className="small" onClick={async () => { const k = S.vaultKeys(item.vaultId)?.item; if (k) setOpen({ ...open, [v.revision]: await C.decryptJson<S.Data>(k, v.dataEnc) }); }}>Show</button>}</li>)}</ul>;
}

function Activity({ item }: { item: S.Item }) {
  const e = useLoad<Rec>(`/api/vault/items/${item.id}/events`);
  const words: Record<string, string> = { created: 'created it', changed: 'changed it', revealed: 'showed', copied: 'copied', opened: 'opened', viewed: 'viewed', requested: 'asked for access', approved: 'approved access', denied: 'denied access', managed: 'started managing it', unmanaged: 'stopped managing it', rotated: 'changed the password', rotation_failed: 'password change failed', rotation_started: 'started a password change', deleted: 'deleted it', restored: 'restored it', moved_in: 'moved it here', exported: 'exported it' };
  const list = (e.data?.items ?? []) as Rec[];
  return list.length ? <ul className="plain log">{list.map((x) => <li key={x.id}><time>{fmtDate(x.at)}</time> {x.user} {words[x.action] ?? x.action}{x.detail ? <span className="muted"> · {x.detail}</span> : null}</li>)}</ul> : <Empty>Nothing yet.</Empty>;
}

/** One item, decrypted. */
function ItemView({ item, onChange, onEdit, folders }: { item: S.Item; onChange: () => void; onEdit: (d: S.Data) => void; folders: Rec[] }) {
  const [data, setData] = useState<S.Data | null>(null); const [t, setT] = useState(''); const [tab, setTab] = useState<'fields' | 'history' | 'activity'>('fields');
  const [err, setErr] = useState(''); const [moving, setMoving] = useState(false);
  useEffect(() => { let live = true; setData(null); setErr(''); void (async () => { setT(await S.title(item)); try { const d = await S.data(item); if (live) { setData(d); if (d) S.note(item.id, 'viewed'); } } catch (e) { if (live) setErr((e as Error).message); } })(); return () => { live = false; }; }, [item.id, item.revision, item.dataEnc]); // eslint-disable-line react-hooks/exhaustive-deps
  const v = S.vaultById(item.vaultId);
  const folder = folders.find((f) => f.id === item.folderId);
  return <article className="item-view">
    <header><span className="type-chip">{typeDef(item.type).label}</span><h2>{t}</h2>
      <p className="muted small">{v?.name}{folder ? ' › ' + folder.name : ''} · changed {ago(item.updatedAt)} by {item.updatedBy || '–'}{item.pwChangedAt ? ` · password set ${ago(item.pwChangedAt)}` : ''}</p></header>
    {err && <p className="err" role="alert">{err}</p>}
    {!item.canRead ? <RequestAccess item={item} onDone={onChange} /> : !data ? <p className="muted">{S.vaultKeys(item.vaultId)?.item ? 'Decrypting…' : 'You do not hold this vault\'s key yet. A manager of the vault gives it to you when they next open the Vault.'}</p> : <>
      <nav className="tabs" aria-label="Item"><button className={tab === 'fields' ? 'on' : ''} onClick={() => setTab('fields')}>Details</button><button className={tab === 'history' ? 'on' : ''} onClick={() => setTab('history')}>Earlier versions</button>{item.canEdit && <button className={tab === 'activity' ? 'on' : ''} onClick={() => setTab('activity')}>Who used it</button>}</nav>
      {tab === 'fields' && <>
        <Fields type={item.type} data={data} itemId={item.id} />
        <Managed item={item} data={data} onChange={onChange} />
        {item.assetId ? <p className="muted small">Linked to <a href={`#/assets/${item.assetId}`}>an asset</a>.</p> : null}
        {item.requiresApproval && <p className="muted small">Viewers of this vault must ask before opening it.</p>}
        {item.canEdit && <div className="tools">
          <button className="primary" onClick={() => onEdit(data)}>Edit</button>
          <button onClick={() => setMoving(true)}>Move to another vault</button>
          <button className="danger" onClick={async () => { if (!window.confirm('Move this item to the trash? It can be restored for 30 days.')) return; try { await api(`/api/vault/items/${item.id}`, { method: 'DELETE' }); onChange(); } catch (e) { setErr((e as Error).message); } }}>Delete</button>
        </div>}
      </>}
      {tab === 'history' && <History item={item} />}
      {tab === 'activity' && <Activity item={item} />}
    </>}
    {moving && data && <Modal title="Move to another vault" onClose={() => setMoving(false)}><MoveForm item={item} data={data} title={t} onDone={() => { setMoving(false); onChange(); }} /></Modal>}
  </article>;
}

function MoveForm({ item, data, title, onDone }: { item: S.Item; data: S.Data; title: string; onDone: () => void }) {
  const targets = S.current().vaults.filter((v) => v.id !== item.vaultId && S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item);
  const [to, setTo] = useState(targets[0]?.id ?? 0); const [err, setErr] = useState('');
  return <form className="form" onSubmit={async (e) => { e.preventDefault(); setErr(''); try { const enc = await S.encItem(to, title, data); await api(`/api/vault/items/${item.id}/move`, { method: 'POST', body: { ...enc, vaultId: to } }); onDone(); } catch (x) { setErr((x as Error).message); } }}>
    {targets.length === 0 ? <Empty>There is no other vault you can add items to.</Empty> : <label><span>Vault</span><select value={to} onChange={(e) => setTo(Number(e.target.value))}>{targets.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}</select></label>}
    <p className="muted small">Earlier versions stay behind; the item is encrypted again with the other vault's key. Open requests for it are cancelled.</p>
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={!targets.length}>Move</button></div>
  </form>;
}

function AssetPick({ value, companyId, onChange }: { value: number | null; companyId?: number | null; onChange: (id: number | null) => void }) {
  const [q, setQ] = useState('');
  const list = useLoad<Rec>(`/api/assets?take=20&search=${encodeURIComponent(q)}${companyId ? `&company=${companyId}` : ''}`, [q, companyId]);
  const cur = useLoad<Rec>(value ? `/api/assets/${value}` : null, [value]);
  const items = (list.data?.items ?? []) as Rec[];
  return <div className="asset-pick">
    {value ? <p>Linked to <strong>{String(cur.data?.name ?? cur.data?.asset?.name ?? 'asset ' + value)}</strong> <button type="button" className="small" onClick={() => onChange(null)}>Remove the link</button></p> : null}
    <input placeholder="Search assets by name or tag" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Find an asset" />
    {q && <ul className="plain picks">{items.slice(0, 8).map((a) => <li key={a.id}><button type="button" className="link" onClick={() => { onChange(Number(a.id)); setQ(''); }}>{a.name} <span className="muted">{a.tag}</span></button></li>)}{items.length === 0 && <li className="muted">No asset found.</li>}</ul>}
  </div>;
}

/** Adding or changing an item; everything is encrypted here before it is saved. */
export function ItemForm({ item, data, onDone, defaults }: { item?: S.Item; data?: S.Data; onDone: (id?: number) => void; defaults?: { vaultId?: number; assetId?: number; type?: string } }) {
  const writable = S.current().vaults.filter((v) => S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item);
  const [vaultId, setVaultId] = useState(item?.vaultId ?? defaults?.vaultId ?? writable.find((v) => v.kind !== 'personal')?.id ?? writable[0]?.id ?? 0);
  const [type, setType] = useState(item?.type ?? defaults?.type ?? 'login');
  const [title, setTitle] = useState(''); const [d, setD] = useState<S.Data>(data ? { ...data } : {});
  const [custom, setCustom] = useState<Custom[]>(data ? customOf(data) : []);
  const [assetId, setAssetId] = useState<number | null>(item?.assetId ?? defaults?.assetId ?? null);
  const [folderId, setFolderId] = useState<number | null>(item?.folderId ?? null);
  const [approval, setApproval] = useState(!!item?.requiresApproval);
  const [gen, setGen] = useState<string | null>(null); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const folders = useLoad<Rec>(vaultId ? `/api/vault/vaults/${vaultId}/folders` : null, [vaultId]);
  const [folderNames, setFolderNames] = useState<Rec[]>([]);
  useEffect(() => { void (async () => { const o = []; for (const f of (folders.data?.items ?? []) as Rec[]) o.push({ id: f.id, name: await S.folderName(vaultId, f.nameEnc) }); setFolderNames(o); })(); }, [folders.data, vaultId]);
  useEffect(() => { if (item) void S.title(item).then(setTitle); }, [item]);
  const v = S.vaultById(vaultId); const t = typeDef(type);
  const set = (k: string, x: string) => setD({ ...d, [k]: x });
  const save = async () => {
    setBusy(true); setErr('');
    try {
      if (!title.trim()) throw new Error('Give the item a name.');
      const body: S.Data = {}; for (const f of t.fields) if (d[f.k]) body[f.k] = d[f.k];
      const cs = custom.filter((c) => c.name.trim()); if (cs.length) body.custom = JSON.stringify(cs);
      const enc = await S.encItem(vaultId, title.trim(), body);
      const pwChanged = !!item && (data?.password ?? '') !== (body.password ?? '');
      if (item) { await api(`/api/vault/items/${item.id}`, { method: 'PUT', body: { ...enc, revision: item.revision, assetId, folderId, requiresApproval: approval, passwordChanged: pwChanged } }); onDone(item.id); }
      else { const r = await api<Rec>(`/api/vault/vaults/${vaultId}/items`, { method: 'POST', body: { ...enc, type, assetId, folderId, requiresApproval: approval, hasPassword: !!body.password } }); onDone(Number(r.id)); }
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  if (writable.length === 0) return <Empty>You cannot add items to any vault yet. Ask an administrator to create a vault and add you, or create your personal vault under Vaults.</Empty>;
  return <form className="form item-form" onSubmit={(e) => { e.preventDefault(); void save(); }}>
    {!item && <div className="row">
      <label><span>Vault</span><select value={vaultId} onChange={(e) => { setVaultId(Number(e.target.value)); setFolderId(null); }}>{writable.map((x) => <option key={x.id} value={x.id}>{x.name}{x.kind === 'personal' ? ' (only you)' : ''}</option>)}</select></label>
      <label><span>Kind</span><select value={type} onChange={(e) => setType(e.target.value)}>{TYPES.map((x) => <option key={x.key} value={x.key}>{x.label}</option>)}</select></label>
    </div>}
    <label><span>Name</span><input value={title} onChange={(e) => setTitle(e.target.value)} placeholder={type === 'server' ? 'SQL01 local administrator' : type === 'wifi' ? 'Office Wi-Fi' : 'Microsoft 365 admin'} autoFocus /></label>
    {t.fields.map((f) => <label key={f.k}><span>{f.label}</span>
      {f.kind === 'select' ? <select value={d[f.k] ?? ''} onChange={(e) => set(f.k, e.target.value)}><option value="">–</option>{f.options!.map((o) => <option key={o}>{o}</option>)}</select>
        : f.multiline ? <textarea rows={f.k === 'privateKey' ? 6 : 3} value={d[f.k] ?? ''} onChange={(e) => set(f.k, e.target.value)} spellCheck={false} />
        : <span className="with-btn"><input type={f.secret && f.kind !== 'totp' ? 'password' : 'text'} autoComplete="off" spellCheck={false} value={d[f.k] ?? ''} placeholder={f.placeholder} onChange={(e) => set(f.k, e.target.value)} />
          {f.kind === 'password' && <button type="button" onClick={() => setGen(f.k)}>Generate</button>}</span>}
      {f.kind === 'password' && d[f.k] ? <Strength pw={d[f.k]} /> : null}
    </label>)}
    {item?.managed && <p className="muted small">The password of this managed account is kept by DeskAsk and not in this form.</p>}
    <fieldset className="custom"><legend>More fields</legend>
      {custom.map((c, i) => <div key={i} className="row">
        <input aria-label="Field name" placeholder="Name" value={c.name} onChange={(e) => setCustom(custom.map((x, j) => j === i ? { ...x, name: e.target.value } : x))} />
        <input aria-label="Value" type={c.hidden ? 'password' : 'text'} placeholder="Value" value={c.value} onChange={(e) => setCustom(custom.map((x, j) => j === i ? { ...x, value: e.target.value } : x))} />
        <label className="check"><input type="checkbox" checked={c.hidden} onChange={(e) => setCustom(custom.map((x, j) => j === i ? { ...x, hidden: e.target.checked } : x))} /> Hidden</label>
        <button type="button" onClick={() => setCustom(custom.filter((_, j) => j !== i))}>Remove</button></div>)}
      <button type="button" onClick={() => setCustom([...custom, { name: '', value: '', hidden: false }])}>Add a field</button>
    </fieldset>
    {folderNames.length > 0 && <label><span>Folder</span><select value={folderId ?? ''} onChange={(e) => setFolderId(e.target.value ? Number(e.target.value) : null)}><option value="">No folder</option>{folderNames.map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}</select></label>}
    {v?.kind !== 'personal' && <>
      <label><span>Asset</span></label><AssetPick value={assetId} companyId={v?.kind === 'customer' ? (v.companyId as number) : null} onChange={setAssetId} />
      <label className="check"><input type="checkbox" checked={approval} onChange={(e) => setApproval(e.target.checked)} /> Viewers must ask before opening it (editors and managers open it directly)</label>
    </>}
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={busy}>{busy ? 'Saving…' : item ? 'Save changes' : 'Add item'}</button><button type="button" onClick={() => onDone()}>Cancel</button></div>
    {gen && <Modal title="Generate a password" onClose={() => setGen(null)}><Generator onUse={(p) => { set(gen, p); setGen(null); }} /></Modal>}
  </form>;
}

/** All items: search and filter on the left, the chosen item on the right. */
export default function Items() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const session = S.useSession();
  const [vault, setVault] = useState(qs.get('vault') ?? ''); const [type, setType] = useState(''); const [q, setQ] = useState(''); const [folder, setFolder] = useState('');
  const asset = qs.get('asset'); const trash = qs.get('trash') === '1';
  const list = useLoad<Rec>(`/api/vault/items${trash ? '?trash=true' : ''}`, [trash]);
  const assetItems = useLoad<Rec>(asset ? `/api/vault/assets/${asset}/items` : null, [asset]);
  const all = useMemo(() => (((asset ? assetItems.data : list.data)?.items ?? []) as S.Item[]), [list.data, assetItems.data, asset]);
  const titles = S.useTitles(all);
  const [sel, setSel] = useState<number | null>(qs.get('item') ? Number(qs.get('item')) : null);
  const [edit, setEdit] = useState<{ item?: S.Item; data?: S.Data } | null>(null);
  const [folders, setFolders] = useState<Rec[]>([]);
  useEffect(() => { void (async () => {
    const o: Rec[] = [];
    for (const v of session.vaults) if (S.rank(v.role) >= 2) for (const f of ((await api<Rec>(`/api/vault/vaults/${v.id}/folders`)).items as Rec[])) o.push({ id: f.id, vaultId: v.id, name: await S.folderName(v.id, f.nameEnc) });
    setFolders(o);
  })().catch(() => undefined); }, [session.vaults]);
  const shown = all.filter((i) => (!vault || String(i.vaultId) === vault) && (!type || i.type === type) && (!folder || String(i.folderId) === folder)
    && (!q || (titles[i.id] ?? '').toLowerCase().includes(q.toLowerCase())))
    .sort((a, b) => (titles[a.id] ?? '').localeCompare(titles[b.id] ?? ''));
  const current = all.find((i) => i.id === sel) ?? null;
  const reload = () => { list.reload(); assetItems.reload(); };
  const canAdd = session.vaults.some((v) => S.rank(v.role) >= 3);
  return <>
    <div className="dash-head"><h1>{trash ? 'Trash' : asset ? 'Items for this asset' : 'All items'}</h1>
      <div className="tools">{canAdd && !trash && <button className="primary" onClick={() => setEdit({})}>Add item</button>}
        <a href={trash ? '#/vault' : '#/vault?trash=1'}>{trash ? 'Back to the items' : 'Trash'}</a></div></div>
    {asset && <div className="filter-chips"><a href="#/vault">This asset only ✕</a></div>}
    <div className="tools">
      <input type="search" placeholder="Search by name" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search items" />
      <select value={vault} onChange={(e) => { setVault(e.target.value); setFolder(''); }} aria-label="Vault"><option value="">Every vault</option>{session.vaults.map((v) => <option key={v.id} value={v.id}>{v.name}{v.kind === 'personal' ? '' : ` (${KIND_WORDS[v.kind]})`}</option>)}</select>
      {vault && folders.some((f) => String(f.vaultId) === vault) && <select value={folder} onChange={(e) => setFolder(e.target.value)} aria-label="Folder"><option value="">Every folder</option>{folders.filter((f) => String(f.vaultId) === vault).map((f) => <option key={f.id} value={f.id}>{f.name}</option>)}</select>}
      <select value={type} onChange={(e) => setType(e.target.value)} aria-label="Kind"><option value="">Every kind</option>{TYPES.map((t) => <option key={t.key} value={t.key}>{t.plural}</option>)}</select>
    </div>
    <Loading loading={list.loading && !list.data} error={list.error} />
    <div className="vault-split">
      <ul className="item-list" aria-label="Items">
        {shown.map((i) => <li key={i.id}><button className={sel === i.id ? 'on' : ''} onClick={() => setSel(i.id)}>
          <span className={'type-dot t-' + i.type} aria-hidden="true">{GLYPH[i.type] ?? '•'}</span>
          <span className="name">{titles[i.id] ?? '…'}<small>{S.vaultById(i.vaultId)?.name}{!i.canRead ? ' · ask to open' : ''}{i.managed ? ' · managed' : ''}</small></span></button></li>)}
        {list.data && shown.length === 0 && <li className="muted pad">{all.length ? 'Nothing matches.' : trash ? 'The trash is empty.' : 'No items yet.'}</li>}
      </ul>
      <div className="item-pane">
        {trash && current ? <article className="item-view"><h2>{titles[current.id]}</h2><p className="muted">Deleted {ago(current.deletedAt)}. Items stay in the trash for 30 days.</p>
          <div className="tools"><button className="primary" onClick={async () => { await api(`/api/vault/items/${current.id}/restore`, { method: 'POST', body: {} }); reload(); setSel(null); }}>Restore</button>
            <button className="danger" onClick={async () => { if (window.confirm('Delete for good? This cannot be undone.')) { await api(`/api/vault/items/${current.id}?purge=true`, { method: 'DELETE' }); reload(); setSel(null); } }}>Delete for good</button></div></article>
          : current ? <ItemView key={current.id} item={current} folders={folders} onChange={reload} onEdit={(d) => setEdit({ item: current, data: d })} />
          : <p className="muted pad">Choose an item to see it.</p>}
      </div>
    </div>
    {edit && <Modal title={edit.item ? 'Change item' : 'Add an item'} onClose={() => setEdit(null)}><ItemForm item={edit.item} data={edit.data} defaults={{ vaultId: vault ? Number(vault) : undefined, assetId: asset ? Number(asset) : undefined }} onDone={(id) => { setEdit(null); reload(); if (id) setSel(id); }} /></Modal>}
  </>;
}
