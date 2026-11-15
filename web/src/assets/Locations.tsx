import { useEffect, useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Badge, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';
import type { Place } from './Purchase';

const LEVEL: Record<string, string> = { country: 'Country', state: 'State or region', site: 'Site' };

/** A customer's countries, states and sites. Administrators manage every customer's; a customer administrator manages their own. */
export default function Locations() {
  const companies = useLoad<Rec[]>('/api/assets/companies'); const cos = Array.isArray(companies.data) ? companies.data : [];
  const [co, setCo] = useState<number | null>(null);
  useEffect(() => { if (co == null && cos.length) setCo(Number(new URLSearchParams(location.hash.split('?')[1] ?? '').get('company')) || cos[0].id); }, [cos, co]);
  const all = useLoad<Rec>(co ? `/api/assets/places?company=${co}` : null, [co]);
  const places = (all.data?.items ?? []) as Place[]; const canEdit = places.length ? places.some((p) => p.canEdit) : !!all.data?.manageAll || ((all.data?.manageCompanies ?? []) as number[]).includes(co ?? -1);
  const [edit, setEdit] = useState<{ place?: Place; level: string; parentId: number | null } | null>(null);
  const [removing, setRemoving] = useState<Place | null>(null); const [err, setErr] = useState('');
  const kids = (id: number | null, level?: string) => places.filter((p) => p.parentId === id && (!level || p.level === level)).sort((a, b) => a.name.localeCompare(b.name));
  const assetsUnder = (p: Place): number => (p.level === 'site' ? Number(p.assets) : kids(p.id).reduce((n, k) => n + assetsUnder(k), 0));
  const sitesUnder = (p: Place): number => (p.level === 'site' ? 1 : kids(p.id).reduce((n, k) => n + sitesUnder(k), 0));
  const remove = async (p: Place) => {
    setErr('');
    try { await api(`/api/assets/places/${p.id}`, { method: 'DELETE' }); all.reload(); }
    catch (e) { if (p.level === 'site' && Number(p.assets) > 0) setRemoving(p); else setErr((e as Error).message); }
  };
  const Row = ({ p }: { p: Place }) => <li className={'loc ' + p.level + (p.active === false ? ' off' : '')}>
    <div className="loc-row">
      <span className="loc-name">{p.name}{p.code && <span className="mono muted"> {p.code}</span>}{p.active === false && <> <Badge>inactive</Badge></>}</span>
      <span className="muted small">{p.level === 'site' ? [p.city, p.contactName].filter(Boolean).join(' · ') : `${sitesUnder(p)} site${sitesUnder(p) === 1 ? '' : 's'}`}</span>
      <a className="small" href={`#/assets/list?company=${co}&place=${p.id}`}>{assetsUnder(p)} asset{assetsUnder(p) === 1 ? '' : 's'}</a>
      {canEdit && <span className="loc-acts">
        {p.level === 'country' && <button className="small" onClick={() => setEdit({ level: 'state', parentId: p.id })}>Add state</button>}
        {p.level !== 'site' && <button className="small" onClick={() => setEdit({ level: 'site', parentId: p.id })}>Add site</button>}
        <button className="small" onClick={() => setEdit({ place: p, level: p.level, parentId: p.parentId })}>Edit</button>
        <button className="small danger" onClick={() => { if (window.confirm(`Delete ${p.name}?`)) void remove(p); }}>Delete</button></span>}
    </div>
    {p.level !== 'site' && kids(p.id).length > 0 && <ul>{kids(p.id, 'site').concat(kids(p.id, 'state')).map((k) => <Row key={k.id} p={k} />)}</ul>}
  </li>;
  return <>
    <h1>Locations</h1>
    <p className="muted">Each customer's countries, states or regions, and sites with their site codes. Assets are placed at a site.</p>
    <div className="tools">
      {cos.length > 1 && <select value={co ?? ''} onChange={(e) => setCo(Number(e.target.value))} aria-label="Customer">{cos.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}</select>}
      {cos.length === 1 && <strong>{cos[0].name}</strong>}
      {canEdit && co && <button className="primary" onClick={() => setEdit({ level: 'country', parentId: null })}>Add country</button>}
    </div>
    <Err text={err} />
    <Loading loading={(companies.loading && !companies.data) || (all.loading && !all.data)} error={companies.error || all.error} />
    {companies.data && cos.length === 0 && <Empty>No customers yet. Add one under Companies.</Empty>}
    {all.data && (places.length === 0 ? <Empty>{canEdit ? 'No locations yet. Start with a country, then add states and sites.' : 'No locations yet.'}</Empty>
      : <ul className="loc-tree">{kids(null).map((c) => <Row key={c.id} p={c} />)}</ul>)}
    {edit && co && <PlaceForm companyId={co} places={places} init={edit} onClose={(ch) => { setEdit(null); if (ch) all.reload(); }} />}
    {removing && <MoveAndDelete site={removing} places={places} onClose={(ch) => { setRemoving(null); if (ch) all.reload(); }} />}
  </>;
}

function PlaceForm({ companyId, places, init, onClose }: { companyId: number; places: Place[]; init: { place?: Place; level: string; parentId: number | null }; onClose: (changed: boolean) => void }) {
  const p = init.place;
  const [v, setV] = useState<Rec>({ name: p?.name ?? '', code: p?.code ?? '', address: p?.address ?? '', city: p?.city ?? '', postalCode: p?.postalCode ?? '', contactName: p?.contactName ?? '',
    contactEmail: p?.contactEmail ?? '', contactPhone: p?.contactPhone ?? '', notes: p?.notes ?? '', active: p?.active !== false, parentId: init.parentId });
  const f = useSubmit(() => onClose(true)); const set = (k: string, x: unknown) => setV({ ...v, [k]: x });
  const site = init.level === 'site';
  const parents = places.filter((x) => (init.level === 'state' ? x.level === 'country' : site ? x.level !== 'site' : false));
  const parentName = (x: Place) => (x.level === 'state' ? `${x.name}, ${places.find((c) => c.id === x.parentId)?.name ?? ''}` : x.name);
  return <Modal title={`${p ? 'Edit' : 'Add'} ${LEVEL[init.level].toLowerCase()}`} onClose={() => onClose(false)}><div className="form">
    <div className="row"><Field label="Name"><input value={v.name} onChange={(e) => set('name', e.target.value)} autoFocus placeholder={site ? 'Bengaluru head office' : init.level === 'state' ? 'Karnataka' : 'India'} /></Field>
      <Field label={site ? 'Site code' : 'Code (optional)'}><input value={v.code} onChange={(e) => set('code', e.target.value.toUpperCase())} placeholder={site ? 'BLR01' : init.level === 'state' ? 'KA' : 'IN'} /></Field></div>
    {init.level !== 'country' && <Field label={site ? 'In (country or state)' : 'In country'}><select value={v.parentId ?? ''} onChange={(e) => set('parentId', Number(e.target.value))}>{parents.map((x) => <option key={x.id} value={x.id}>{parentName(x)}</option>)}</select></Field>}
    {site && <>
      <Field label="Address"><textarea rows={2} value={v.address} onChange={(e) => set('address', e.target.value)} /></Field>
      <div className="row"><Field label="City"><input value={v.city} onChange={(e) => set('city', e.target.value)} /></Field><Field label="Postal code"><input value={v.postalCode} onChange={(e) => set('postalCode', e.target.value)} /></Field></div>
      <div className="row"><Field label="Site contact"><input value={v.contactName} onChange={(e) => set('contactName', e.target.value)} /></Field><Field label="Contact email"><input value={v.contactEmail} onChange={(e) => set('contactEmail', e.target.value)} /></Field><Field label="Phone"><input value={v.contactPhone} onChange={(e) => set('contactPhone', e.target.value)} /></Field></div>
      {p && <label className="check"><input type="checkbox" checked={v.active} onChange={(e) => set('active', e.target.checked)} /> In use (an inactive site keeps its assets but is not offered for new ones)</label>}
    </>}
    <Field label="Notes"><textarea rows={2} value={v.notes} onChange={(e) => set('notes', e.target.value)} /></Field>
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary" disabled={f.busy || !String(v.name).trim()} onClick={() => f.submit(() => p
        ? api(`/api/assets/places/${p.id}`, { method: 'PUT', body: v })
        : api('/api/assets/places', { method: 'POST', body: { ...v, companyId, level: init.level } }))}>Save</button></div></div></Modal>;
}

