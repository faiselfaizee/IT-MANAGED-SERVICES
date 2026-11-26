import { useState } from 'react';
import { useLoad, type Rec } from '../http';
import { Act, Badge, Card, Empty, Loading, fmtDate } from '../ui';

const TONE: Record<string, 'ok' | 'warn' | 'bad' | undefined> = { ok: 'ok', warn: 'warn', bad: 'bad', info: undefined };
const WORD: Record<string, string> = { ok: 'Good', warn: 'Review', bad: 'Fix', info: 'Info' };

/** Health check (6.24.0): what to fix first in this directory, read from the directory itself. */
export function Health({ domain }: { domain: Rec }) {
  const [run, setRun] = useState(1);
  const h = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/health`, [domain.id, run]);
  if (!h.data) return <><h1>Health check</h1><Loading loading={h.loading} error={h.error} /></>;
  const s = h.data.summary as Rec; const list = (h.data.findings ?? []) as Rec[];
  return <>
    <h1>Health check</h1>
    <p className="muted">Read from {String(domain.name)} just now. Nothing is changed. {Number(s.bad)} to fix, {Number(s.warn)} to review, {Number(s.ok)} fine.
      {' '}<button onClick={() => setRun(run + 1)}>Check again</button></p>
    {list.map((f) => <Card key={String(f.key)}>
      <div className="ad-find"><Badge tone={TONE[String(f.level)]}>{WORD[String(f.level)] ?? 'Info'}</Badge> <strong>{String(f.title)}</strong></div>
      <p style={{ margin: '6px 0' }}>{String(f.text)}</p>
      {Array.isArray(f.items) && f.items.length > 0 && <details><summary>{f.count != null && Number(f.count) > f.items.length ? `First ${f.items.length} of ${f.count}` : `Show ${f.items.length}`}</summary>
        <ul>{(f.items as string[]).map((x, i) => <li key={i}>{x}</li>)}</ul></details>}
    </Card>)}
  </>;
}

/** Group Policy objects and where they are linked. Read only. */
export function Policies({ domain }: { domain: Rec }) {
  const p = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/policies`, [domain.id]);
  const [only, setOnly] = useState('');
  if (!p.data) return <><h1>Group Policy</h1><Loading loading={p.loading} error={p.error} /></>;
  const s = p.data.summary as Rec; const all = (p.data.gpos ?? []) as Rec[];
  const rows = all.filter((g) => only === '' || (only === 'unlinked' && g.unlinked) || (only === 'empty' && g.empty) || (only === 'off' && g.allOff));
  const csv = async () => {
    const q = (v: unknown) => '"' + String(v ?? '').replace(/"/g, '""') + '"';
    const lines = [['Policy', 'Id', 'Status', 'Linked to', 'User version', 'Computer version', 'Changed'].map(q).join(',')];
    for (const g of rows) lines.push([g.name, g.id, g.status, (g.links as Rec[]).map((l) => l.target + (l.enforced ? ' (enforced)' : '') + (l.disabled ? ' (link off)' : '')).join('; '), g.userVersion, g.computerVersion, g.changed ?? ''].map(q).join(','));
    const url = URL.createObjectURL(new Blob(['\ufeff' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = 'group-policies.csv'; a.click(); URL.revokeObjectURL(url);
  };
  return <>
    <h1>Group Policy</h1>
    <p className="muted">{Number(s.total)} policies: {Number(s.unlinked)} not linked anywhere, {Number(s.empty)} with no settings, {Number(s.allOff)} switched off. Editing policies and checking them against a security baseline come later; this page shows what exists and where it applies.</p>
    <div className="tools"><select aria-label="Show" value={only} onChange={(e) => setOnly(e.target.value)}>
      <option value="">All policies</option><option value="unlinked">Not linked</option><option value="empty">No settings</option><option value="off">All settings off</option></select>
      <Act label="Download CSV" run={csv} /></div>
    {rows.length === 0 ? <Empty>No policy matches.</Empty> : <table><thead><tr><th>Policy</th><th>Status</th><th>Applies to</th><th>Version (user / computer)</th><th>Changed</th></tr></thead><tbody>
      {rows.map((g) => <tr key={String(g.id)}>
        <td>{String(g.name)}<div className="muted small">{String(g.id)}{g.wmi ? ' · WMI filter' : ''}</div></td>
        <td>{g.status === 'Enabled' ? <Badge tone="ok">Enabled</Badge> : <Badge tone="warn">{String(g.status)}</Badge>}{g.empty ? <> <Badge>Empty</Badge></> : null}</td>
        <td>{(g.links as Rec[]).length === 0 ? <Badge tone="warn">Not linked</Badge> : (g.links as Rec[]).map((l, i) => <div key={i}>{String(l.target)}{l.enforced ? ' · enforced' : ''}{l.disabled ? ' · link off' : ''}</div>)}</td>
        <td>{String(g.userVersion)} / {String(g.computerVersion)}</td><td>{g.changed ? fmtDate(g.changed) : '–'}</td></tr>)}</tbody></table>}
    {Array.isArray(p.data.blocked) && p.data.blocked.length > 0 && <Card title="Inheritance blocked"><p className="muted">These places do not receive policies from above:</p><ul>{(p.data.blocked as string[]).map((b) => <li key={b}>{b}</li>)}</ul></Card>}
  </>;
}

/** DNS zones stored in the directory, and the records of one zone. */
export function Dns({ domain }: { domain: Rec }) {
  const z = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/dns`, [domain.id]);
  const [zone, setZone] = useState<Rec | null>(null); const [filter, setFilter] = useState('');
  const r = useLoad<Rec>(zone ? `/api/adsuite/domains/${domain.id}/dns?zone=${encodeURIComponent(String(zone.name))}&source=${zone.source}` : null, [zone?.name, zone?.source]);
  if (!z.data) return <><h1>DNS</h1><Loading loading={z.loading} error={z.error} /></>;
  const zones = (z.data.zones ?? []) as Rec[]; const recs = ((r.data?.records ?? []) as Rec[]).filter((x) => filter === '' || `${x.name} ${x.data} ${x.type}`.toLowerCase().includes(filter.toLowerCase()));
  return <>
    <h1>DNS</h1>
    <p className="muted">Zones that live in Active Directory. Read only.</p>
    {zones.length === 0 ? <Empty>No directory-integrated zones were found. The DNS may be on a separate server.</Empty> : <div className="tools">
      <select aria-label="Zone" value={zone ? `${zone.source}|${zone.name}` : ''} onChange={(e) => setZone(zones.find((x) => `${x.source}|${x.name}` === e.target.value) ?? null)}>
        <option value="">Choose a zone</option>{zones.map((x) => <option key={`${x.source}|${x.name}`} value={`${x.source}|${x.name}`}>{String(x.name)}</option>)}</select>
      {zone && <input aria-label="Filter records" placeholder="Filter records" value={filter} onChange={(e) => setFilter(e.target.value)} />}</div>}
    {zone && <><Loading loading={r.loading} error={r.error} />
      {r.data && (recs.length === 0 ? <Empty>No records.</Empty> : <table><thead><tr><th>Name</th><th>Type</th><th>Data</th><th>TTL</th></tr></thead><tbody>
        {recs.map((x, i) => <tr key={i}><td>{String(x.name)}</td><td>{String(x.type)}</td><td>{String(x.data)}</td><td>{String(x.ttl)}</td></tr>)}</tbody></table>)}
      {r.data?.truncated ? <p className="muted small">Only the first records are shown. Use the filter.</p> : null}</>}
  </>;
}

function Branch({ n, depth }: { n: Rec; depth: number }) {
  const kids = (n.children ?? []) as Rec[]; const [open, setOpen] = useState(depth < 1);
  return <li>
    <div className="ad-node">
      {kids.length > 0 ? <button aria-label={open ? 'Collapse' : 'Expand'} className="ad-twist" onClick={() => setOpen(!open)}>{open ? '▾' : '▸'}</button> : <span className="ad-twist" />}
      <strong>{String(n.name)}</strong>{n.kind === 'container' ? <span className="muted"> (container)</span> : null}{n.linked ? <> <Badge>Policy linked</Badge></> : null}
      <span className="muted small"> · {Number(n.totalUsers)} users · {Number(n.totalComputers)} computers · {Number(n.totalGroups)} groups{kids.length > 0 ? ` (here: ${Number(n.users)}, ${Number(n.computers)}, ${Number(n.groups)})` : ''}</span>
      {n.description ? <div className="muted small">{String(n.description)}</div> : null}
    </div>
    {open && kids.length > 0 && <ul className="ad-tree">{kids.map((k) => <Branch key={String(k.dn)} n={k} depth={depth + 1} />)}</ul>}
  </li>;
}

/** The directory at a glance: OUs with counts, domain controllers and trusts. */
export function DirMap({ domain }: { domain: Rec }) {
  const m = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/map`, [domain.id]);
  if (!m.data) return <><h1>Directory map</h1><Loading loading={m.loading} error={m.error} /></>;
  const dcs = (m.data.dcs ?? []) as Rec[]; const trusts = (m.data.trusts ?? []) as Rec[];
  return <>
    <h1>Directory map</h1>
    <Card title="Structure"><ul className="ad-tree root"><Branch n={m.data.tree as Rec} depth={0} /></ul>
      {m.data.truncated ? <p className="muted small">Counted the first 20,000 objects only.</p> : null}</Card>
    <Card title={`Domain controllers (${dcs.length})`}>{dcs.length === 0 ? <Empty>None found.</Empty> : <ul>{dcs.map((d) => <li key={String(d.dn)}>{String(d.name)}<span className="muted"> · {String(d.os || 'system unknown')}</span></li>)}</ul>}</Card>
    <Card title={`Trusts (${trusts.length})`}>{trusts.length === 0 ? <Empty>No trusts to other domains.</Empty> : <ul>{trusts.map((t, i) => <li key={i}>{String(t.partner)}<span className="muted"> · {String(t.direction)} · {String(t.type)}{t.forest ? ' · forest' : ''}</span></li>)}</ul>}</Card>
  </>;
}
