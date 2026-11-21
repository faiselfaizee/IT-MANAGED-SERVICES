import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, useLoad } from '../http';
import { Field, Loading } from '../ui';
/** Vault settings an administrator chooses. */
export default function Settings() {
    const { data, error, loading, reload } = useLoad('/api/vault/settings');
    const [v, setV] = useState({});
    const [msg, setMsg] = useState('');
    const [err, setErr] = useState('');
    useEffect(() => { if (data)
        setV({ ...data }); }, [data]);
    const set = (k, x) => setV({ ...v, [k]: x });
    if (loading && !data)
        return _jsx(Loading, { loading: true, error: error });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Vault settings" }), _jsxs("form", { className: "form", style: { maxWidth: 640 }, onSubmit: async (e) => {
                    e.preventDefault();
                    setMsg('');
                    setErr('');
                    try {
                        await api('/api/vault/settings', { method: 'PUT', body: { ...v, autoLockMinutes: Number(v.autoLockMinutes), rotationLength: Number(v.rotationLength), defaultRotateDays: Number(v.defaultRotateDays), requestMaxMinutes: Number(v.requestMaxMinutes) } });
                        setMsg('Saved.');
                        reload();
                    }
                    catch (x) {
                        setErr(x.message);
                    }
                }, children: [_jsx(Field, { label: "Who may create vaults", children: _jsxs("select", { value: v.creators ?? 'admins', onChange: (e) => set('creators', e.target.value), children: [_jsx("option", { value: "admins", children: "Administrators" }), _jsx("option", { value: "engineers", children: "Administrators and engineers" })] }) }), _jsx(Field, { label: "Lock the vault after this many idle minutes", children: _jsx("input", { type: "number", min: 1, max: 480, value: v.autoLockMinutes ?? 15, onChange: (e) => set('autoLockMinutes', e.target.value) }) }), _jsx(Field, { label: "Longest time an access request can be approved for (minutes)", children: _jsx("input", { type: "number", min: 15, max: 10080, value: v.requestMaxMinutes ?? 480, onChange: (e) => set('requestMaxMinutes', e.target.value) }) }), _jsx("h2", { children: "Managed accounts" }), _jsx(Field, { label: "Length of passwords DeskAsk sets", children: _jsx("input", { type: "number", min: 14, max: 128, value: v.rotationLength ?? 24, onChange: (e) => set('rotationLength', e.target.value) }) }), _jsx(Field, { label: "Change managed passwords every (days, for new accounts; 0 = only when asked)", children: _jsx("input", { type: "number", min: 0, max: 365, value: v.defaultRotateDays ?? 30, onChange: (e) => set('defaultRotateDays', e.target.value) }) }), _jsx("h2", { children: "Breach check" }), _jsxs("label", { className: "check", children: [_jsx("input", { type: "checkbox", checked: v.breachCheck !== false, onChange: (e) => set('breachCheck', e.target.checked) }), " Let people check passwords against Have I Been Pwned (only the first 5 characters of each password's SHA-1 hash leave the server)"] }), err && _jsx("p", { className: "err", role: "alert", children: err }), msg && _jsx("p", { className: "muted", role: "status", children: msg }), _jsx("div", { className: "tools", children: _jsx("button", { type: "submit", className: "primary", children: "Save settings" }) })] })] });
}
