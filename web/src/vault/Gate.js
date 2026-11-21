import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useState } from 'react';
import { copyText } from '../ui';
import * as S from './session';
import { strength } from './crypto';
function Strength({ pw }) {
    const s = strength(pw);
    return pw ? _jsxs("div", { className: "pw-meter", "data-score": s.score, children: [_jsx("i", { style: { width: `${(s.score + 1) * 20}%` } }), _jsx("span", { children: s.label })] }) : null;
}
/** Shows the recovery code once, until the person says they have kept it. */
export function RecoveryCode({ code, onDone, again }) {
    const [ok, setOk] = useState(false);
    const save = () => {
        const a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([`DeskAsk Vault recovery code\n\n${code}\n\nKeep this somewhere safe and offline. With it you can set a new master password.\n`], { type: 'text/plain' }));
        a.download = 'deskask-vault-recovery-code.txt';
        a.click();
    };
    return _jsxs("section", { className: "vault-gate", children: [_jsx("h1", { children: again ? 'Your new recovery code' : 'Keep your recovery code' }), _jsx("p", { children: "If you forget your master password, this code is the only way back into your vault. Nobody at DeskAsk or your company can reset it for you." }), _jsx("p", { className: "recovery", "aria-label": "Recovery code", children: code }), _jsxs("div", { className: "tools", children: [_jsx("button", { type: "button", onClick: () => copyText(code), children: "Copy" }), _jsx("button", { type: "button", onClick: save, children: "Save as a text file" }), _jsx("button", { type: "button", onClick: () => window.print(), children: "Print" })] }), again && _jsx("p", { className: "muted", children: "The old recovery code no longer works." }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: ok, onChange: (e) => setOk(e.target.checked) }), " I have put the code somewhere safe, away from this computer"] }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: !ok, onClick: onDone, children: "Continue" }) })] });
}
/** First visit: choose a master password. Also used after a reset. */
export function SetUp({ reset = false, onStart, onDone }) {
    const [a, setA] = useState('');
    const [b, setB] = useState('');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const [code, setCode] = useState('');
    if (code)
        return _jsx(RecoveryCode, { code: code, onDone: onDone });
    const weak = strength(a).score < 2;
    return _jsxs("form", { className: "vault-gate", onSubmit: async (e) => {
            e.preventDefault();
            setErr('');
            if (a.length < 12)
                return setErr('Use at least 12 characters. A short sentence you can remember works well.');
            if (weak)
                return setErr('That master password is too easy to guess. Add words or characters.');
            if (a !== b)
                return setErr('The two passwords are not the same.');
            setBusy(true);
            onStart?.(); // keep this screen up until the recovery code has been seen
            try {
                setCode(await S.setUp(a, reset));
            }
            catch (x) {
                setErr(x.message);
            }
            finally {
                setBusy(false);
            }
        }, children: [_jsx("h1", { children: reset ? 'Start your vault again' : 'Set up your vault' }), _jsx("p", { children: "Your vault is encrypted on this computer with a master password before anything is sent to the server. The server cannot read your passwords, and it cannot recover your master password either." }), _jsxs("label", { children: [_jsx("span", { children: "Master password" }), _jsx("input", { type: "password", autoComplete: "new-password", value: a, onChange: (e) => setA(e.target.value), autoFocus: true })] }), _jsx(Strength, { pw: a }), _jsxs("label", { children: [_jsx("span", { children: "Type it again" }), _jsx("input", { type: "password", autoComplete: "new-password", value: b, onChange: (e) => setB(e.target.value) })] }), _jsx("p", { className: "muted small", children: "Not your sign-in password: choose a different one. You will need it each time the vault has locked itself." }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsx("div", { className: "tools", children: _jsx("button", { className: "primary", disabled: busy, children: busy ? 'Making your keys…' : 'Create my vault' }) })] });
}
/** The vault is locked: master password, or the recovery code to choose a new one, or (last resort) start again. */
export function Unlock({ onReset }) {
    const [mode, setMode] = useState('pw');
    const [pw, setPw] = useState('');
    const [code, setCode] = useState('');
    const [np, setNp] = useState('');
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const run = async (f) => { setBusy(true); setErr(''); try {
        await f();
    }
    catch (x) {
        setErr(x.message);
    }
    finally {
        setBusy(false);
    } };
    if (mode === 'reset')
        return _jsxs("section", { className: "vault-gate", children: [_jsx("h1", { children: "Start again without your old keys" }), _jsx("p", { children: "Only when both the master password and the recovery code are lost. Your personal vault is emptied, because nobody can open it any more. Shared vaults come back once a manager of each vault opens the Vault." }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "danger", onClick: onReset, children: "Start again" }), _jsx("button", { onClick: () => setMode('pw'), children: "Back" })] })] });
    return _jsxs("form", { className: "vault-gate", onSubmit: (e) => { e.preventDefault(); if (mode === 'pw')
            void run(() => S.unlockWith(pw));
        else
            void run(async () => { if (strength(np).score < 2 || np.length < 12)
                throw new Error('Choose a new master password of at least 12 characters that is not easy to guess.'); await S.recover(code, np); }); }, children: [_jsx("h1", { children: "The vault is locked" }), mode === 'pw' ? _jsxs(_Fragment, { children: [_jsxs("label", { children: [_jsx("span", { children: "Master password" }), _jsx("input", { type: "password", autoComplete: "current-password", value: pw, onChange: (e) => setPw(e.target.value), autoFocus: true })] }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", disabled: busy || !pw, children: busy ? 'Unlocking…' : 'Unlock' }), _jsx("button", { type: "button", className: "link", onClick: () => { setMode('rec'); setErr(''); }, children: "Forgot the master password?" })] })] }) : _jsxs(_Fragment, { children: [_jsx("p", { children: "Type your recovery code and choose a new master password." }), _jsxs("label", { children: [_jsx("span", { children: "Recovery code" }), _jsx("input", { value: code, onChange: (e) => setCode(e.target.value), placeholder: "XXXX-XXXX-XXXX-\u2026", autoFocus: true, spellCheck: false })] }), _jsxs("label", { children: [_jsx("span", { children: "New master password" }), _jsx("input", { type: "password", autoComplete: "new-password", value: np, onChange: (e) => setNp(e.target.value) })] }), _jsx(Strength, { pw: np }), err && _jsx("p", { className: "err", role: "alert", children: err }), _jsxs("div", { className: "tools", children: [_jsx("button", { className: "primary", disabled: busy || !code || !np, children: busy ? 'Checking…' : 'Set the new master password' }), _jsx("button", { type: "button", onClick: () => setMode('pw'), children: "Back" }), _jsx("button", { type: "button", className: "link", onClick: () => setMode('reset'), children: "I lost the recovery code too" })] })] })] });
}
export { Strength };
