import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useEffect, useRef, useState } from 'react';
import { api, useLoad } from '../http';
import { Empty, Loading, Modal } from '../ui';
/** "OU=Sales,OU=Staff,DC=acme,DC=test" → "Staff › Sales", the way people read it. */
export function ouPath(dn, baseDn) {
    if (!dn)
        return '';
    const rest = baseDn && dn.toLowerCase().endsWith(baseDn.toLowerCase()) ? dn.slice(0, dn.length - baseDn.length).replace(/,$/, '') : dn;
    if (!rest)
        return 'Whole domain';
    return rest.split(/,(?=(?:OU|CN|DC)=)/i).map((p) => p.replace(/^[A-Za-z]+=/, '').replace(/\\(.)/g, '$1')).reverse().join(' › ');
}
export const rdn = (dn) => (dn.split(/,(?=(?:OU|CN|DC)=)/i)[0] ?? '').replace(/^[A-Za-z]+=/, '').replace(/\\(.)/g, '$1');
/** Choose an OU from the directory: a tree that opens one level at a time. */
export function OuPicker({ domain, value, onChange, label = 'OU', allowDomain }) {
    const [open, setOpen] = useState(false);
    return _jsxs("div", { className: "picker", children: [_jsxs("button", { type: "button", className: "pick", "aria-label": label, onClick: () => setOpen(true), title: value, children: [_jsx("span", { children: value ? ouPath(value, domain.baseDn) : 'Choose an OU…' }), _jsx("i", { "aria-hidden": true, children: "\u25BE" })] }), open && _jsx(OuTree, { domain: domain, start: value, allowDomain: allowDomain, onPick: (dn) => { onChange(dn); setOpen(false); }, onClose: () => setOpen(false) })] });
}
function OuTree({ domain, start, onPick, onClose, allowDomain }) {
    const [base, setBase] = useState(start && start.toLowerCase() !== String(domain.baseDn).toLowerCase() ? parentOf(start) : '');
    const t = useLoad(`/api/adsuite/domains/${domain.id}/tree?base=${encodeURIComponent(base)}`, [base]);
    const here = String(t.data?.baseDn || base || domain.baseDn);
    const items = (t.data?.items ?? []);
    const atTop = here.toLowerCase() === String(domain.baseDn).toLowerCase() || !!domain.scoped && !base;
    return _jsxs(Modal, { title: "Choose an OU", onClose: onClose, children: [_jsx("p", { className: "crumbs", children: _jsx("code", { children: ouPath(here, domain.baseDn) }) }), !atTop && _jsx("p", { children: _jsx("button", { className: "link", onClick: () => setBase(parentOf(here)), children: "\u2191 Up one level" }) }), _jsx(Loading, { loading: t.loading && !t.data, error: t.error }), t.data && (items.length === 0 ? _jsx(Empty, { children: "No OUs below this one." }) :
                _jsx("ul", { className: "tree", children: items.map((i) => _jsxs("li", { children: [_jsxs("button", { className: "link", onClick: () => setBase(String(i.dn)), title: "Open", children: ["\uD83D\uDCC1 ", String(i.name)] }), _jsx("button", { className: "small", onClick: () => onPick(String(i.dn)), children: "Choose" })] }, String(i.dn))) })), _jsxs("div", { className: "actions", children: [(!atTop || allowDomain) && _jsxs("button", { className: "primary", onClick: () => onPick(here), children: ["Choose \u201C", atTop ? 'Whole domain' : rdn(here), "\u201D"] }), _jsx("button", { onClick: onClose, children: "Cancel" })] })] });
}
const parentOf = (dn) => dn.replace(/^(?:[^,\\]|\\.)*,/, '');
/** Type a few letters, pick from the directory. multiple: chips; otherwise one value. */
export function ObjectPicker({ domain, kind, value, onChange, multiple, placeholder, label }) {
    const [q, setQ] = useState('');
    const [hits, setHits] = useState([]);
    const [busy, setBusy] = useState(false);
    const [openList, setOpenList] = useState(false);
    const timer = useRef(undefined);
    useEffect(() => {
        window.clearTimeout(timer.current);
        if (q.trim().length < 2) {
            setHits([]);
            return;
        }
        timer.current = window.setTimeout(async () => {
            setBusy(true);
            try {
                const r = await api(`/api/adsuite/domains/${domain.id}/search?kind=${kind}&q=${encodeURIComponent(q.trim())}&limit=20`);
                setHits((r.items ?? []).map((i) => ({ dn: String(i.dn), name: String(i.name), sub: String(i.sam || i.mail || ouPath(String(i.dn).replace(/^(?:[^,\\]|\\.)*,/, ''), domain.baseDn)) })));
                setOpenList(true);
            }
            catch {
                setHits([]);
            }
            finally {
                setBusy(false);
            }
        }, 300);
        return () => window.clearTimeout(timer.current);
    }, [q, domain.id, kind]);
    const add = (h) => { onChange(multiple ? (value.some((v) => v.dn === h.dn) ? value : [...value, h]) : [h]); setQ(''); setHits([]); setOpenList(false); };
    return _jsxs("div", { className: "objpick", children: [value.length > 0 && _jsx("div", { className: "chips", children: value.map((v) => _jsxs("span", { className: "chip", title: v.dn, children: [v.name, _jsx("button", { type: "button", "aria-label": 'Remove ' + v.name, onClick: () => onChange(value.filter((x) => x.dn !== v.dn)), children: "\u00D7" })] }, v.dn)) }), (multiple || value.length === 0) && _jsxs("div", { className: "combo", children: [_jsx("input", { "aria-label": label ?? 'Search', value: q, placeholder: placeholder ?? 'Type 2 letters to search…', onChange: (e) => setQ(e.target.value), onFocus: () => hits.length && setOpenList(true), onBlur: () => window.setTimeout(() => setOpenList(false), 200) }), busy && _jsx("span", { className: "muted small", children: "Searching\u2026" }), openList && hits.length > 0 && _jsx("ul", { className: "suggest", role: "listbox", children: hits.map((h) => _jsxs("li", { role: "option", "aria-selected": "false", onMouseDown: (e) => { e.preventDefault(); add(h); }, children: [_jsx("strong", { children: h.name }), " ", _jsx("span", { className: "muted small", children: h.sub })] }, h.dn)) }), openList && !busy && q.trim().length >= 2 && hits.length === 0 && _jsx("ul", { className: "suggest", children: _jsx("li", { className: "muted", children: "Nothing found." }) })] })] });
}
