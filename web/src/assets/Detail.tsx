import { useState } from 'react';
import { api, openPage, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Meter, Modal, ago, fmtBytes, fmtDate, fmtDay, fmtUptime, useSubmit } from '../ui';
import { Checks, Commands, Ise, RemoteControl, Shell, Tickets } from './DetailOps';

const TABS = [
  ['overview', 'Overview'], ['storage', 'Storage'], ['network', 'Network'], ['checks', 'Service checks'], ['apps', 'Applications'], ['tickets', 'Tickets'],
  ['logs', 'Logs'], ['commands', 'Commands'], ['shell', 'Shell'], ['ise', 'PowerShell ISE'], ['remote', 'Remote control'],
] as const;

export default function Detail({ id, tab }: { id: string; tab: string }) {
  const { data: d, error, loading, reload } = useLoad<Rec>(`/api/assets/${id}/device`);
  const [modal, setModal] = useState<'' | 'ownership' | 'handover' | 'qr'>('');
  if (!d) return <Loading loading={loading} error={error} />;
  const online = d.agent.online as boolean, hasAgent = !!d.agent.id;
  return <div>
    <p><a href="#/assets">← All assets</a></p>
    <h1>{d.name} <span className="muted mono">{d.tag}</span> {hasAgent && <Badge tone={online ? 'ok' : 'bad'}>{online ? 'Online' : 'Offline'}</Badge>} {d.ownership.underAmc && <Badge tone="ok">AMC</Badge>} {d.ownership.rented && <Badge>Rented</Badge>}</h1>
    <div className="tools">
      <button onClick={() => setModal('ownership')}>Customer &amp; category</button>
      <button onClick={() => setModal('handover')}>{d.ownership.assignedTo ? 'Hand over' : 'Allocate'}</button>
      <button onClick={() => setModal('qr')}>QR label</button>
      {hasAgent && <Act label="Refresh inventory" run={async () => { await api(`/api/assets/${id}/refresh`, { method: 'POST', body: {} }); reload(); }} />}
    </div>
    <nav className="sub" aria-label="Asset tabs">{TABS.map(([k, label]) => <a key={k} href={`#/assets/${id}/${k}`} className={tab === k ? 'on' : ''}>{label}</a>)}</nav>
    {tab === 'overview' && <Overview id={id} d={d} reload={reload} />}
    {tab === 'storage' && <Storage d={d} />}
    {tab === 'network' && <Network d={d} />}
    {tab === 'checks' && <Checks id={id} />}
    {tab === 'apps' && <Apps id={id} />}
    {tab === 'tickets' && <Tickets id={id} />}
    {tab === 'logs' && <Logs id={id} />}
    {tab === 'commands' && <Commands id={id} d={d} />}
    {tab === 'shell' && <Shell id={id} d={d} />}
    {tab === 'ise' && <Ise id={id} d={d} />}
    {tab === 'remote' && <RemoteControl id={id} d={d} />}
    {modal === 'ownership' && <Ownership id={id} d={d} onClose={(ch) => { setModal(''); if (ch) reload(); }} />}
    {modal === 'handover' && <Handover id={id} d={d} onClose={(ch) => { setModal(''); if (ch) reload(); }} />}
    {modal === 'qr' && <QrDialog id={id} tag={d.tag} onClose={() => setModal('')} />}
  </div>;
}

