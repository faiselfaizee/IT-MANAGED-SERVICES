import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { Logo } from '../chrome';
import * as C from './crypto';
/** The public page of a send link (no sign-in). The key is in the address after the last /, which browsers never send to a server. */
export default function SendOpen({ id, frag }) {
    const [meta, setMeta] = useState(null);
    const [gone, setGone] = useState('');
    const [pass, setPass] = useState('');
    const [text, setText] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const [shown, setShown] = useState(false);
    useEffect(() => { void fetch(`/api/public/vault/sends/${encodeURIComponent(id)}`).then(async (r) => { const j = await r.json().catch(() => ({})); if (r.ok)
        setMeta(j);
    else
        setGone(String(j.detail ?? 'This link does not work.')); }, () => setGone('The server could not be reached.')); }, [id]);
    const open = async () => {
        setBusy(true);
        setErr('');
        try {
            const key = C.unb64(frag);
            const r = await fetch(`/api/public/vault/sends/${encodeURIComponent(id)}/open`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ passcodeCheck: meta?.passcode ? await C.sendCheck(key, pass) : undefined }) });
            const j = await r.json().catch(() => ({}));
            if (!r.ok) {
                if (r.status === 403)
                    setErr(String(j.detail));
                else
                    setGone(String(j.detail ?? 'This link does not work.'));
                return;
            }
            const v = await C.decryptJson(await C.sendKey(key, meta?.passcode ? pass : undefined), String(j.dataEnc));
            setText(v.text);
            history.replaceState(null, '', '/#/send/opened');
        }
        catch {
            setErr('The link is incomplete or damaged. Ask for a new one.');
        }
        finally {
            setBusy(false);
        }
    };
    return _jsx("main", { className: "send-page", children: _jsxs("div", { className: "send-card", children: [_jsxs("div", { className: "send-brand", children: [_jsx(Logo, { size: 28 }), _jsx("strong", { children: "DeskAsk" })] }), text !== null ? _jsxs(_Fragment, { children: [_jsx("h1", { children: "Here it is" }), _jsx("pre", { className: "send-text", children: shown ? text : text.replace(/[^\s]/g, '•') }), _jsxs("div", { className: "tools", children: [_jsx("button", { onClick: () => setShown(!shown), children: shown ? 'Hide' : 'Show' }), _jsx("button", { className: "primary", onClick: () => void navigator.clipboard?.writeText(text), children: "Copy" })] }), _jsxs("p", { className: "muted", children: ["Save it now: ", meta && meta.viewsLeft > 1 ? `the link works ${meta.viewsLeft - 1} more time${meta.viewsLeft > 2 ? 's' : ''}.` : 'the link no longer works.'] })] }) : gone ? _jsxs(_Fragment, { children: [_jsx("h1", { children: "This link no longer works" }), _jsx("p", { children: gone }), _jsx("p", { className: "muted", children: "Ask the person who sent it for a new one." })] })
                    : !meta ? _jsx("p", { className: "muted", children: "Checking the link\u2026" }) : _jsxs(_Fragment, { children: [_jsxs("h1", { children: [meta.from, " sent you something private"] }), _jsxs("p", { children: ["It opens ", meta.viewsLeft === 1 ? 'only once' : `${meta.viewsLeft} more times`, ", until ", new Date(meta.expiresAt).toLocaleString(), ". Open it when you are ready to keep it."] }), meta.passcode && _jsxs("label", { children: [_jsx("span", { children: "Passcode" }), _jsx("input", { value: pass, onChange: (e) => setPass(e.target.value), autoFocus: true, autoComplete: "off" })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: busy || (meta.passcode && !pass), onClick: () => void open(), children: busy ? 'Opening…' : 'Open' }) }), _jsx("p", { className: "muted small", children: "It was encrypted before it left the sender's computer; DeskAsk cannot read it." })] })] }) });
}
