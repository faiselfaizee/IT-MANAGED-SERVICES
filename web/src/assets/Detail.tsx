import { Relations } from './Relations';
import PersonInput from './PersonInput';
import { InstallFromRepository } from './Packages';
import { PurchaseFields, purchaseBody } from './Purchase';
import { useState } from 'react';
import { usePerms } from '../perms';
import { Icon } from '../chrome';
import { api, go, openPage, saveFile, useLoad, type Rec } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Loading, Meter, Modal, ago, fmtBytes, fmtDate, fmtDay, fmtUptime, useSubmit } from '../ui';
import { Checks, Commands, Ise, RemoteControl, Shell, Tickets } from './DetailOps';
import { AgentInstall } from './AgentInstall';
import { AgentUpdate } from './AgentUpdate';
import { DEEP, Hardware, Watch } from './Hardware';
import { Configs } from './Configs';
import { DuplicateNotice, matchesOf } from './Duplicates';
import { AttachedCard, CustomFields } from './Peripherals';
import { ServicesPanel, UninstallForm } from './Services';
import { PrinterPanel } from './Printer';
import { LIFECYCLE_TABS, useAssetsLite } from './lite';

const TABS = [
  ['overview', 'Overview'], ['storage', 'Storage'], ['network', 'Network'], ['checks', 'Service checks'], ['apps', 'Applications'], ['services', 'Services'], ['tickets', 'Tickets'], ['relations', 'Relationships'],
  ['logs', 'Logs'], ['commands', 'Commands'], ['shell', 'Shell'], ['ise', 'PowerShell ISE'], ['remote', 'Remote control'],
] as const;

/** The Hardware tab appears for devices a controller reads in depth (VMware today). */
function tabsFor(d: Rec): (readonly [string, string])[] {
  const hw = d.hardware && Object.keys(d.hardware).length > 0;
  const printer = heroKind(d) === 'printer';
  if (!hw && !DEEP.includes(d.watch?.mode) && !d.watch?.bmc) { const t: (readonly [string, string])[] = [...TABS]; if (printer) t.splice(1, 0, ['printer', 'Printer']); return t; }
  const t: (readonly [string, string])[] = [...TABS]; t.splice(3, 0, ['hardware', 'Hardware']);
  if (d.watch?.mode === 'network') t.splice(4, 0, ['config', 'Configuration']);
  if (printer) t.splice(1, 0, ['printer', 'Printer']);
  return t;
}

/** The kind of device, for the coloured tile at the top of the page. */
function heroKind(d: Rec): 'server' | 'linux' | 'network' | 'printer' | 'phone' | 'pc' {
  const t = `${d.category ?? ''} ${d.type ?? ''} ${d.identity?.model ?? ''}`.toLowerCase(); const os = String(d.system?.osFamily ?? '').toLowerCase();
  if (/printer/.test(t)) return 'printer';
  if (d.watch?.mode === 'network' || d.watch?.mode === 'snmp' || /switch|router|firewall|access point|network/.test(t)) return 'network';
  if (/phone|mobile|tablet/.test(t)) return 'phone';
  if (os === 'linux') return 'linux';
  if (/server|poweredge|proliant|thinksystem|esxi/.test(t) || /server/.test(String(d.system?.os ?? '').toLowerCase())) return 'server';
  return 'pc';
}
const HERO_ICON = { server: 'server', linux: 'server', network: 'network', printer: 'printer', phone: 'phone', pc: 'Assets' } as const;