function Overview({ id, d, reload }: { id: string; d: Rec; reload: () => void }) {
  const i = d.identity, s = d.system, o = d.ownership, a = d.agent, rm = d.remote;
  const warrantyLeft = o.warrantyEnd ? Math.ceil((new Date(o.warrantyEnd).getTime() - Date.now()) / 86400000) : null;
  return <div className="grid">
    <Card title="Identity"><Dl rows={[['Make', i.make], ['Model', i.model], ['Serial number', i.serial], ['BIOS', i.bios], ['Processor', [i.cpuMake, i.cpuModel].filter(Boolean).join(' ')], ['Sockets / cores', i.cpus ? `${i.cpus} / ${i.cores}${i.logical ? ` (${i.logical} threads)` : ''}` : ''], ['Memory', i.ramBytes ? fmtBytes(i.ramBytes) : ''], ['Storage', i.storageBytes ? fmtBytes(i.storageBytes) : '']]} /></Card>
    <Card title="System"><Dl rows={[['Host name', s.hostname], ['DNS name', s.dnsName], ['IP address', s.ip], ['Domain', s.domainType ? `${s.domainType}${s.domain ? ': ' + s.domain : ''}` : ''], ['Operating system', [s.os, s.osVersion].filter(Boolean).join(' ')], ['Antivirus', s.antivirus ? <Badge tone={s.antivirus === 'Protected' ? 'ok' : 'warn'}>{s.antivirus}</Badge> : ''], ['Last patch', s.lastPatch], ['Uptime', s.uptimeSeconds ? fmtUptime(s.uptimeSeconds) : '']]} /></Card>
    <Card title="Ownership"><Dl rows={[['Customer', d.company || '(own)'], ['Category', d.category], ['Status', d.status], ['Assigned to', o.assignedTo], ['Location', o.location], ['Purchased', o.purchaseDate], ['Warranty ends', o.warrantyEnd ? <>{fmtDay(o.warrantyEnd)} {warrantyLeft !== null && <Badge tone={warrantyLeft < 0 ? 'bad' : warrantyLeft < 60 ? 'warn' : 'ok'}>{warrantyLeft < 0 ? 'expired' : warrantyLeft + ' days left'}</Badge>}</> : ''], ['AMC', o.amc || (o.underAmc ? 'Yes' : '')], ['Notes', o.notes]]} /></Card>
    <Card title="Agent &amp; remote access">
      <Dl rows={[['Agent', a.id ? (a.online ? 'Online' : 'Offline') : 'None'], ['Last seen', a.lastSeen ? `${ago(a.lastSeen)} (${fmtDate(a.lastSeen)})` : ''], ['Data from', a.source], ['Inventory at', a.inventoryAt ? fmtDate(a.inventoryAt) : '']]} />
      <p><label><input type="checkbox" checked={rm.assetAllows} onChange={async (e) => { await api(`/api/assets/${id}/remote`, { body: { on: e.target.checked } }); reload(); }} /> Allow remote commands, shell and remote control on this device</label></p>
      {!rm.companyAllows && <p className="note">Remote access is switched off for this customer. Switch it on under Companies.</p>}
      {!a.id && <p><Act label="Install agent through a collector" run={async () => { await api(`/api/assets/${id}/agent-install`, { body: {} }); }} /></p>}
    </Card>
  </div>;
}

function Storage({ d }: { d: Rec }) {
  const disks: Rec[] = d.storage ?? [];
  if (!disks.length) return <Empty>No storage details yet. They arrive with the next inventory.</Empty>;
  return <>{disks.map((k, n) => <Card key={n} title={`${k.name || 'Disk ' + (n + 1)}${k.model ? ' · ' + k.model : ''}`}>
    <p className="muted">{fmtBytes(k.size_bytes)} {k.type && <Badge>{k.type}</Badge>}</p>
    <table><thead><tr><th>Volume</th><th>File system</th><th className="num">Size</th><th className="num">Free</th><th style={{ width: 160 }}>Used</th></tr></thead><tbody>
      {(k.partitions ?? []).map((p: Rec, j: number) => { const used = p.size_bytes ? ((p.size_bytes - (p.free_bytes ?? 0)) / p.size_bytes) * 100 : 0;
        return <tr key={j}><td>{p.mount || p.name}{p.label ? ` (${p.label})` : ''}</td><td>{p.fs}</td><td className="num">{fmtBytes(p.size_bytes)}</td><td className="num">{p.free_bytes != null ? fmtBytes(p.free_bytes) : '–'}</td><td>{p.free_bytes != null && <Meter pct={used} />}</td></tr>; })}
    </tbody></table></Card>)}</>;
}

function Network({ d }: { d: Rec }) {
  const nics: Rec[] = d.network ?? [];
  if (!nics.length) return <Empty>No network details yet.</Empty>;
  return <><table><thead><tr><th>Adapter</th><th>MAC</th><th>Addresses</th><th className="num">Speed</th><th>State</th></tr></thead><tbody>
    {nics.map((n, i) => <tr key={i}><td>{n.name}<div className="muted">{n.description}</div></td><td className="mono">{n.mac}</td><td className="mono">{(n.ips ?? []).join(', ')}</td><td className="num">{n.speed_mbps ? n.speed_mbps + ' Mbps' : '–'}</td><td><Badge tone={n.connected ? 'ok' : undefined}>{n.connected ? 'Connected' : 'Down'}</Badge></td></tr>)}
  </tbody></table>{(d.connectedIps ?? []).length > 0 && <p className="muted">Connected addresses: <span className="mono">{d.connectedIps.join(', ')}</span></p>}</>;
}