function MoveAndDelete({ site, places, onClose }: { site: Place; places: Place[]; onClose: (changed: boolean) => void }) {
  const others = places.filter((p) => p.level === 'site' && p.id !== site.id);
  const [to, setTo] = useState(others[0]?.id ?? 0); const f = useSubmit(() => onClose(true));
  return <Modal title={`Delete ${site.name}`} onClose={() => onClose(false)}><div className="form">
    <p>{site.assets} asset{Number(site.assets) === 1 ? ' is' : 's are'} at this site. Move {Number(site.assets) === 1 ? 'it' : 'them'} to another site first, or keep the site and mark it inactive.</p>
    {others.length === 0 ? <Empty>This customer has no other site.</Empty> : <Field label="Move to"><select value={to} onChange={(e) => setTo(Number(e.target.value))}>{others.map((p) => <option key={p.id} value={p.id}>{p.name}{p.code ? ` (${p.code})` : ''}</option>)}</select></Field>}
    <Err text={f.err} /><div className="actions"><button onClick={() => onClose(false)}>Cancel</button>
      <button className="primary danger" disabled={f.busy || !others.length} onClick={() => f.submit(async () => { await api(`/api/assets/places/${site.id}/move`, { body: { toSiteId: to } }); await api(`/api/assets/places/${site.id}`, { method: 'DELETE' }); })}>Move the assets and delete</button></div></div></Modal>;
}