export default function Detail({ id, tab }: { id: string; tab: string }) {
  const lite = useAssetsLite();
  const { data: d, error, loading, reload } = useLoad<Rec>(`/api/assets/${id}/device`);
  const perms = usePerms('assets'); const approval = useLoad<Rec>('/api/assets/delete-approval');
  const vault = useLoad<Rec>(`/api/vault/assets/${id}/items`, [id]);
  const att = useLoad<Rec>(`/api/assets/${id}/attached`, [id]);
  const [modal, setModal] = useState<'' | 'ownership' | 'handover' | 'qr' | 'edit' | 'ask'>('');
  if (!d) return <Loading loading={loading} error={error} />;
  const online = d.agent.online as boolean, hasAgent = !!d.agent.id; const quiet = !!att.data?.peripheral && !hasAgent;
  return <div>
    <p><a href="#/assets">← All assets</a></p>
    <section className="asset-hero">
      <span className={'hero-ico ' + heroKind(d)}><Icon name={HERO_ICON[heroKind(d)]} size={30} /></span>
      <div className="hero-main">
        <h1>{d.name} <span className="muted mono">{d.tag}</span> {hasAgent && <Badge tone={online ? 'ok' : 'bad'}>{online ? 'Online' : 'Offline'}</Badge>} {d.ownership.underAmc && <Badge tone="ok">AMC</Badge>} {d.ownership.rented && <Badge>Rented</Badge>}</h1>
        <div className="hero-meta">
          {d.company && <span className="for-customers"><b>Customer</b>{d.company}</span>}
          {d.identity?.serial && <span><b>Serial</b>{d.identity.serial}</span>}
          {d.system?.ip && <span><b>IP</b>{d.system.ip}</span>}
          {(d.system?.os || d.identity?.model) && <span><b>{d.system?.os ? 'OS' : 'Model'}</b>{d.system?.os || [d.identity?.make, d.identity?.model].filter(Boolean).join(' ')}</span>}
          {d.ownership?.assignedTo && <span><b>Holder</b>{d.ownership.assignedTo}</span>}
          {d.ownership?.location && <span><b>Room</b>{d.ownership.location}</span>}
        </div>
      </div>
    </section>
    <div className="tools hero-tools">
      {perms.admin && <button onClick={() => setModal('ownership')}>Customer &amp; category</button>}
      {perms.admin && <button onClick={() => setModal('handover')}>{d.ownership.assignedTo ? 'Hand over' : 'Allocate'}</button>}
      <button onClick={() => setModal('qr')}>QR label</button>
      {vault.data && <a className="button" href={`#/vault?asset=${id}`}>Passwords in the Vault ({((vault.data.items ?? []) as Rec[]).length})</a>}
      <Act label="Export PDF" run={() => saveFile(`/api/assets/${id}/export.pdf`, `${d.tag}.pdf`)} />
      <Act label="Export all details (CSV)" run={() => saveFile(`/api/assets/${id}/export.csv`, `${d.tag}-details.csv`)} />
      {perms.modify && <button onClick={() => setModal('edit')}>Edit</button>}
      {perms.delete ? <Act label="Delete" danger confirm={`Delete ${d.name} (${d.tag}) for good? Its history goes with it.`} run={async () => { await api(`/api/assets/${id}`, { method: 'DELETE' }); go('assets'); }} />
        : perms.modify && <button className={approval.data?.required ? '' : 'danger'} onClick={() => setModal('ask')}>{approval.data?.required ? 'Request deletion' : 'Delete'}</button>}
      {perms.admin && hasAgent && <Act label="Refresh inventory" run={async () => { await api(`/api/assets/${id}/refresh`, { method: 'POST', body: {} }); reload(); }} />}
    </div>
    <nav className="sub" aria-label="Asset tabs">{tabsFor(d).filter(([k]) => !(lite && LIFECYCLE_TABS.includes(k)) && (k !== 'services' || hasAgent) && (!quiet || !['checks', 'commands', 'shell', 'ise', 'remote', 'apps', 'logs', 'network', 'storage'].includes(k))).map(([k, label]) => <a key={k} href={`#/assets/${id}/${k}`} className={tab === k ? 'on' : ''}>{label}</a>)}</nav>
    {tab === 'overview' && <><Overview id={id} d={d} reload={reload} admin={!!perms.admin} quiet={quiet} /><AttachedCard id={id} d={d} data={att.data} reload={att.reload} canEdit={!!perms.modify} /><Details id={id} /></>}
    {tab === 'printer' && <PrinterPanel id={id} canAct={!!perms.modify} />}
    {tab === 'storage' && <Storage d={d} />}
    {tab === 'network' && <Network d={d} />}
    {tab === 'hardware' && <Hardware d={d} />}
    {tab === 'config' && <Configs id={id} />}
    {tab === 'checks' && <Checks id={id} />}
    {tab === 'apps' && <Apps id={id} canUninstall={hasAgent && !!perms.modify} os={d.system?.osFamily === 'linux' ? 'linux' : 'windows'} />}
    {tab === 'services' && <ServicesPanel assetId={id} />}
    {tab === 'tickets' && <Tickets id={id} d={d} />}
    {tab === 'relations' && <Relations id={id} canEdit={!!perms.modify} />}
    {tab === 'logs' && <Logs id={id} />}
    {tab === 'commands' && <Commands id={id} d={d} />}
    {tab === 'shell' && <Shell id={id} d={d} />}
    {tab === 'ise' && <Ise id={id} d={d} />}
    {tab === 'remote' && <RemoteControl id={id} d={d} />}
    {modal === 'ownership' && <Ownership id={id} d={d} onClose={(ch) => { setModal(''); if (ch) reload(); }} />}
    {modal === 'handover' && <Handover id={id} d={d} onClose={(ch) => { setModal(''); if (ch) reload(); }} />}
    {modal === 'qr' && <QrDialog id={id} tag={d.tag} onClose={() => setModal('')} />}
    {modal === 'edit' && <EditAsset id={id} d={d} onClose={(ch) => { setModal(''); if (ch) reload(); }} />}
    {modal === 'ask' && <AskDelete id={id} d={d} onClose={() => setModal('')} />}
  </div>;
}

