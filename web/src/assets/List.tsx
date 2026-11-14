import { useState } from 'react';
import { api, download, go, openPage, saveFile, useLoad, type Rec } from '../http';
import { usePerms } from '../perms';
import { Act, Badge, Empty, Err, Field, Loading, Modal, fmtDay, useSubmit } from '../ui';

const STATUSES = ['stock', 'in_use', 'repair', 'retired', 'lost'];
const label = (s: string) => s.replace('_', ' ');

/** Filters that come from the dashboard (a ring part), with the words shown for them. */
const DRILL: Record<string, Record<string, string>> = {
  compliance: { compliant: 'Compliant', noncompliant: 'Not compliant', nodata: 'No compliance data' },
  warranty: { covered: 'Warranty covered', soon: 'Warranty ends within 30 days', expired: 'Warranty expired', unknown: 'Warranty not recorded' },
  kind: { computer: 'Computers', server: 'Servers and hypervisors', network: 'Network devices', printer: 'Printers', other: 'Other devices' },
  health: { bad: 'Hardware not OK' },
};
const AGENT_WORDS: Record<string, string> = { online: 'Agent live', offline: 'Agent not reporting', update: 'Agent update due', none: 'No agent' };

export default function List() {
  const qs = new URLSearchParams(location.hash.split('?')[1] ?? '');
  const company = qs.get('company') ?? '';
  const drill = Object.keys(DRILL).map((k) => [k, qs.get(k) ?? ''] as const).filter(([k, v]) => v && DRILL[k][v]);
  const perms = usePerms('assets'); const [search, setSearch] = useState(''); const [status, setStatus] = useState(''); const [agent, setAgent] = useState(qs.get('agent') ?? ''); const [skip, setSkip] = useState(0); const take = 50;
  const co = useLoad<Rec>(company ? `/api/assets/dashboard?company=${encodeURIComponent(company)}` : null);
  const coName = company ? String(co.data?.customer?.company ?? (company === 'none' ? 'No customer' : '…')) : '';
  const without = (key: string) => { const n = new URLSearchParams(qs); n.delete(key); const t = n.toString(); return '#/assets/list' + (t ? '?' + t : ''); };
  const [sel, setSel] = useState<Set<number>>(new Set()); const [add, setAdd] = useState(false); const [print, setPrint] = useState(false);
  const filters = `&agent=${agent}` + (company ? `&company=${encodeURIComponent(company)}` : '') + drill.map(([k, v]) => `&${k}=${v}`).join('');
  const exportQs = `search=${encodeURIComponent(search)}&status=${status}${filters}`;
  const q = `/api/assets?search=${encodeURIComponent(search)}&status=${status}${filters}&skip=${skip}&take=${take}`;
  const { data, error, loading, reload } = useLoad<Rec>(q);
  const items = (data?.items ?? []) as Rec[]; const total = (data?.total ?? 0) as number;
  const toggle = (id: number) => setSel((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  return <>
    <nav className="crumbs" aria-label="Breadcrumb"><a href="#/assets">Assets</a><span aria-hidden="true">›</span>
      {company ? <><a href={`#/assets/customer/${company}`}>{coName}</a><span aria-hidden="true">›</span></> : null}<span aria-current="page">{drill.length ? drill.map(([k, v]) => DRILL[k][v]).join(', ') : agent ? AGENT_WORDS[agent] : 'All assets'}</span></nav>
    <h1>{company ? coName : 'All assets'}</h1>
    {(company || drill.length > 0) && <div className="filter-chips">{company && <a href={without('company')} title="Show every customer">{coName} ✕</a>}{drill.map(([k, v]) => <a key={k} href={without(k)} title="Remove this filter">{DRILL[k][v]} ✕</a>)}</div>}
    <div className="tools">
      <input placeholder="Search name, tag, serial, model, person…" value={search} onChange={(e) => { setSearch(e.target.value); setSkip(0); }} style={{ minWidth: 260 }} />
      <select value={status} onChange={(e) => { setStatus(e.target.value); setSkip(0); }} aria-label="Status"><option value="">Any status</option>{STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}</select>
      <select value={agent} onChange={(e) => { setAgent(e.target.value); setSkip(0); }} aria-label="Agent"><option value="">Any agent state</option><option value="online">Agent live, up to date</option><option value="offline">Agent offline</option><option value="update">Agent update due</option><option value="none">No agent</option></select>
      {perms.add && <button className="primary" onClick={() => setAdd(true)}>Add asset</button>}
      <button disabled={sel.size === 0} onClick={() => setPrint(true)}>QR labels ({sel.size})</button>
      <button onClick={() => download('/api/assets/export.csv?' + exportQs, 'assets.csv')}>Export list (CSV)</button>
      <Act label="Export all details (CSV)" run={() => saveFile('/api/assets/export-full.csv?' + exportQs, 'assets-full-details.csv')} />
    </div>
    {perms.admin && sel.size > 0 && <BulkAgents ids={[...sel]} total={total} query={q} onSelect={(ids) => setSel(new Set(ids))} onDone={() => { setSel(new Set()); reload(); }} />}
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No assets match.</Empty> : <table><thead><tr><th><input type="checkbox" aria-label="Select all" checked={items.every((i) => sel.has(i.id))} onChange={(e) => setSel(e.target.checked ? new Set([...sel, ...items.map((i) => i.id)]) : new Set([...sel].filter((x) => !items.some((i) => i.id === x))))} /></th>
      <th>Tag</th><th>Name</th><th>Type</th><th>Agent</th><th>Model</th><th>Assigned to</th><th>Location</th><th>Warranty</th></tr></thead><tbody>
      {items.map((a) => <tr key={a.id}><td><input type="checkbox" aria-label={'Select ' + a.name} checked={sel.has(a.id)} onChange={() => toggle(a.id)} /></td>
        <td className="mono">{a.tag}</td><td><a href={`#/assets/${a.id}`}>{a.name}</a></td><td>{a.type ?? ''}</td><td><AgentCell a={(a.agent ?? { state: 'none' }) as Rec} /></td>
        <td>{[a.manufacturer, a.model].filter(Boolean).join(' ')}</td><td>{a.assignedTo}</td><td>{a.location}</td><td>{fmtDay(a.warrantyEnd)}</td></tr>)}</tbody></table>)}
    <div className="tools" style={{ marginTop: 10 }}><button disabled={skip === 0} onClick={() => setSkip(Math.max(0, skip - take))}>Previous</button><span className="muted">{total ? `${skip + 1}–${Math.min(total, skip + take)} of ${total}` : ''}</span><button disabled={skip + take >= total} onClick={() => setSkip(skip + take)}>Next</button></div>
    {add && <AddAsset onClose={(id) => { setAdd(false); if (id) go(`assets/${id}`); else reload(); }} />}
    {print && <PrintDialog ids={[...sel]} onClose={() => setPrint(false)} />}
  </>;
}