function Apps({ id }: { id: string }) {
  const [q, setQ] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/assets/${id}/applications?q=${encodeURIComponent(q)}`);
  return <><div className="tools"><input placeholder="Search installed applications" value={q} onChange={(e) => setQ(e.target.value)} /><span className="muted">{data ? `${data.count} applications` : ''}</span></div>
    <Loading loading={loading && !data} error={error} />
    {data && <table><thead><tr><th>Name</th><th>Version</th><th>Publisher</th><th>Installed</th></tr></thead><tbody>
      {(data.items as Rec[]).map((a, i) => <tr key={i}><td>{a.name}</td><td>{a.version}</td><td>{a.publisher}</td><td>{a.installedOn}</td></tr>)}</tbody></table>}</>;
}

function Logs({ id }: { id: string }) {
  const [kind, setKind] = useState('');
  const { data, error, loading } = useLoad<Rec>(`/api/assets/${id}/logs?kind=${kind}`);
  const kinds: string[] = data?.kinds ?? [];
  const brief = (t: string) => { try { const o = JSON.parse(t); return Object.entries(o).map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`).join(' · '); } catch { return t; } };
  return <><div className="tools"><select value={kind} onChange={(e) => setKind(e.target.value)} aria-label="Kind"><option value="">All activity</option>{kinds.map((k) => <option key={k}>{k}</option>)}</select></div>
    <Loading loading={loading && !data} error={error} />
    {data && (data.items.length === 0 ? <Empty>Nothing recorded yet.</Empty> : <table><thead><tr><th>When</th><th>Kind</th><th>By</th><th>What</th></tr></thead><tbody>
      {(data.items as Rec[]).map((l, i) => <tr key={i}><td className="mono">{fmtDate(l.at)}</td><td><Badge>{l.kind}</Badge></td><td>{l.by}</td><td>{brief(l.text)}</td></tr>)}</tbody></table>)}</>;
}

function Ownership({ id, d, onClose }: { id: string; d: Rec; onClose: (changed: boolean) => void }) {
  const { data: companies } = useLoad<Rec[] | Rec>('/api/assets/companies');
  const [co, setCo] = useState(d.companyId ? String(d.companyId) : ''); const [cat, setCat] = useState<string>(d.category ?? '');
  const f = useSubmit(() => onClose(true));
  const list = (Array.isArray(companies) ? companies : []) as Rec[];
  return <Modal title="Customer and category" onClose={() => onClose(false)}><div className="form">
    <Field label="Customer"><select value={co} onChange={(e) => setCo(e.target.value)}><option value="">(own organisation)</option>{list.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select></Field>
    <Field label="Category"><input value={cat} onChange={(e) => setCat(e.target.value)} placeholder="IT hardware, Server, Printers & peripherals…" /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api(`/api/assets/${id}/ownership`, { method: 'PATCH', body: { companyId: co ? Number(co) : null, category: cat } }))}>Save</button></div></div></Modal>;
}

function Handover({ id, d, onClose }: { id: string; d: Rec; onClose: (changed: boolean) => void }) {
  const holder: string = d.ownership.assignedTo; const [to, setTo] = useState(''); const [cond, setCond] = useState('good'); const [acc, setAcc] = useState(''); const [notes, setNotes] = useState('');
  const { data: hist } = useLoad<Rec>(`/api/assets/${id}/handovers`);
  const f = useSubmit(() => onClose(true));
  const body = { condition: cond, to, accessories: acc.split(',').map((x) => x.trim()).filter(Boolean), notes };
  return <Modal title={holder ? `Hand over from ${holder}` : 'Allocate to someone'} onClose={() => onClose(false)}><div className="form">
    {!holder && <Field label="Receiving person"><input value={to} onChange={(e) => setTo(e.target.value)} autoFocus /></Field>}
    <Field label="Condition"><select value={cond} onChange={(e) => setCond(e.target.value)}>{['good', 'fair', 'damaged', 'lost'].map((c) => <option key={c}>{c}</option>)}</select></Field>
    <Field label="Accessories (comma separated)"><input value={acc} onChange={(e) => setAcc(e.target.value)} placeholder="charger, bag, mouse" /></Field>
    <Field label="Notes"><textarea rows={2} value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
    <Err text={f.err} />
    {hist && hist.items.length > 0 && <details><summary>Handover history ({hist.items.length})</summary><ul>{(hist.items as Rec[]).map((h) => <li key={h.id}>{fmtDate(h.at)} · {h.kind === 'assign' ? 'allocated to' : 'handed back by'} <b>{h.holder}</b> ({h.condition}) by {h.by}{h.notes ? ' – ' + h.notes : ''}</li>)}</ul></details>}
    <div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy} onClick={() => f.submit(() => api(`/api/assets/${id}/${holder ? 'handover' : 'allocate'}`, { body }))}>{holder ? 'Record handover' : 'Allocate'}</button></div></div></Modal>;
}

export function QrDialog({ id, tag, onClose }: { id: string; tag: string; onClose: () => void }) {
  const [content, setContent] = useState('details'); const { data } = useLoad<Rec>(`/api/assets/qr/${id}/info?content=${content}`, []);
  return <Modal title={`QR label for ${tag}`} onClose={onClose}><div className="form">
    <Field label="What the code holds"><select value={content} onChange={(e) => setContent(e.target.value)}><option value="details">Details (compact)</option><option value="full">Full details</option><option value="link">Link to the asset</option></select></Field>
    {data && <pre className="term" style={{ minHeight: 0 }}>{data.text}</pre>}
    <div className="actions"><button onClick={onClose}>Close</button>
      <button className="primary" onClick={() => openPage(`/api/assets/qr/print?ids=${id}&content=${content}&layout=label&size=35`)}>Print label</button></div></div></Modal>;
}
