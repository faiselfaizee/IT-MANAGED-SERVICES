import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useInterval, useLoad } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { Fields } from './Items';
import { typeDef } from './types';
const left = (s) => { const m = Math.max(0, Math.round((new Date(s).getTime() - Date.now()) / 60000)); return m >= 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m} min`; };
function Waiting({ r, title, onDone }) {
    const [minutes, setMinutes] = useState(Number(r.minutes));
    const [note, setNote] = useState('');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const run = async (fn) => { setBusy(true); setErr(''); try {
        await fn();
        onDone();
    }
    catch (e) {
        setErr(e.message);
    }
    finally {
        setBusy(false);
    } };
    return _jsxs("li", { className: "req", children: [_jsxs("div", { children: [_jsx("strong", { children: r.user }), " asks for ", _jsx("strong", { children: title }), " ", _jsxs("span", { className: "muted", children: ["(", typeDef(r.type).label, ", ", r.vault, ")"] }), " for ", r.minutes, " minutes, ", ago(r.at), _jsxs("div", { className: "said", children: ["\u201C", r.reason, "\u201D", r.ticket ? _jsxs(_Fragment, { children: [" \u00B7 ticket ", _jsx("a", { href: `#/servicedesk?search=${encodeURIComponent(r.ticket)}`, children: r.ticket })] }) : null] })] }), !r.publicKey ? _jsx("p", { className: "muted", children: "They have not set up their vault, so nothing can be shared with them yet." }) : _jsxs("div", { className: "tools", children: [_jsx("select", { value: minutes, onChange: (e) => setMinutes(Number(e.target.value)), "aria-label": "Allow for", children: [15, 30, 60, 120, 240, 480, 1440].filter((m) => m <= Number(r.minutes)).map((m) => _jsx("option", { value: m, children: m < 60 ? `${m} min` : `${m / 60} h` }, m)) }), _jsx("input", { value: note, onChange: (e) => setNote(e.target.value), placeholder: "Note to them (optional)", "aria-label": "Note" }), _jsx("button", { className: "primary", disabled: busy, onClick: () => void run(async () => {
                            // encrypt the item once more, for the requester only
                            const it = await api(`/api/vault/items/${r.itemId}`);
                            const data = (await S.data(it)) ?? {};
                            const dataEnc = await C.sealFor(String(r.publicKey), { title, type: it.type, data });
                            await api(`/api/vault/requests/${r.id}/approve`, { method: 'POST', body: { dataEnc, minutes, note } });
                        }), children: "Approve" }), _jsx("button", { disabled: busy, onClick: () => void run(() => api(`/api/vault/requests/${r.id}/deny`, { method: 'POST', body: { note } })), children: "Deny" })] }), r.managed && _jsx("p", { className: "muted small", children: "A managed account: the password itself is read through DeskAsk while the approval lasts." }), err && _jsx("p", { className: "err", role: "alert", children: err })] });
}
function Grant({ g, onDone }) {
    const [open, setOpen] = useState(null);
    const [pw, setPw] = useState('');
    const [err, setErr] = useState('');
    return _jsxs("li", { className: "req", children: [_jsxs("div", { children: [_jsx("strong", { children: open ? String(open.title) : 'An item' }), " ", _jsxs("span", { className: "muted", children: ["(", typeDef(g.type).label, ") \u00B7 approved by ", g.grantedBy, " \u00B7 ", left(g.expiresAt), " left"] })] }), open ? _jsxs(_Fragment, { children: [_jsx(Fields, { type: String(open.type), data: open.data, itemId: g.itemId }), g.managed && (pw ? _jsxs("p", { children: ["Password: ", _jsx("code", { children: pw })] }) : _jsx("button", { onClick: async () => { try {
                            setPw(String((await api(`/api/vault/items/${g.itemId}/secret`)).password));
                        }
                        catch (e) {
                            setErr(e.message);
                        } }, children: "Show the password" }))] })
                : _jsx("button", { className: "primary", onClick: async () => { try {
                        setOpen(await C.openSealed(S.key(), g.dataEnc));
                        S.note(g.itemId, 'viewed', 'approved request');
                    }
                    catch (e) {
                        setErr(e.message);
                    } }, children: "Open" }), _jsx("button", { onClick: async () => { await api(`/api/vault/grants/${g.id}`, { method: 'DELETE' }); onDone(); }, children: "I am done" }), err && _jsx("p", { className: "err", role: "alert", children: err })] });
}
export default function Requests() {
    const approve = useLoad('/api/vault/requests?scope=approve');
    const mine = useLoad('/api/vault/requests');
    const grants = useLoad('/api/vault/grants');
    useInterval(() => { approve.reload(); grants.reload(); }, 30_000);
    const wait = (approve.data?.items ?? []);
    const my = (mine.data?.items ?? []);
    const gs = (grants.data?.items ?? []);
    const titles = S.useTitles([...wait, ...my].map((r) => ({ id: Number(r.itemId), vaultId: Number(r.vaultId), titleEnc: String(r.titleEnc) })));
    const [, setTick] = useState(0);
    useEffect(() => { const t = setInterval(() => setTick((n) => n + 1), 30_000); return () => clearInterval(t); }, []);
    const reload = () => { approve.reload(); mine.reload(); grants.reload(); };
    const status = { pending: 'Waiting', approved: 'Approved', denied: 'Denied', expired: 'Ended', cancelled: 'Cancelled' };
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Access requests" }), _jsx(Loading, { loading: approve.loading && !approve.data, error: approve.error }), wait.length > 0 && _jsxs("section", { className: "card", children: [_jsx("h2", { children: "Waiting for your answer" }), _jsx("ul", { className: "plain reqs", children: wait.map((r) => _jsx(Waiting, { r: r, title: titles[Number(r.itemId)] ?? '…', onDone: reload }, r.id)) })] }), _jsxs("section", { className: "card", children: [_jsx("h2", { children: "Open now" }), gs.length === 0 ? _jsx(Empty, { children: "Nothing approved for you right now." }) : _jsx("ul", { className: "plain reqs", children: gs.map((g) => _jsx(Grant, { g: g, onDone: reload }, g.id)) })] }), _jsxs("section", { className: "card", children: [_jsx("h2", { children: "What you asked for" }), my.length === 0 ? _jsx(Empty, { children: "You have not asked for anything. Items you may only ask for show \u201Cask to open\u201D in the list." })
                        : _jsx("div", { className: "table-wrap", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Item" }), _jsx("th", { children: "Why" }), _jsx("th", { children: "Asked" }), _jsx("th", { children: "Answer" }), _jsx("th", {})] }) }), _jsx("tbody", { children: my.map((r) => _jsxs("tr", { children: [_jsxs("td", { children: [titles[Number(r.itemId)] ?? '…', _jsx("div", { className: "muted small", children: r.vault })] }), _jsxs("td", { children: [r.reason, r.ticket ? _jsx("div", { className: "muted small", children: r.ticket }) : null] }), _jsx("td", { title: fmtDate(r.at), children: ago(r.at) }), _jsxs("td", { children: [_jsx("span", { className: 'badge ' + (r.status === 'approved' ? 'ok' : r.status === 'denied' ? 'bad' : ''), children: status[r.status] ?? r.status }), r.decidedBy ? _jsxs("div", { className: "muted small", children: [r.decidedBy, r.note ? `: ${r.note}` : ''] }) : null] }), _jsx("td", { children: r.status === 'pending' && _jsx("button", { className: "small", onClick: async () => { await api(`/api/vault/requests/${r.id}/cancel`, { method: 'POST', body: {} }); reload(); }, children: "Cancel" }) })] }, r.id)) })] }) })] })] });
}