function Overview({ id, d, reload, admin, quiet }: { id: string; d: Rec; reload: () => void; admin: boolean; quiet?: boolean }) {
  const i = d.identity, s = d.system, o = d.ownership, a = d.agent, rm = d.remote;
  const cov = useLoad<Rec>(`/api/assets/${id}/coverage`, [d]).data;
  const warrantyLeft = o.warrantyEnd ? Math.ceil((new Date(o.warrantyEnd).getTime() - Date.now()) / 86400000) : null;
  return <div className="grid">
    <Card title="Identity"><Dl rows={[['Make', i.make], ['Model', i.model], ['Serial number', i.serial], ['BIOS', i.bios], ['Processor', [i.cpuMake, i.cpuModel].filter(Boolean).join(' ')], ['Sockets / cores', i.cpus ? `${i.cpus} / ${i.cores}${i.logical ? ` (${i.logical} threads)` : ''}` : ''], ['Memory', i.ramBytes ? fmtBytes(i.ramBytes) : ''], ['Storage', i.storageBytes ? fmtBytes(i.storageBytes) : '']]} /></Card>
    {!quiet && <Card title="System"><Dl rows={[['Host name', s.hostname], ['DNS name', s.dnsName], ['IP address', s.ip], ['Domain', s.domainType ? `${s.domainType}${s.domain ? ': ' + s.domain : ''}` : ''], ['Operating system', [s.os, s.osVersion].filter(Boolean).join(' ')], ['Antivirus', s.antivirus ? <Badge tone={s.antivirus === 'Protected' ? 'ok' : 'warn'}>{s.antivirus}</Badge> : ''], ['Last patch', s.lastPatch], ['Uptime', s.uptimeSeconds ? fmtUptime(s.uptimeSeconds) : '']]} /></Card>}
    <Card title="Ownership"><Dl rows={[['Customer', d.company || '(own)'], ['Category', d.category], ['Status', d.status], ['Assigned to', o.assignedTo], ['Site', o.site ? `${o.site}${o.siteCode ? ` (${o.siteCode})` : ''}` : ''], ['Room or area', o.location],
      ['Vendor', o.vendor], ['Purchase order', o.poNumber], ['Invoice', o.invoiceNumber], ['Purchased', o.purchaseDate], ['Warranty period', o.warrantyMonths ? `${o.warrantyMonths} months` : ''], ['Warranty ends', o.warrantyEnd ? <>{fmtDay(o.warrantyEnd)} {warrantyLeft !== null && <Badge tone={warrantyLeft < 0 ? 'bad' : warrantyLeft < 60 ? 'warn' : 'ok'}>{warrantyLeft < 0 ? 'expired' : warrantyLeft + ' days left'}</Badge>}</> : ''], ['AMC', cov?.amc ? `${cov.amc.title}${cov.amc.vendor ? ' (' + cov.amc.vendor + ')' : ''}` : o.amc || (o.underAmc ? 'Yes' : '')], ['Rental', cov?.rental ? `${cov.rental.title}${cov.rental.vendor ? ' (' + cov.rental.vendor + ')' : ''}` : ''], ['Monitoring', cov ? (cov.monitored ? 'On' : 'Not monitored') : ''], ['Notes', o.notes]]} /></Card>
    {!quiet && <Card title="Agent &amp; remote access">
      <Dl rows={[['Agent', a.id ? (a.online ? 'Online' : 'Offline') : 'None'], ['Last seen', a.lastSeen ? `${ago(a.lastSeen)} (${fmtDate(a.lastSeen)})` : ''], ['Data from', a.source], ['Inventory at', a.inventoryAt ? fmtDate(a.inventoryAt) : '']]} />
      <p><label><input type="checkbox" checked={rm.assetAllows} onChange={async (e) => { await api(`/api/assets/${id}/remote`, { body: { on: e.target.checked } }); reload(); }} /> Allow remote commands, shell and remote control on this device</label></p>
      {!rm.companyAllows && <p className="note">Remote access is switched off for this customer. Switch it on under Companies.</p>}
      {!a.id && <AgentInstall id={id} onDone={reload} />}
      {a.id && <AgentUpdate id={id} admin={admin} onDone={reload} />}
    </Card>}
    {admin && !quiet && <Watch id={id} d={d} onChange={reload} />}
  </div>;
}

