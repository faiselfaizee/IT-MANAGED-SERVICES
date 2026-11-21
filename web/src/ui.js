import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { Fragment, useState } from 'react';
import { createPortal } from 'react-dom';
export const fmtBytes = (n) => {
    if (!n || n <= 0)
        return '–';
    const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
    let i = 0;
    let v = n;
    while (v >= 1024 && i < u.length - 1) {
        v /= 1024;
        i++;
    }
    return (v >= 100 || i === 0 ? Math.round(v) : Math.round(v * 10) / 10) + ' ' + u[i];
};
export const fmtDate = (s) => (s ? new Date(s).toLocaleString() : '–');
export const fmtDay = (s) => (s ? s.slice(0, 10) : '–');
export const fmtUptime = (s) => { if (!s)
    return '–'; const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600); return d ? `${d}d ${h}h` : `${h}h ${Math.floor((s % 3600) / 60)}m`; };
export const ago = (s) => {
    if (!s)
        return 'never';
    const sec = Math.max(0, (Date.now() - new Date(s).getTime()) / 1000);
    return sec < 90 ? 'just now' : sec < 5400 ? Math.round(sec / 60) + ' min ago' : sec < 129600 ? Math.round(sec / 3600) + ' h ago' : Math.round(sec / 86400) + ' days ago';
};
export function Badge({ tone, children }) { return _jsx("span", { className: 'badge ' + (tone ?? ''), children: children }); }
export function Err({ text }) { return text ? _jsx("p", { className: "err", role: "alert", children: text }) : null; }
export function Loading({ loading, error }) { return error ? _jsx(Err, { text: error }) : loading ? _jsx("p", { className: "muted", children: "Loading\u2026" }) : null; }
export function Empty({ children }) { return _jsx("p", { className: "muted", children: children }); }
/** A dialog. It is drawn at the end of the page, so no surface it is opened from (blurred glass, a scrolling pane, another dialog) can hide or clip it. */
export function Modal({ title, onClose, children }) {
    const box = _jsx("div", { className: "modal", role: "dialog", "aria-label": title, onMouseDown: (e) => { if (e.target === e.currentTarget)
            onClose(); }, children: _jsxs("div", { children: [_jsx("h2", { children: title }), children] }) });
    return typeof document === 'undefined' ? box : createPortal(box, document.body);
}
export function Field({ label, hint, children }) { return _jsxs("label", { children: [_jsx("span", { children: label }), children, hint ? _jsx("small", { className: "muted", children: hint }) : null] }); }
export function Dl({ rows }) {
    return _jsx("dl", { className: "kv", children: rows.filter(([, v]) => v !== '' && v !== null && v !== undefined).map(([k, v]) => _jsxs(Fragment, { children: [_jsx("dt", { children: k }), _jsx("dd", { children: v })] }, k)) });
}
export function Card({ title, children }) { return _jsxs("section", { className: "card", children: [title && _jsx("h2", { style: { marginTop: 0 }, children: title }), children] }); }
export function Meter({ pct }) { const p = Math.max(0, Math.min(100, pct)); return _jsx("div", { className: "bar", title: Math.round(p) + '%', children: _jsx("i", { className: p >= 90 ? 'hot' : '', style: { width: p + '%' } }) }); }
/** A button that asks the server and shows the error next to itself. */
export function Act({ label, run, primary, danger, confirm, disabled }) {
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState('');
    return _jsxs(_Fragment, { children: [_jsx("button", { className: (primary ? 'primary ' : '') + (danger ? 'danger' : ''), disabled: busy || disabled, onClick: async () => { if (confirm && !window.confirm(confirm))
                    return; setBusy(true); setErr(''); try {
                    await run();
                }
                catch (e) {
                    setErr(e.message);
                }
                finally {
                    setBusy(false);
                } }, children: label }), err && _jsx("span", { className: "err", children: err })] });
}
/** Runs a save, shows the error inside the form, closes on success. */
export function useSubmit(done) {
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    return { err, busy, submit: async (fn) => { setBusy(true); setErr(''); try {
            await fn();
            done();
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        } } };
}
export function str(o, k) { const v = o[k]; return v === null || v === undefined ? '' : String(v); }
export const copyText = (t) => { try {
    void navigator.clipboard.writeText(t);
}
catch { /* clipboard not available */ } };
export function Copy({ text }) { return _jsx("code", { className: "copy", onClick: () => copyText(text), title: "Click to copy", children: text }); }