/** Agent actions for the chosen assets: update, move to the recycle bin, uninstall; and choosing every asset that matches the filters. */
function BulkAgents({ ids, total, query, onSelect, onDone }: { ids: number[]; total: number; query: string; onSelect: (ids: number[]) => void; onDone: () => void }) {
  const [msg, setMsg] = useState(''); const [busy, setBusy] = useState(false);
  const run = async (action: 'update' | 'bin' | 'uninstall') => {
    setBusy(true); setMsg('');
    try {
      let done = 0; const skipped: Record<string, number> = {};
      for (let i = 0; i < ids.length; i += 1000) {
        const r = await api<Rec>('/api/assets/agents/bulk', { body: { action, assetIds: ids.slice(i, i + 1000) } });
        done += Number(r.done ?? 0); for (const [k, v] of Object.entries((r.skipped ?? {}) as Record<string, number>)) skipped[k] = (skipped[k] ?? 0) + v;
      }
      const why: Record<string, string> = { no_agent: 'have no agent', up_to_date: 'are up to date', agent_offline: 'are offline', busy: 'are already updating', no_build: 'have no build on the server' };
      const rest = Object.entries(skipped).map(([k, v]) => `${v} ${why[k] ?? k}`).join(', ');
      setMsg(`${action === 'update' ? 'Update asked for' : action === 'bin' ? 'Moved to the recycle bin:' : 'Uninstall sent to'} ${done} agent${done === 1 ? '' : 's'}.${rest ? ' Skipped: ' + rest + '.' : ''}`);
      if (action !== 'update') onDone();
    } catch (e) { setMsg((e as Error).message); } finally { setBusy(false); }
  };
  const all = async () => {
    setBusy(true);
    try {
      const out: number[] = [];
      for (let skip = 0; skip < Math.min(total, 5000); skip += 200) {
        const r = await api<Rec>(query.replace(/&skip=\d+&take=\d+/, '') + `&skip=${skip}&take=200`);
        out.push(...((r.items ?? []) as Rec[]).map((x) => Number(x.id)));
      }
      onSelect(out);
    } catch (e) { setMsg((e as Error).message); } finally { setBusy(false); }
  };
  return <div className="bulk" role="region" aria-label="Agent actions">
    <b>{ids.length} chosen</b>{ids.length < total && <button disabled={busy} onClick={all}>Choose all {Math.min(total, 5000)} matching</button>}
    <button disabled={busy} onClick={() => run('update')}>Update agents</button>
    <Act label="Move agents to bin" confirm={`Move the agents of ${ids.length} assets to the recycle bin? They stop reporting; the assets stay, and you can restore the agents for 30 days.`} run={() => run('bin')} />
    <Act label="Uninstall agents" danger confirm={`Uninstall the agent from ${ids.length} computers? The assets stay.`} run={() => run('uninstall')} />
    {msg && <span role="status">{msg}</span>}
  </div>;
}

