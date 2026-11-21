import { jsxs as _jsxs, jsx as _jsx, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { api, useLoad } from './http';
import { Card, Empty, Err, Loading, useSubmit } from './ui';
/** Administration › Branding › Your own web address: desk.example.com instead of the built-in address. */
export default function Domains() {
    const { data, error, loading, reload } = useLoad('/api/platform/domains');
    const [host, setHost] = useState('');
    const [msg, setMsg] = useState('');
    const add = useSubmit(() => { setHost(''); reload(); });
    const items = (data?.items ?? []);
    const act = async (fn, done = '') => { setMsg(''); try {
        const r = await fn();
        if (done)
            setMsg(done);
        else if (r && r.detail)
            setMsg(String(r.detail));
        reload();
    }
    catch (e) {
        setMsg(e.message);
    } };
    return _jsxs(Card, { title: "Your own web address", children: [_jsxs("p", { className: "muted", children: ["Let people open DeskAsk at your own address, such as desk.example.com. The built-in address", data?.builtIn ? ` (${data.builtIn})` : '', " keeps working. The certificate is issued automatically once the address is checked."] }), _jsx(Loading, { loading: loading && !data, error: error }), data && (items.length === 0 ? _jsx(Empty, { children: "No address of your own yet." }) : items.map((d) => _jsxs("div", { className: "card", style: { marginBottom: 8 }, children: [_jsxs("p", { children: [_jsx("strong", { children: d.host }), " ", d.verified ? _jsx("span", { className: "pill ok", children: "Checked" }) : _jsx("span", { className: "pill", children: "Not checked yet" }), d.primary && _jsxs(_Fragment, { children: [" ", _jsx("span", { className: "pill", children: "Main address" })] })] }), !d.verified && _jsxs(_Fragment, { children: [_jsx("p", { className: "muted", children: "At your DNS provider, add these two records, wait a few minutes, then press Check." }), _jsx("table", { children: _jsxs("tbody", { children: [_jsxs("tr", { children: [_jsx("td", { children: "TXT" }), _jsx("td", { children: _jsx("code", { children: d.txtName }) }), _jsx("td", { children: _jsx("code", { children: d.txtValue }) })] }), _jsxs("tr", { children: [_jsx("td", { children: "CNAME" }), _jsx("td", { children: _jsx("code", { children: d.host }) }), _jsx("td", { children: _jsx("code", { children: d.cname }) })] })] }) })] }), d.verified && _jsxs("p", { className: "muted small", children: ["If people sign in with Microsoft or Google, add ", _jsx("code", { children: `https://${d.host}/account/sso/microsoft/callback` }), " (and the google one) to that sign-in's allowed redirect addresses. Agents already installed keep working on the old address; new install commands use the address you are signed in at."] }), _jsxs("p", { children: [!d.verified && _jsx("button", { type: "button", className: "primary", onClick: () => void act(() => api(`/api/platform/domains/${encodeURIComponent(d.host)}/verify`, { method: 'POST', body: {} })), children: "Check" }), ' ', d.verified && _jsx("button", { type: "button", onClick: () => void act(() => api(`/api/platform/domains/${encodeURIComponent(d.host)}/primary`, { method: 'POST', body: { primary: !d.primary } }), d.primary ? 'Links now use the built-in address.' : 'Links in emails now use this address.'), children: d.primary ? 'Use the built-in address in links' : 'Use in links and emails' }), ' ', _jsx("button", { type: "button", onClick: () => { if (confirm(`Remove ${d.host}? People can no longer open DeskAsk at it.`))
                                    void act(() => api(`/api/platform/domains/${encodeURIComponent(d.host)}`, { method: 'DELETE' }), 'The address is removed.'); }, children: "Remove" })] })] }, d.host))), msg && _jsx("p", { role: "status", children: msg }), _jsxs("form", { onSubmit: (e) => { e.preventDefault(); void add.submit(() => api('/api/platform/domains', { body: { host: host.trim() } })); }, className: "tools", children: [_jsx("input", { "aria-label": "New web address", placeholder: "desk.example.com", value: host, onChange: (e) => setHost(e.target.value) }), _jsx("button", { type: "submit", disabled: add.busy || !host.trim(), children: "Add address" })] }), _jsx(Err, { text: add.err })] });
}
