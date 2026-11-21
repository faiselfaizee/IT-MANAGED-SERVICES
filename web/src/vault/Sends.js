import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from '../http';
import { ago, copyText, Empty, fmtDate, Loading } from '../ui';
import * as C from './crypto';
/** Makes a send link: the text is encrypted here with a key that only exists in the link's # part. */
export async function makeSend(text, o) {
    const frag = C.random(32);
    const key = await C.sendKey(frag, o.passcode || undefined);
    const r = await api('/api/vault/sends', { method: 'POST', body: { dataEnc: await C.encryptJson(key, { text }), label: o.label, hours: o.hours, maxViews: o.maxViews, passcode: !!o.passcode, passcodeCheck: o.passcode ? await C.sendCheck(frag, o.passcode) : undefined } });
    return `${location.origin}/#/send/${r.id}/${C.b64u(frag)}`;
}
export function SendForm({ initial = '', onMade }) {
    const [text, setText] = useState(initial);
    const [label, setLabel] = useState('');
    const [hours, setHours] = useState(24);
    const [views, setViews] = useState(1);
    const [pass, setPass] = useState('');
    const [link, setLink] = useState('');
    const [err, setErr] = useState('');
    if (link)
        return _jsxs("div", { className: "form", children: [_jsxs("p", { children: ["Send this link. It works ", views === 1 ? 'once' : `${views} times`, " and for ", hours < 24 ? `${hours} hours` : `${hours / 24} days`, "."] }), _jsx("p", { className: "link-out", children: _jsx("code", { children: link }) }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", onClick: () => copyText(link), children: "Copy the link" }), _jsx("button", { onClick: () => { setLink(''); setText(''); setPass(''); }, children: "Make another" })] }), pass && _jsx("p", { className: "note", children: "Tell them the passcode another way (by phone, or in a different app). The link alone does not open it." }), _jsxs("p", { className: "muted small", children: ["The secret part of the link (after the last /) never reaches the server. Anyone who has the link", pass ? ' and the passcode' : '', " can open it, so send it only to the person it is for."] })] });
    return _jsxs("form", { className: "form", onSubmit: async (e) => { e.preventDefault(); setErr(''); try {
            setLink(await makeSend(text, { label, hours, maxViews: views, passcode: pass }));
            onMade?.();
        }
        catch (x) {
            setErr(x.message);
        } }, children: [_jsxs("label", { children: [_jsx("span", { children: "What to send" }), _jsx("textarea", { rows: 5, value: text, onChange: (e) => setText(e.target.value), placeholder: "User name and password for the new starter, a Wi-Fi key, a licence key\u2026", spellCheck: false })] }), _jsxs("label", { children: [_jsx("span", { children: "Label for your list (not secret, not shown to them)" }), _jsx("input", { value: label, onChange: (e) => setLabel(e.target.value), placeholder: "New starter at Acme" })] }), _jsxs("div", { className: "row", children: [_jsxs("label", { children: [_jsx("span", { children: "Works for" }), _jsx("select", { value: hours, onChange: (e) => setHours(Number(e.target.value)), children: [1, 4, 24, 72, 168, 720].map((h) => _jsx("option", { value: h, children: h < 24 ? `${h} hour${h > 1 ? 's' : ''}` : `${h / 24} day${h > 24 ? 's' : ''}` }, h)) })] }), _jsxs("label", { children: [_jsx("span", { children: "Opens" }), _jsx("select", { value: views, onChange: (e) => setViews(Number(e.target.value)), children: [1, 2, 3, 5, 10].map((v) => _jsx("option", { value: v, children: v === 1 ? 'once' : `${v} times` }, v)) })] }), _jsxs("label", { children: [_jsx("span", { children: "Passcode (optional)" }), _jsx("input", { value: pass, onChange: (e) => setPass(e.target.value), placeholder: "Told separately", autoComplete: "off" })] })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: !text.trim(), children: "Make the link" }) })] });
}
export default function Sends() {
    const list = useLoad('/api/vault/sends');
    const items = (list.data?.items ?? []);
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Send links" }), _jsx("p", { className: "muted", children: "For someone outside DeskAsk: a customer's new starter, a contractor, a supplier. The link opens a page that shows the text once, then stops working." }), _jsxs("div", { className: "grid", children: [_jsxs("section", { className: "card", children: [_jsx("h2", { children: "New link" }), _jsx(SendForm, { onMade: list.reload })] }), _jsxs("section", { className: "card", children: [_jsx("h2", { children: "Your links" }), _jsx(Loading, { loading: list.loading && !list.data, error: list.error }), list.data && (items.length === 0 ? _jsx(Empty, { children: "No links yet." }) : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Label" }), _jsx("th", { children: "Made" }), _jsx("th", { children: "Opened" }), _jsx("th", { children: "State" }), _jsx("th", {})] }) }), _jsx("tbody", { children: items.map((s) => _jsxs("tr", { children: [_jsxs("td", { children: [s.label || _jsx("span", { className: "muted", children: "No label" }), _jsxs("div", { className: "muted small", children: [s.createdBy, s.passcode ? ' · with passcode' : ''] })] }), _jsx("td", { title: fmtDate(s.createdAt), children: ago(s.createdAt) }), _jsxs("td", { children: [s.views, " of ", s.maxViews] }), _jsx("td", { children: _jsx("span", { className: 'badge ' + (s.active ? 'ok' : ''), children: s.active ? `Works until ${fmtDate(s.expiresAt)}` : s.deletedAt ? 'Deleted' : s.views >= s.maxViews ? 'Used up' : 'Expired' }) }), _jsx("td", { children: s.active && _jsx("button", { className: "small", onClick: async () => { await api(`/api/vault/sends/${s.id}`, { method: 'DELETE' }); list.reload(); }, children: "Stop it now" }) })] }, s.id)) })] }) }))] })] })] });
}
