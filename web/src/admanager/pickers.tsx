import { useEffect, useRef, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Empty, Loading, Modal } from '../ui';

/** "OU=Sales,OU=Staff,DC=acme,DC=test" → "Staff › Sales", the way people read it. */
export function ouPath(dn: string, baseDn?: string): string {
  if (!dn) return '';
  const rest = baseDn && dn.toLowerCase().endsWith(baseDn.toLowerCase()) ? dn.slice(0, dn.length - baseDn.length).replace(/,$/, '') : dn;
  if (!rest) return 'Whole domain';
  return rest.split(/,(?=(?:OU|CN|DC)=)/i).map((p) => p.replace(/^[A-Za-z]+=/, '').replace(/\\(.)/g, '$1')).reverse().join(' › ');
}
export const rdn = (dn: string) => (dn.split(/,(?=(?:OU|CN|DC)=)/i)[0] ?? '').replace(/^[A-Za-z]+=/, '').replace(/\\(.)/g, '$1');

/** Choose an OU from the directory: a tree that opens one level at a time. */
export function OuPicker({ domain, value, onChange, label = 'OU', allowDomain }: { domain: Rec; value: string; onChange: (dn: string) => void; label?: string; allowDomain?: boolean }) {
  const [open, setOpen] = useState(false);
  return <div className="picker">
    <button type="button" className="pick" aria-label={label} onClick={() => setOpen(true)} title={value}>
      <span>{value ? ouPath(value, domain.baseDn) : 'Choose an OU…'}</span><i aria-hidden>▾</i></button>
    {open && <OuTree domain={domain} start={value} allowDomain={allowDomain} onPick={(dn) => { onChange(dn); setOpen(false); }} onClose={() => setOpen(false)} />}
  </div>;
}

function OuTree({ domain, start, onPick, onClose, allowDomain }: { domain: Rec; start: string; onPick: (dn: string) => void; onClose: () => void; allowDomain?: boolean }) {
  const [base, setBase] = useState<string>(start && start.toLowerCase() !== String(domain.baseDn).toLowerCase() ? parentOf(start) : '');
  const t = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/tree?base=${encodeURIComponent(base)}`, [base]);
  const here = String(t.data?.baseDn || base || domain.baseDn);
  const items = (t.data?.items ?? []) as Rec[];
  const atTop = here.toLowerCase() === String(domain.baseDn).toLowerCase() || !!domain.scoped && !base;
  return <Modal title="Choose an OU" onClose={onClose}>
    <p className="crumbs"><code>{ouPath(here, domain.baseDn)}</code></p>
    {!atTop && <p><button className="link" onClick={() => setBase(parentOf(here))}>↑ Up one level</button></p>}
    <Loading loading={t.loading && !t.data} error={t.error} />
    {t.data && (items.length === 0 ? <Empty>No OUs below this one.</Empty> :
      <ul className="tree">{items.map((i) => <li key={String(i.dn)}>
        <button className="link" onClick={() => setBase(String(i.dn))} title="Open">📁 {String(i.name)}</button>
        <button className="small" onClick={() => onPick(String(i.dn))}>Choose</button></li>)}</ul>)}
    <div className="actions">
      {(!atTop || allowDomain) && <button className="primary" onClick={() => onPick(here)}>Choose “{atTop ? 'Whole domain' : rdn(here)}”</button>}
      <button onClick={onClose}>Cancel</button></div>
  </Modal>;
}
const parentOf = (dn: string) => dn.replace(/^(?:[^,\\]|\\.)*,/, '');

type Hit = { dn: string; name: string; sub?: string };

/** Type a few letters, pick from the directory. multiple: chips; otherwise one value. */
export function ObjectPicker({ domain, kind, value, onChange, multiple, placeholder, label }: {
  domain: Rec; kind: 'user' | 'group' | 'computer' | 'all'; value: Hit[]; onChange: (v: Hit[]) => void; multiple?: boolean; placeholder?: string; label?: string;
}) {
  const [q, setQ] = useState(''); const [hits, setHits] = useState<Hit[]>([]); const [busy, setBusy] = useState(false); const [openList, setOpenList] = useState(false);
  const timer = useRef<number | undefined>(undefined);
  useEffect(() => {
    window.clearTimeout(timer.current);
    if (q.trim().length < 2) { setHits([]); return; }
    timer.current = window.setTimeout(async () => {
      setBusy(true);
      try {
        const r = await api<Rec>(`/api/adsuite/domains/${domain.id}/search?kind=${kind}&q=${encodeURIComponent(q.trim())}&limit=20`);
        setHits(((r.items ?? []) as Rec[]).map((i) => ({ dn: String(i.dn), name: String(i.name), sub: String(i.sam || i.mail || ouPath(String(i.dn).replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn)) })));
        setOpenList(true);
      } catch { setHits([]); } finally { setBusy(false); }
    }, 300);
    return () => window.clearTimeout(timer.current);
  }, [q, domain.id, kind]);
  const add = (h: Hit) => { onChange(multiple ? (value.some((v) => v.dn === h.dn) ? value : [...value, h]) : [h]); setQ(''); setHits([]); setOpenList(false); };
  return <div className="objpick">
    {value.length > 0 && <div className="chips">{value.map((v) => <span key={v.dn} className="chip" title={v.dn}>{v.name}<button type="button" aria-label={'Remove ' + v.name} onClick={() => onChange(value.filter((x) => x.dn !== v.dn))}>×</button></span>)}</div>}
    {(multiple || value.length === 0) && <div className="combo">
      <input aria-label={label ?? 'Search'} value={q} placeholder={placeholder ?? 'Type 2 letters to search…'} onChange={(e) => setQ(e.target.value)} onFocus={() => hits.length && setOpenList(true)} onBlur={() => window.setTimeout(() => setOpenList(false), 200)} />
      {busy && <span className="muted small">Searching…</span>}
      {openList && hits.length > 0 && <ul className="suggest" role="listbox">{hits.map((h) => <li key={h.dn} role="option" aria-selected="false" onMouseDown={(e) => { e.preventDefault(); add(h); }}>
        <strong>{h.name}</strong> <span className="muted small">{h.sub}</span></li>)}</ul>}
      {openList && !busy && q.trim().length >= 2 && hits.length === 0 && <ul className="suggest"><li className="muted">Nothing found.</li></ul>}
    </div>}
  </div>;
}