/** Online, offline or no agent, with the version and a flag when an update is due. */
function AgentCell({ a }: { a: Rec }) {
  if (a.state === 'none') return <span className="muted">No agent</span>;
  const on = a.state === 'online';
  return <span className="agent-cell"><Badge tone={on ? 'ok' : 'bad'}>{on ? 'Online' : 'Offline'}</Badge>{' '}
    <span className="muted mono" title={a.lastSeen ? `Last seen ${new Date(String(a.lastSeen)).toLocaleString()}` : 'Never seen'}>{String(a.version || '')}</span>
    {a.updateDue && <>{' '}<Badge tone="warn">update due</Badge></>}</span>;
}

function AddAsset({ onClose }: { onClose: (id?: number) => void }) {
  const [v, setV] = useState<Rec>({ name: '', status: 'in_use', manufacturer: '', model: '', serial: '', location: '', assignedTo: '' }); const types = useLoad<Rec[]>('/api/assets/types');
  const f = useSubmit(() => undefined); const [err, setErr] = useState('');
  const set = (k: string, x: string) => setV({ ...v, [k]: x });
  return <Modal title="Add asset" onClose={() => onClose()}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field>
    <div className="row"><Field label="Type"><select value={v.typeId ?? ''} onChange={(e) => setV({ ...v, typeId: e.target.value ? Number(e.target.value) : undefined })}><option value="">(none)</option>{(Array.isArray(types.data) ? types.data : []).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</select></Field>
      <Field label="Status"><select value={v.status} onChange={(e) => set('status', e.target.value)}>{STATUSES.map((s) => <option key={s} value={s}>{label(s)}</option>)}</select></Field></div>
    <div className="row"><Field label="Manufacturer"><input value={v.manufacturer} onChange={(e) => set('manufacturer', e.target.value)} /></Field><Field label="Model"><input value={v.model} onChange={(e) => set('model', e.target.value)} /></Field></div>
    <div className="row"><Field label="Serial number"><input value={v.serial} onChange={(e) => set('serial', e.target.value)} /></Field><Field label="Location"><input value={v.location} onChange={(e) => set('location', e.target.value)} /></Field></div>
    <Field label="Assigned to"><input value={v.assignedTo} onChange={(e) => set('assignedTo', e.target.value)} /></Field>
    <Err text={err || f.err} /><div className="actions"><button onClick={() => onClose()}>Cancel</button>
      <button className="primary" disabled={!v.name.trim()} onClick={async () => { setErr(''); try { const r = await api<Rec>('/api/assets', { body: v }); onClose(r.id); } catch (e) { setErr((e as Error).message); } }}>Create</button></div></div></Modal>;
}

export function PrintDialog({ ids, onClose }: { ids: number[]; onClose: () => void }) {
  const [layout, setLayout] = useState('sheet'); const [content, setContent] = useState('details'); const [size, setSize] = useState('35'); const [text, setText] = useState(true);
  return <Modal title={`Print QR labels (${ids.length})`} onClose={onClose}><div className="form">
    <div className="row"><Field label="Layout"><select value={layout} onChange={(e) => setLayout(e.target.value)}><option value="sheet">A4 sheet, 10 × 10</option><option value="label">One label each</option></select></Field>
      <Field label="Content"><select value={content} onChange={(e) => setContent(e.target.value)}><option value="details">Details (compact)</option><option value="full">Full details</option><option value="link">Link to the asset</option></select></Field></div>
    {layout === 'label' && <Field label="Label size"><select value={size} onChange={(e) => setSize(e.target.value)}><option value="25">25 mm</option><option value="35">35 mm</option><option value="50">50 mm</option></select></Field>}
    <label><input type="checkbox" checked={text} onChange={(e) => setText(e.target.checked)} /> Print the tag under each code</label>
    <div className="actions"><button onClick={onClose}>Cancel</button><button className="primary" onClick={() => { void openPage(`/api/assets/qr/print?ids=${ids.join(',')}&layout=${layout}&content=${content}&size=${size}&text=${text ? 1 : 0}`); onClose(); }}>Open print page</button></div></div></Modal>;
}