/** The extra details of the asset's kind (IMEI, screen size…), with the values it has. */
function Details({ id }: { id: string }) {
  const full = useLoad<Rec>(`/api/assets/${id}`, [id]); const types = useLoad<Rec[]>('/api/assets/types');
  const a = full.data?.asset as Rec | undefined; const t = (Array.isArray(types.data) ? types.data : []).find((x) => x.id === a?.typeId);
  const fields = ((t?.fields ?? []) as Rec[]).filter((f) => a?.custom?.[f.key] !== undefined && a?.custom?.[f.key] !== '');
  if (!a || fields.length === 0) return null;
  return <Card title={`${t?.name} details`}><Dl rows={fields.map((f) => [f.label, f.kind === 'bool' ? (a.custom[f.key] ? 'Yes' : 'No') : String(a.custom[f.key])])} /></Card>;
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

function Apps({ id, canUninstall, os }: { id: string; canUninstall: boolean; os: string }) {
  const [q, setQ] = useState(''); const [rm, setRm] = useState<Rec | null>(null); const [add, setAdd] = useState(false); const [msg, setMsg] = useState<{ text: string; ok: boolean } | null>(null);
  const [gone, setGone] = useState<string[]>([]);
  const { data, error, loading } = useLoad<Rec>(`/api/assets/${id}/applications?q=${encodeURIComponent(q)}`);
  const key = (a: Rec) => `${a.name}|${a.version ?? ''}`;
  return <><div className="tools"><input placeholder="Search installed applications" value={q} onChange={(e) => setQ(e.target.value)} />{canUninstall && <button onClick={() => setAdd(true)}>Install from the repository</button>}<span className="muted">{data ? `${data.count} applications` : ''}</span></div>
    {add && <InstallFromRepository assetId={Number(id)} os={os} onClose={() => setAdd(false)} />}
    {msg && <p role="status" className={msg.ok ? 'ok-note' : 'err'}>{msg.text}{msg.ok ? ' The list updates when the agent next sends its inventory.' : ''}</p>}
    <Loading loading={loading && !data} error={error} />
    {data && <table><thead><tr><th>Name</th><th>Version</th><th>Publisher</th><th>Installed</th>{canUninstall && <th><span className="sr-only">Actions</span></th>}</tr></thead><tbody>
      {(data.items as Rec[]).map((a, i) => <tr key={i} className={gone.includes(key(a)) ? 'gone' : undefined}><td>{a.name}</td><td>{a.version}</td><td>{a.publisher}</td><td>{a.installedOn}</td>
        {canUninstall && <td>{gone.includes(key(a)) ? <span className="muted small">Uninstalled</span> : <button type="button" className="small-btn" onClick={() => setRm(a)}>Uninstall</button>}</td>}</tr>)}</tbody></table>}
    {rm && <UninstallForm assetId={id} app={rm} onClose={() => setRm(null)} onDone={(text, ok) => { setMsg({ text, ok }); if (ok) setGone((g) => [...g, key(rm)]); setRm(null); }} />}</>;
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
    {!holder && <Field label="Receiving person"><PersonInput value={to} onChange={setTo} companyId={d.companyId} autoFocus /></Field>}
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

const STATUS_CHOICES = ['stock', 'in_use', 'repair', 'retired', 'lost'];

function EditAsset({ id, d, onClose }: { id: string; d: Rec; onClose: (changed: boolean) => void }) {
  const full = useLoad<Rec>(`/api/assets/${id}`); const cov = useLoad<Rec>(`/api/assets/${id}/coverage`);
  const a = (full.data?.asset ?? null) as Rec | null;
  if (!a || !cov.data) return <Modal title={`Edit ${d.tag}`} onClose={() => onClose(false)}><Loading loading={!full.error && !cov.error} error={full.error || cov.error} /></Modal>;
  return <EditForm id={id} d={d} a={a} cov={cov.data} onClose={onClose} />;
}
function EditForm({ id, d, a, cov, onClose }: { id: string; d: Rec; a: Rec; cov: Rec; onClose: (changed: boolean) => void }) {
  const [v, setV] = useState<Rec>({ name: a.name ?? '', status: a.status ?? 'in_use', manufacturer: a.manufacturer ?? '', model: a.model ?? '', serial: a.serial ?? '', assignedTo: a.assignedTo ?? '',
    ipAddress: d.system?.ip ?? '', location: a.location ?? '', siteId: a.siteId ?? null, vendorId: a.vendorId ?? null, poNumber: a.poNumber ?? '', invoiceNumber: a.invoiceNumber ?? '',
    purchaseDate: a.purchaseDate ?? '', warrantyMonths: a.warrantyMonths ?? null, warrantyEnd: a.warrantyEnd ?? '',
    hostname: cov.hostname ?? '', macAddress: cov.macAddress ?? '', monitored: cov.monitored !== false, custom: a.custom ?? {}, amcId: cov.amc?.id ?? null, rentalId: cov.rental?.id ?? null });
  const [dup, setDup] = useState<ReturnType<typeof matchesOf>>(null);
  const f = useSubmit(() => onClose(true)); const set = (k: string, x: string) => setV({ ...v, [k]: x });
  const types = useLoad<Rec[]>('/api/assets/types'); const kind = (Array.isArray(types.data) ? types.data : []).find((x) => x.id === a.typeId); const remote = !(kind?.peripheral && !d.agent?.id);
  const save = (confirmDuplicate: boolean) => { setDup(null); return f.submit(async () => {
    try {
      await api(`/api/assets/${id}`, { method: 'PUT', body: { name: v.name, status: v.status, manufacturer: v.manufacturer, model: v.model, serial: v.serial, assignedTo: v.assignedTo, ipAddress: String(v.ipAddress).trim(), ...purchaseBody(v), hostname: String(v.hostname).trim(), macAddress: String(v.macAddress).trim(), monitored: v.monitored, custom: v.custom, confirmDuplicate } });
    } catch (e) { const m = matchesOf(e); if (m) { setDup(m); throw new Error(''); } throw e; }
    const body: Rec = {}; if ((v.amcId ?? null) !== (cov.amc?.id ?? null) && cov.canEditAmc) body.amcId = v.amcId; if ((v.rentalId ?? null) !== (cov.rental?.id ?? null) && cov.canEditRental) body.rentalId = v.rentalId;
    if (Object.keys(body).length) await api(`/api/assets/${id}/coverage`, { method: 'PUT', body });
  }); };
  return <Modal title={`Edit ${d.tag}`} onClose={() => onClose(false)}><div className="form">
    <Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus /></Field>
    <div className="row"><Field label="Status"><select value={v.status} onChange={(e) => set('status', e.target.value)}>{STATUS_CHOICES.map((x) => <option key={x} value={x}>{x.replace('_', ' ')}</option>)}</select></Field>
      <Field label="Assigned to (name or email)"><PersonInput value={v.assignedTo ?? ''} onChange={(x) => set('assignedTo', x)} companyId={a.companyId} /></Field></div>
    {remote && <>
    <Field label="IP address (the address DeskAsk uses to reach it)"><input value={v.ipAddress} onChange={(e) => set('ipAddress', e.target.value)} placeholder="10.0.0.25" spellCheck={false} /></Field>
    <div className="row"><Field label="Host name"><input value={v.hostname} onChange={(e) => set('hostname', e.target.value)} spellCheck={false} /></Field>
      <Field label="MAC address"><input value={v.macAddress} onChange={(e) => set('macAddress', e.target.value)} placeholder="00:1A:2B:3C:4D:5E" spellCheck={false} /></Field></div>
    <label><input type="checkbox" checked={v.monitored} onChange={(e) => setV({ ...v, monitored: e.target.checked })} /> Monitor this asset (alerts, services, checks)</label>
    </>}
    {d.agent?.id && <p className="muted small">This asset has an agent. After you change the IP address or host name the agent is asked to report again, and what it sees replaces what you typed.</p>}
    <div className="row"><Field label="Under AMC"><select value={v.amcId ?? ''} disabled={!cov.canEditAmc} onChange={(e) => setV({ ...v, amcId: e.target.value ? Number(e.target.value) : null })}><option value="">No AMC</option>
        {(cov.amcChoices as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.title}{c.vendor ? ` (${c.vendor})` : ''}{c.end ? ` to ${c.end}` : ''}</option>)}</select></Field>
      <Field label="Under rental"><select value={v.rentalId ?? ''} disabled={!cov.canEditRental} onChange={(e) => setV({ ...v, rentalId: e.target.value ? Number(e.target.value) : null })}><option value="">Not rented</option>
        {(cov.rentalChoices as Rec[]).map((c) => <option key={c.id} value={c.id}>{c.title}{c.vendor ? ` (${c.vendor})` : ''}{c.end ? ` to ${c.end}` : ''}</option>)}</select></Field></div>
    <p className="muted small">Contracts and their vendors are kept under AMC and Rentals; vendors under Vendors.</p>
    <div className="row"><Field label="Manufacturer"><input value={v.manufacturer} onChange={(e) => set('manufacturer', e.target.value)} /></Field><Field label="Model"><input value={v.model} onChange={(e) => set('model', e.target.value)} /></Field><Field label="Serial number"><input value={v.serial} onChange={(e) => set('serial', e.target.value)} /></Field></div>
    <CustomFields fields={(kind?.fields ?? []) as Rec[]} value={v.custom} onChange={(c) => setV({ ...v, custom: c })} />
    <PurchaseFields v={v} set={(p) => setV({ ...v, ...p })} companyId={a.companyId} />
    {!a.companyId && <p className="muted small">Set the customer (Ownership) to choose a site.</p>}
    {dup && <DuplicateNotice info={dup} busy={f.busy} onAnyway={() => void save(true)} />}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !String(v.name).trim()} onClick={() => void save(false)}>Save</button></div></div></Modal>;
}

