import PersonInput from './PersonInput';
import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { usePerms } from '../perms';
import { Act, Badge, Card, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';
import { PurchaseFields, purchaseBody } from './Purchase';

/** The extra fields a kind of asset defines (IMEI and phone number for a phone, the screen size of a monitor). */
export function CustomFields({ fields, value, onChange }: { fields: Rec[]; value: Rec; onChange: (v: Rec) => void }) {
  if (fields.length === 0) return null;
  const set = (k: string, x: unknown) => { const n = { ...value }; if (x === '' || x === undefined || x === null) delete n[k]; else n[k] = x; onChange(n); };
  return <fieldset className="custom-fields"><legend>Details of this kind</legend><div className="row">{fields.map((f) => <Field key={f.key} label={f.label}>
    {f.kind === 'bool' ? <input type="checkbox" checked={value[f.key] === true} onChange={(e) => set(f.key, e.target.checked ? true : undefined)} />
      : f.kind === 'choice' ? <select value={value[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)}><option value="">(not set)</option>{((f.options ?? []) as string[]).map((o) => <option key={o}>{o}</option>)}</select>
        : f.kind === 'number' ? <input type="number" value={value[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value === '' ? undefined : Number(e.target.value))} />
          : <input type={f.kind === 'date' ? 'date' : 'text'} value={value[f.key] ?? ''} onChange={(e) => set(f.key, e.target.value)} />}
  </Field>)}</div></fieldset>;
}

/** Choose the main device (the desktop or laptop) an accessory belongs to, by its serial number, tag or name. */
export function MainDevicePicker({ value, onChange, companyId, selfId }: { value: Rec | null; onChange: (d: Rec | null) => void; companyId?: number | null; selfId?: number }) {
  const [q, setQ] = useState('');
  const found = useLoad<Rec>(q.trim().length >= 2 ? `/api/assets?search=${encodeURIComponent(q.trim())}&take=8${companyId ? `&company=${companyId}` : ''}` : null, [q, companyId]);
  const items = ((found.data?.items ?? []) as Rec[]).filter((a) => a.id !== selfId);
  if (value) return <div className="attached-to">Attached to <b>{value.name}</b> <span className="mono muted">{value.tag}</span>{value.serial ? <> · serial <b className="mono">{value.serial}</b></> : null}{' '}
    <button type="button" onClick={() => onChange(null)} aria-label="Detach from the main device">✕</button></div>;
  return <div><Field label="Attached to (main device: search by its serial number, tag or name)"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Serial number of the desktop or laptop" /></Field>
    {items.length > 0 && <ul className="pick">{items.map((a) => <li key={a.id}><button type="button" onClick={() => { onChange(a); setQ(''); }}>{a.tag} · {a.name}{a.serial ? ` · ${a.serial}` : ''}{a.assignedTo ? ` · ${a.assignedTo}` : ''}</button></li>)}</ul>}</div>;
}

/** On an asset's page: the main device it is attached to, or the accessories attached to it, with their serial numbers. */
export function AttachedCard({ id, d, data, reload, canEdit }: { id: string; d: Rec; data: Rec | null; reload: () => void; canEdit: boolean }) {
  const [mode, setMode] = useState<'' | 'attach' | 'add'>(''); const [err, setErr] = useState('');
  if (!data) return null;
  if (!data.parent && (data.items ?? []).length === 0 && !canEdit) return null;
  const parent = data.parent as Rec | null; const kids = (data.items ?? []) as Rec[];
  const put = async (parentId: number | null) => { setErr(''); try { await api(`/api/assets/${id}/attach`, { method: 'PUT', body: { parentId } }); setMode(''); reload(); } catch (e) { setErr((e as Error).message); } };
  return <Card title="Attached items">
    {parent && <p>Attached to <a href={`#/assets/${parent.id}`}>{parent.name}</a> <span className="muted mono">{parent.tag}</span>{parent.serial ? <> · serial <b className="mono">{parent.serial}</b></> : null}{parent.assignedTo ? <> · {parent.assignedTo}</> : null}
      {canEdit && <> <button onClick={() => void put(null)}>Detach</button></>}</p>}
    {kids.length > 0 ? <table><thead><tr><th>Kind</th><th>Name</th><th>Serial number</th><th>Status</th><th>Warranty ends</th></tr></thead><tbody>
      {kids.map((k) => <tr key={k.id}><td>{k.type}</td><td><a href={`#/assets/${k.id}`}>{k.name}</a> <span className="muted mono">{k.tag}</span></td><td className="mono">{k.serial || '–'}</td><td>{String(k.status).replace('_', ' ')}</td><td>{k.warrantyEnd ? fmtDay(k.warrantyEnd) : ''}</td></tr>)}</tbody></table>
      : !parent && <Empty>Nothing is attached. Attach the monitors, keyboard, mouse and docking station to the desktop or laptop they came with, so support can find them by its serial number.</Empty>}
    <Err text={err} />
    {canEdit && <div className="tools">{!parent && <button onClick={() => setMode('attach')}>Attach to a main device…</button>}{!parent && <button onClick={() => setMode('add')}>Add accessories…</button>}</div>}
    {mode === 'attach' && <Modal title="Attach to a main device" onClose={() => setMode('')}><div className="form">
      <MainDevicePicker value={null} onChange={(m) => { if (m) void put(Number(m.id)); }} companyId={d.companyId} selfId={Number(id)} />
      <p className="muted small">Purchase, warranty, site and holder that this asset does not have yet are copied from the main device.</p><Err text={err} />
      <div className="actions"><button onClick={() => setMode('')}>Cancel</button></div></div></Modal>}
    {mode === 'add' && <AddSeveral parent={{ id: Number(id), tag: d.tag, name: d.name, serial: d.identity?.serial ?? '' }} companyId={d.companyId} onClose={(ch) => { setMode(''); if (ch) reload(); }} />}
  </Card>;
}

/** Add several of one kind in one go: a list of serial numbers, or a quantity; attached to a main device if wanted. */
export function AddSeveral({ parent, companyId, onClose }: { parent?: Rec | null; companyId?: number | null; onClose: (changed: boolean) => void }) {
  const types = useLoad<Rec[]>('/api/assets/types'); const companies = useLoad<Rec[]>('/api/assets/companies');
  const tl = Array.isArray(types.data) ? types.data : []; const cos = Array.isArray(companies.data) ? companies.data : [];
  const [v, setV] = useState<Rec>({ typeId: '', name: '', manufacturer: '', model: '', assignedTo: '', serials: '', quantity: 1, companyId: companyId ?? null });
  const [main, setMain] = useState<Rec | null>(parent ?? null); const [res, setRes] = useState<Rec | null>(null);
  const f = useSubmit(() => undefined); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const co = (main ? (parent ? companyId : v.companyId) : (v.companyId ?? (cos.length === 1 ? cos[0].id : null))) as number | null | undefined;
  const serials = String(v.serials).split(/[\n,;\t]+/).map((s) => s.trim()).filter(Boolean);
  const go = () => f.submit(async () => {
    const body: Rec = { typeId: Number(v.typeId), companyId: co ?? undefined, parentId: main ? Number(main.id) : undefined, name: v.name, manufacturer: v.manufacturer, model: v.model, assignedTo: v.assignedTo, ...purchaseBody(v) };
    if (serials.length) body.items = serials.map((serial) => ({ serial })); else body.quantity = Math.max(1, Number(v.quantity) || 1);
    setRes(await api<Rec>('/api/assets/bulk-add', { body }));
  });
  if (res) return <Modal title="Added" onClose={() => onClose(true)}><div className="form">
    <p role="status"><b>{(res.created ?? []).length}</b> added{(res.skipped ?? []).length > 0 ? `, ${res.skipped.length} not added` : ''}.</p>
    {(res.created as Rec[]).length > 0 && <table><thead><tr><th>Tag</th><th>Name</th><th>Serial number</th></tr></thead><tbody>{(res.created as Rec[]).map((c) => <tr key={c.id}><td className="mono"><a href={`#/assets/${c.id}`}>{c.tag}</a></td><td>{c.name}</td><td className="mono">{c.serial}</td></tr>)}</tbody></table>}
    {(res.skipped as Rec[]).length > 0 && <><h4>Not added</h4><ul>{(res.skipped as Rec[]).map((s, i) => <li key={i}><span className="mono">{s.serial || s.name}</span>: {s.reason}</li>)}</ul></>}
    <div className="actions"><button className="primary" onClick={() => onClose(true)}>Close</button></div></div></Modal>;
  return <Modal title="Add several" onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="Kind"><select value={v.typeId} onChange={(e) => set('typeId', e.target.value)}><option value="">(choose)</option>{tl.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</select></Field>
      {!parent && <Field label="Customer"><select value={co ?? ''} onChange={(e) => set('companyId', e.target.value ? Number(e.target.value) : null)}>{cos.length !== 1 && <option value="">(own organisation)</option>}{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>}</div>
    <MainDevicePicker value={main} onChange={setMain} companyId={co} />
    <Field label="Serial numbers, one per line"><textarea rows={4} value={v.serials} onChange={(e) => set('serials', e.target.value)} placeholder={'CN12345678\nCN12345679'} /></Field>
    {serials.length === 0 ? <Field label="Or how many, without serial numbers"><input type="number" min={1} max={200} value={v.quantity} onChange={(e) => set('quantity', e.target.value)} /></Field> : <p className="muted small">{serials.length} to add.</p>}
    <div className="row"><Field label="Name (optional)"><input value={v.name} onChange={(e) => set('name', e.target.value)} placeholder="Dell mouse" /></Field><Field label="Manufacturer"><input value={v.manufacturer} onChange={(e) => set('manufacturer', e.target.value)} /></Field><Field label="Model"><input value={v.model} onChange={(e) => set('model', e.target.value)} /></Field></div>
    <Field label="Assigned to (name or email)"><input value={v.assignedTo} onChange={(e) => set('assignedTo', e.target.value)} /></Field>
    {main && <p className="muted small">Purchase, warranty, vendor, site and holder you leave empty are copied from the main device.</p>}
    <PurchaseFields v={v} set={(p) => setV({ ...v, ...p })} companyId={co ?? null} />
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !v.typeId} onClick={() => void go()}>Add</button></div></div></Modal>;
}

const esc = (s: unknown) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** A sheet to sign when assets are handed over: what the person holds, with serial numbers. */
export function printSheet(holder: string, company: string, items: Rec[]) {
  const w = window.open('', '_blank'); if (!w) return;
  const rows = items.map((a) => `<tr><td>${esc(a.type)}</td><td>${esc(a.tag)}</td><td>${esc(a.name)}</td><td>${esc([a.manufacturer, a.model].filter(Boolean).join(' '))}</td><td>${esc(a.serial)}</td><td>${esc(a.parentTag)}</td></tr>`).join('');
  w.document.write(`<!doctype html><title>Handover ${esc(holder)}</title><style>body{font:14px sans-serif;margin:32px}table{border-collapse:collapse;width:100%}td,th{border:1px solid #888;padding:6px;text-align:left}.sig{margin-top:60px;display:flex;gap:48px}.sig div{flex:1;border-top:1px solid #000;padding-top:4px}</style>`
    + `<h1>Asset handover</h1><p><b>${esc(holder)}</b>${company ? ' · ' + esc(company) : ''} · ${esc(new Date().toLocaleDateString())}</p>`
    + `<table><thead><tr><th>Kind</th><th>Tag</th><th>Name</th><th>Make and model</th><th>Serial number</th><th>Attached to</th></tr></thead><tbody>${rows}</tbody></table>`
    + `<p>I have received the items above in working condition and will return them when asked.</p><div class="sig"><div>Received by (signature, date)</div><div>Handed over by (signature, date)</div></div>`);
  w.document.close(); w.focus(); w.print();
}

/** What each person holds: one list, hand several things back at once, give things from stock, print the sheet. */
export function People() {
  const perms = usePerms('assets');
  const [q, setQ] = useState(''); const [who, setWho] = useState<{ name: string; companyId: number | null; company: string } | null>(null);
  const [newName, setNewName] = useState(''); const [give, setGive] = useState(false);
  const holders = useLoad<Rec>(`/api/assets/holders?search=${encodeURIComponent(q)}`, [q]);
  const directory = useLoad<Rec>(`/api/assets/directory-people?limit=500&search=${encodeURIComponent(q)}`, [q]);
  // people who hold something, then everyone else the customer's domain controller lists (holding nothing yet)
  const held = (holders.data?.items ?? []) as Rec[];
  const key = (n: string, c: number | null) => n.toLowerCase() + '|' + (c ?? '');
  const known = new Set(held.map((h) => key(h.name, h.companyId)));
  const others = ((directory.data?.items ?? []) as Rec[]).filter((p) => !known.has(key(p.name, p.companyId)))
    .map((p) => ({ name: p.name, companyId: p.companyId, company: p.company, assets: 0, accessories: 0, detail: [p.email, p.department].filter(Boolean).join(' · ') }));
  const list = [...held, ...others];
  return <>
    <h1>People</h1>
    <p className="muted">Everything handed to a person: the laptop, the monitors, the phone. Choose a person to see their items, take several back at once or print the sheet they sign.</p>
    <div className="tools"><input placeholder="Search people" value={q} onChange={(e) => setQ(e.target.value)} aria-label="Search people" />
      {perms.admin && <><PersonInput placeholder="Give things to someone new (name or email)" value={newName} onChange={setNewName} label="Someone new" />
        <button disabled={!newName.trim()} onClick={() => { setWho({ name: newName.trim(), companyId: null, company: '' }); setGive(true); }}>Give…</button></>}</div>
    <Loading loading={holders.loading && !holders.data} error={holders.error} />
    {holders.data && (list.length === 0 ? <Empty>No one holds anything yet. Set up the customer's domain controller under Devices setup to list their people here.</Empty> : <table><thead><tr><th>Person</th><th>Customer</th><th className="num">Items</th><th className="num">Accessories</th></tr></thead><tbody>
      {list.map((h, i) => <tr key={i} className={who?.name === h.name && who?.companyId === h.companyId ? 'on' : ''}><td><a href="#/assets/people" onClick={(e) => { e.preventDefault(); setWho({ name: h.name, companyId: h.companyId, company: h.company }); }}>{h.name}</a>{h.detail && <div className="muted">{h.detail}</div>}</td><td>{h.company}</td><td className="num">{h.assets}</td><td className="num">{h.accessories}</td></tr>)}</tbody></table>)}
    {who && <Holder key={who.name + who.companyId} who={who} admin={!!perms.admin} onChanged={holders.reload} onGive={() => setGive(true)} />}
    {give && who && <Give to={who.name} companyId={who.companyId} onClose={(ch) => { setGive(false); if (ch) { holders.reload(); setWho({ ...who }); } }} />}
  </>;
}

function Holder({ who, admin, onChanged, onGive }: { who: { name: string; companyId: number | null; company: string }; admin: boolean; onChanged: () => void; onGive: () => void }) {
  const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/holder?name=${encodeURIComponent(who.name)}${who.companyId ? `&companyId=${who.companyId}` : ''}`, [who.name, who.companyId]);
  const items = (data?.items ?? []) as Rec[]; const [sel, setSel] = useState<Set<number>>(new Set()); const [msg, setMsg] = useState('');
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  const back = async () => {
    setMsg('');
    try { const r = await api<Rec>('/api/assets/holder/handover', { body: { ids: [...sel], mode: 'return', condition: 'good', includeAttached: true } }); setSel(new Set()); setMsg(`${r.done} taken back${(r.skipped ?? []).length ? `, ${r.skipped.length} skipped` : ''}.`); reload(); onChanged(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return <Card title={who.name}>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>{who.name} holds nothing.</Empty> : <table><thead><tr>{admin && <th />}<th>Kind</th><th>Tag</th><th>Name</th><th>Serial number</th><th>Attached to</th><th>Warranty ends</th></tr></thead><tbody>
      {items.map((a) => <tr key={a.id}>{admin && <td><input type="checkbox" aria-label={'Select ' + a.name} checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td>}
        <td>{a.type || '–'} {a.peripheral && <Badge>accessory</Badge>}</td><td className="mono">{a.tag}</td><td><a href={`#/assets/${a.id}`}>{a.name}</a></td><td className="mono">{a.serial || '–'}</td><td className="mono">{a.parentTag}</td><td>{a.warrantyEnd ? fmtDay(a.warrantyEnd) : ''}</td></tr>)}</tbody></table>)}
    <div className="tools">
      {admin && <button onClick={onGive}>Give more…</button>}
      {admin && <Act label={`Take back selected (${sel.size})`} confirm={`Take ${sel.size} item${sel.size === 1 ? '' : 's'} back from ${who.name}? Items attached to a chosen one come with it.`} run={back} />}
      <button disabled={items.length === 0} onClick={() => printSheet(who.name, who.company, items)}>Print handover sheet</button>
      {msg && <span role="status">{msg}</span>}
    </div></Card>;
}

/** Hand things from stock to a person. */
function Give({ to, companyId, onClose }: { to: string; companyId: number | null; onClose: (changed: boolean) => void }) {
  const [q, setQ] = useState(''); const [sel, setSel] = useState<Set<number>>(new Set());
  const found = useLoad<Rec>(`/api/assets?search=${encodeURIComponent(q)}&status=stock&take=40${companyId ? `&company=${companyId}` : ''}`, [q, companyId]);
  const items = ((found.data?.items ?? []) as Rec[]).filter((a) => !a.assignedTo);
  const f = useSubmit(() => onClose(true));
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return <Modal title={`Give to ${to}`} onClose={() => onClose(false)}><div className="form">
    <Field label="Search the stock"><input value={q} onChange={(e) => setQ(e.target.value)} placeholder="Name, tag or serial number" autoFocus /></Field>
    {items.length === 0 ? <Empty>Nothing in stock matches.</Empty> : <table><thead><tr><th /><th>Kind</th><th>Tag</th><th>Name</th><th>Serial number</th></tr></thead><tbody>
      {items.map((a) => <tr key={a.id}><td><input type="checkbox" aria-label={'Select ' + a.name} checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td><td>{a.type}</td><td className="mono">{a.tag}</td><td>{a.name}</td><td className="mono">{a.serial}</td></tr>)}</tbody></table>}
    <p className="muted small">Items attached to a chosen one go with it.</p>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || sel.size === 0} onClick={() => void f.submit(() => api('/api/assets/holder/handover', { body: { ids: [...sel], mode: 'assign', to, condition: 'good', includeAttached: true } }))}>Give {sel.size || ''}</button></div></div></Modal>;
}

/** How many of each kind are in stock, in use or in repair. */
export function Stock() {
  const companies = useLoad<Rec[]>('/api/assets/companies'); const cos = Array.isArray(companies.data) ? companies.data : [];
  const [co, setCo] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/assets/stock${co ? `?companyId=${co}` : ''}`, [co]);
  const rows = (data?.items ?? []) as Rec[];
  return <>
    <h1>Stock</h1>
    <p className="muted">How many of each kind are in stock, handed out or in repair.</p>
    {cos.length > 1 && <div className="tools"><select value={co} onChange={(e) => setCo(e.target.value)} aria-label="Customer"><option value="">All customers</option>{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></div>}
    <Loading loading={loading && !data} error={error} />
    {data && (rows.length === 0 ? <Empty>No assets yet.</Empty> : <table><thead><tr><th>Kind</th><th className="num">In stock</th><th className="num">In use</th><th className="num">In repair</th><th className="num">Retired</th><th className="num">Lost</th><th className="num">Total</th></tr></thead><tbody>
      {rows.map((r) => <tr key={r.type}><td>{r.type} {r.peripheral && <Badge>accessory</Badge>}</td><td className="num">{r.stock}</td><td className="num">{r.inUse}</td><td className="num">{r.repair}</td><td className="num">{r.retired}</td><td className="num">{r.lost}</td><td className="num"><b>{r.total}</b></td></tr>)}</tbody></table>)}
  </>;
}

const KINDS = ['text', 'number', 'date', 'bool', 'choice'];
const slug = (s: string) => { const k = s.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 30); return /^[a-z]/.test(k) ? k : ('f_' + k).slice(0, 30); };

/** The kinds of asset and the extra details each one keeps. */
export function Types() {
  const { data, error, loading, reload } = useLoad<Rec[]>('/api/assets/types');
  const [edit, setEdit] = useState<Rec | 'new' | null>(null);
  const list = Array.isArray(data) ? data : [];
  return <>
    <h1>Kinds of asset</h1>
    <p className="muted">Each kind keeps its own extra details (a phone's IMEI, a monitor's screen size). A kind marked <b>not reached remotely</b> has no agent, shell or remote control and is not watched by monitoring.</p>
    <div className="tools"><button className="primary" onClick={() => setEdit('new')}>New kind</button></div>
    <Loading loading={loading && !data} error={error} />
    {data && <table><thead><tr><th>Kind</th><th>Details</th><th>Not reached remotely</th><th className="num">Assets</th><th /></tr></thead><tbody>
      {list.map((t) => <tr key={t.id}><td>{t.name}</td><td className="muted">{((t.fields ?? []) as Rec[]).map((f) => f.label).join(', ')}</td><td>{t.peripheral ? 'Yes' : ''}</td><td className="num">{t.assets}</td><td><button onClick={() => setEdit(t)}>Change</button></td></tr>)}</tbody></table>}
    {edit && <TypeForm t={edit === 'new' ? null : edit} onClose={(ch) => { setEdit(null); if (ch) reload(); }} />}
  </>;
}

function TypeForm({ t, onClose }: { t: Rec | null; onClose: (changed: boolean) => void }) {
  const [name, setName] = useState<string>(t?.name ?? ''); const [peripheral, setPeripheral] = useState<boolean>(!!t?.peripheral);
  const [text, setText] = useState<string>(((t?.fields ?? []) as Rec[]).map((f) => `${f.label}:${f.kind}${f.kind === 'choice' ? ':' + (f.options ?? []).join('/') : ''}`).join('\n'));
  const f = useSubmit(() => onClose(true));
  const fields = () => {
    const used = new Set<string>(); const old = (t?.fields ?? []) as Rec[];
    return text.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
      const [label, kind = 'text', opts = ''] = l.split(':').map((x) => x.trim());
      if (!KINDS.includes(kind)) throw new Error(`"${l}": the kind must be one of ${KINDS.join(', ')}`);
      let key = old.find((o) => o.label === label)?.key ?? slug(label); let n = 2; while (used.has(key)) key = (slug(label) + '_' + n++).slice(0, 30); used.add(key);
      return { key, label, kind, ...(kind === 'choice' ? { options: opts.split('/').map((x) => x.trim()).filter(Boolean) } : {}) };
    });
  };
  return <Modal title={t ? `Change ${t.name}` : 'New kind of asset'} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={name} onChange={(e) => setName(e.target.value)} autoFocus /></Field>
    <label><input type="checkbox" checked={peripheral} onChange={(e) => setPeripheral(e.target.checked)} /> Not reached remotely (no agent, shell or remote control; not monitored)</label>
    <Field label="Extra details, one per line as  Label:kind  (kinds: text, number, date, bool, choice; a choice lists its options like  OS:choice:Android/iOS)"><textarea rows={6} value={text} onChange={(e) => setText(e.target.value)} placeholder={'IMEI:text\nStorage (GB):number'} spellCheck={false} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !name.trim()} onClick={() => void f.submit(() => api(t ? `/api/assets/types/${t.id}` : '/api/assets/types', { method: t ? 'PUT' : 'POST', body: { name: name.trim(), peripheral, fields: fields() } }))}>Save</button></div></div></Modal>;
}
