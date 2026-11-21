import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useLoad } from '../http';
import { Badge, Empty, Err, Field, Loading, Modal, useSubmit } from '../ui';
const LEVEL = { country: 'Country', state: 'State or region', site: 'Site' };
/** A customer's countries, states and sites. Administrators manage every customer's; a customer administrator manages their own. */
export default function Locations() {
    const companies = useLoad('/api/assets/companies');
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const [co, setCo] = useState(null);
    useEffect(() => { if (co == null && cos.length)
        setCo(Number(new URLSearchParams(location.hash.split('?')[1] ?? '').get('company')) || cos[0].id); }, [cos, co]);
    const all = useLoad(co ? `/api/assets/places?company=${co}` : null, [co]);
    const places = (all.data?.items ?? []);
    const canEdit = places.length ? places.some((p) => p.canEdit) : !!all.data?.manageAll || (all.data?.manageCompanies ?? []).includes(co ?? -1);
    const [edit, setEdit] = useState(null);
    const [removing, setRemoving] = useState(null);
    const [err, setErr] = useState('');
    const kids = (id, level) => places.filter((p) => p.parentId === id && (!level || p.level === level)).sort((a, b) => a.name.localeCompare(b.name));
    const assetsUnder = (p) => (p.level === 'site' ? Number(p.assets) : kids(p.id).reduce((n, k) => n + assetsUnder(k), 0));
    const sitesUnder = (p) => (p.level === 'site' ? 1 : kids(p.id).reduce((n, k) => n + sitesUnder(k), 0));
    const remove = async (p) => {
        setErr('');
        try {
            await api(`/api/assets/places/${p.id}`, { method: 'DELETE' });
            all.reload();
        }
        catch (e) {
            if (p.level === 'site' && Number(p.assets) > 0)
                setRemoving(p);
            else
                setErr(e.message);
        }
    };
    const Row = ({ p }) => _jsxs("li", { className: 'loc ' + p.level + (p.active === false ? ' off' : ''), children: [_jsxs("div", { className: "loc-row", children: [_jsxs("span", { className: "loc-name", children: [p.name, p.code && _jsxs("span", { className: "mono muted", children: [" ", p.code] }), p.active === false && _jsxs(_Fragment, { children: [" ", _jsx(Badge, { children: "inactive" })] })] }), _jsx("span", { className: "muted small", children: p.level === 'site' ? [p.city, p.contactName].filter(Boolean).join(' · ') : `${sitesUnder(p)} site${sitesUnder(p) === 1 ? '' : 's'}` }), _jsxs("a", { className: "small", href: `#/assets/list?company=${co}&place=${p.id}`, children: [assetsUnder(p), " asset", assetsUnder(p) === 1 ? '' : 's'] }), canEdit && _jsxs("span", { className: "loc-acts", children: [p.level === 'country' && _jsx("button", { className: "small", onClick: () => setEdit({ level: 'state', parentId: p.id }), children: "Add state" }), p.level !== 'site' && _jsx("button", { className: "small", onClick: () => setEdit({ level: 'site', parentId: p.id }), children: "Add site" }), _jsx("button", { className: "small", onClick: () => setEdit({ place: p, level: p.level, parentId: p.parentId }), children: "Edit" }), _jsx("button", { className: "small danger", onClick: () => { if (window.confirm(`Delete ${p.name}?`))
                                    void remove(p); }, children: "Delete" })] })] }), p.level !== 'site' && kids(p.id).length > 0 && _jsx("ul", { children: kids(p.id, 'site').concat(kids(p.id, 'state')).map((k) => _jsx(Row, { p: k }, k.id)) })] });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Locations" }), _jsx("p", { className: "muted", children: "Each customer's countries, states or regions, and sites with their site codes. Assets are placed at a site." }), _jsxs("div", { className: "tools", children: [cos.length > 1 && _jsx("select", { value: co ?? '', onChange: (e) => setCo(Number(e.target.value)), "aria-label": "Customer", children: cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id)) }), cos.length === 1 && _jsx("strong", { children: cos[0].name }), canEdit && co && _jsx("button", { className: "primary", onClick: () => setEdit({ level: 'country', parentId: null }), children: "Add country" })] }), _jsx(Err, { text: err }), _jsx(Loading, { loading: (companies.loading && !companies.data) || (all.loading && !all.data), error: companies.error || all.error }), companies.data && cos.length === 0 && _jsx(Empty, { children: "No customers yet. Add one under Companies." }), all.data && (places.length === 0 ? _jsx(Empty, { children: canEdit ? 'No locations yet. Start with a country, then add states and sites.' : 'No locations yet.' })
                : _jsx("ul", { className: "loc-tree", children: kids(null).map((c) => _jsx(Row, { p: c }, c.id)) })), edit && co && _jsx(PlaceForm, { companyId: co, places: places, init: edit, onClose: (ch) => { setEdit(null); if (ch)
                    all.reload(); } }), removing && _jsx(MoveAndDelete, { site: removing, places: places, onClose: (ch) => { setRemoving(null); if (ch)
                    all.reload(); } })] });
}
function PlaceForm({ companyId, places, init, onClose }) {
    const p = init.place;
    const [v, setV] = useState({ name: p?.name ?? '', code: p?.code ?? '', address: p?.address ?? '', city: p?.city ?? '', postalCode: p?.postalCode ?? '', contactName: p?.contactName ?? '',
        contactEmail: p?.contactEmail ?? '', contactPhone: p?.contactPhone ?? '', notes: p?.notes ?? '', active: p?.active !== false, parentId: init.parentId });
    const f = useSubmit(() => onClose(true));
    const set = (k, x) => setV({ ...v, [k]: x });
    const site = init.level === 'site';
    const parents = places.filter((x) => (init.level === 'state' ? x.level === 'country' : site ? x.level !== 'site' : false));
    const parentName = (x) => (x.level === 'state' ? `${x.name}, ${places.find((c) => c.id === x.parentId)?.name ?? ''}` : x.name);
    return _jsx(Modal, { title: `${p ? 'Edit' : 'Add'} ${LEVEL[init.level].toLowerCase()}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("div", { className: "row", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: v.name, onChange: (e) => set('name', e.target.value), autoFocus: true, placeholder: site ? 'Bengaluru head office' : init.level === 'state' ? 'Karnataka' : 'India' }) }), _jsx(Field, { label: site ? 'Site code' : 'Code (optional)', children: _jsx("input", { value: v.code, onChange: (e) => set('code', e.target.value.toUpperCase()), placeholder: site ? 'BLR01' : init.level === 'state' ? 'KA' : 'IN' }) })] }), init.level !== 'country' && _jsx(Field, { label: site ? 'In (country or state)' : 'In country', children: _jsx("select", { value: v.parentId ?? '', onChange: (e) => set('parentId', Number(e.target.value)), children: parents.map((x) => _jsx("option", { value: x.id, children: parentName(x) }, x.id)) }) }), site && _jsxs(_Fragment, { children: [_jsx(Field, { label: "Address", children: _jsx("textarea", { rows: 2, value: v.address, onChange: (e) => set('address', e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "City", children: _jsx("input", { value: v.city, onChange: (e) => set('city', e.target.value) }) }), _jsx(Field, { label: "Postal code", children: _jsx("input", { value: v.postalCode, onChange: (e) => set('postalCode', e.target.value) }) })] }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Site contact", children: _jsx("input", { value: v.contactName, onChange: (e) => set('contactName', e.target.value) }) }), _jsx(Field, { label: "Contact email", children: _jsx("input", { value: v.contactEmail, onChange: (e) => set('contactEmail', e.target.value) }) }), _jsx(Field, { label: "Phone", children: _jsx("input", { value: v.contactPhone, onChange: (e) => set('contactPhone', e.target.value) }) })] }), p && _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: v.active, onChange: (e) => set('active', e.target.checked) }), " In use (an inactive site keeps its assets but is not offered for new ones)"] })] }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: v.notes, onChange: (e) => set('notes', e.target.value) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary", disabled: f.busy || !String(v.name).trim(), onClick: () => f.submit(() => p
                                ? api(`/api/assets/places/${p.id}`, { method: 'PUT', body: v })
                                : api('/api/assets/places', { method: 'POST', body: { ...v, companyId, level: init.level } })), children: "Save" })] })] }) });
}
function MoveAndDelete({ site, places, onClose }) {
    const others = places.filter((p) => p.level === 'site' && p.id !== site.id);
    const [to, setTo] = useState(others[0]?.id ?? 0);
    const f = useSubmit(() => onClose(true));
    return _jsx(Modal, { title: `Delete ${site.name}`, onClose: () => onClose(false), children: _jsxs("div", { className: "form", children: [_jsxs("p", { children: [site.assets, " asset", Number(site.assets) === 1 ? ' is' : 's are', " at this site. Move ", Number(site.assets) === 1 ? 'it' : 'them', " to another site first, or keep the site and mark it inactive."] }), others.length === 0 ? _jsx(Empty, { children: "This customer has no other site." }) : _jsx(Field, { label: "Move to", children: _jsx("select", { value: to, onChange: (e) => setTo(Number(e.target.value)), children: others.map((p) => _jsxs("option", { value: p.id, children: [p.name, p.code ? ` (${p.code})` : ''] }, p.id)) }) }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(false), children: "Cancel" }), _jsx("button", { className: "primary danger", disabled: f.busy || !others.length, onClick: () => f.submit(async () => { await api(`/api/assets/places/${site.id}/move`, { body: { toSiteId: to } }); await api(`/api/assets/places/${site.id}`, { method: 'DELETE' }); }), children: "Move the assets and delete" })] })] }) });
}