function AskDelete({ id, d, onClose }: { id: string; d: Rec; onClose: () => void }) {
  const [reason, setReason] = useState(''); const [sent, setSent] = useState(false); const f = useSubmit(() => setSent(true));
  const approval = useLoad<Rec>('/api/assets/delete-approval'); const [typed, setTyped] = useState('');
  if (approval.data && !approval.data.required) return <Modal title={`Delete ${d.tag}`} onClose={onClose}><div className="form">
    <p>This deletes <strong>{d.name}</strong> ({d.tag}) for good, with its history. It cannot be undone; the reason is kept in the audit log. To keep the record, set its status to Retired instead.</p>
    <Field label="Why?"><textarea rows={2} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus /></Field>
    <Field label={`Type ${d.tag} to confirm`}><input value={typed} onChange={(e) => setTyped(e.target.value)} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
      <button className="primary danger" disabled={f.busy || !reason.trim() || typed.trim() !== d.tag} onClick={() => f.submit(async () => { await api(`/api/assets/${id}/delete-request`, { body: { reason } }); go('assets'); })}>Delete</button></div>
  </div></Modal>;
  return <Modal title={`Request deletion of ${d.tag}`} onClose={onClose}><div className="form">
    {sent ? <><p>Request sent. The asset stays until an approver decides; follow it under Delete requests.</p><div className="actions"><button className="primary" onClick={onClose}>Close</button></div></> : <>
      <p className="muted">You may edit assets but not delete them. An approver (an administrator, or a team lead or manager who was given the delete right) will decide.</p>
      <Field label="Why should it be deleted?"><textarea rows={3} value={reason} onChange={(e) => setReason(e.target.value)} autoFocus /></Field>
      <Err text={f.err} /><div className="actions"><button onClick={onClose}>Cancel</button>
        <button className="primary" disabled={f.busy || !reason.trim()} onClick={() => f.submit(() => api(`/api/assets/${id}/delete-request`, { body: { reason } }))}>Send request</button></div></>}
  </div></Modal>;
}
