import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import * as S from './session';
import { RecoveryCode, Strength } from './Gate';
import { strength } from './crypto';
/** The person's own vault key: master password and recovery code. */
export default function Account() {
    const [a, setA] = useState('');
    const [b, setB] = useState('');
    const [c, setC] = useState('');
    const [msg, setMsg] = useState('');
    const [err, setErr] = useState('');
    const [rp, setRp] = useState('');
    const [code, setCode] = useState('');
    const [rerr, setRerr] = useState('');
    const [busy, setBusy] = useState(false);
    if (code)
        return _jsx(RecoveryCode, { code: code, again: true, onDone: () => setCode('') });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "My vault key" }), _jsxs("div", { className: "grid", children: [_jsxs("form", { className: "card form", onSubmit: async (e) => {
                            e.preventDefault();
                            setErr('');
                            setMsg('');
                            if (b.length < 12 || strength(b).score < 2)
                                return setErr('Choose at least 12 characters that are not easy to guess.');
                            if (b !== c)
                                return setErr('The two new passwords are not the same.');
                            setBusy(true);
                            try {
                                await S.changePassword(a, b);
                                setMsg('Your master password is changed.');
                                setA('');
                                setB('');
                                setC('');
                            }
                            catch (x) {
                                setErr(x.message);
                            }
                            finally {
                                setBusy(false);
                            }
                        }, children: [_jsx("h2", { children: "Change the master password" }), _jsxs("label", { children: [_jsx("span", { children: "Current master password" }), _jsx("input", { type: "password", autoComplete: "current-password", value: a, onChange: (e) => setA(e.target.value) })] }), _jsxs("label", { children: [_jsx("span", { children: "New master password" }), _jsx("input", { type: "password", autoComplete: "new-password", value: b, onChange: (e) => setB(e.target.value) })] }), _jsx(Strength, { pw: b }), _jsxs("label", { children: [_jsx("span", { children: "Type it again" }), _jsx("input", { type: "password", autoComplete: "new-password", value: c, onChange: (e) => setC(e.target.value) })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), msg && _jsx("p", { className: "note", role: "status", children: msg }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: busy, children: "Change" }) })] }), _jsxs("form", { className: "card form", onSubmit: async (e) => { e.preventDefault(); setRerr(''); try {
                            setCode(await S.newRecoveryCode(rp));
                            setRp('');
                        }
                        catch (x) {
                            setRerr(x.message);
                        } }, children: [_jsx("h2", { children: "New recovery code" }), _jsx("p", { className: "muted", children: "When the old one may have been seen, or is lost. The old code stops working." }), _jsxs("label", { children: [_jsx("span", { children: "Master password" }), _jsx("input", { type: "password", autoComplete: "current-password", value: rp, onChange: (e) => setRp(e.target.value) })] }), rerr && _jsx("p", { className: "err", role: "alert", children: rerr }), _jsx("div", { className: "tools", children: _jsx("button", { disabled: !rp, children: "Make a new recovery code" }) })] }), _jsxs("section", { className: "card", children: [_jsx("h2", { children: "Lock" }), _jsxs("p", { className: "muted", children: ["The vault locks itself after ", String(S.current().me?.settings?.autoLockMinutes ?? 15), " idle minutes, and when this tab is closed."] }), _jsx("div", { className: "tools", children: _jsx("button", { onClick: S.lock, children: "Lock now" }) })] })] })] });
}
