import { jsx as _jsx, Fragment as _Fragment, jsxs as _jsxs } from "react/jsx-runtime";
import { useState } from 'react';
import { api, go, useLoad } from '../http';
import { Act, Badge, Card, Dl, Empty, Err, Field, Modal, fmtBytes, useSubmit } from '../ui';
export default function Discover() {
    const [tab, setTab] = useState('probe');
    const [prefill, setPrefill] = useState('');
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Discover devices" }), _jsx("nav", { className: "sub", children: [['probe', 'Look up a device'], ['scan', 'Scan a range']].map(([k, l]) => _jsx("a", { href: "#/assets/discover", className: tab === k ? 'on' : '', onClick: (e) => { e.preventDefault(); setTab(k); }, children: l }, k)) }), tab === 'probe' && _jsx(Probe, { initial: prefill }, prefill), tab === 'scan' && _jsx(Scan, { onPick: (ip) => { setPrefill(ip); setTab('probe'); } })] });
}
function Probe({ initial = '' }) {
    const [target, setTarget] = useState(initial);
    const [res, setRes] = useState(null);
    const [details, setDetails] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    const [adopt, setAdopt] = useState(false);
    const run = async () => { setBusy(true); setErr(''); setDetails(null); try {
        setRes(await api('/api/assets/discover/probe', { body: { target } }));
    }
    catch (e) {
        setErr(e.message);
        setRes(null);
    }
    finally {
        setBusy(false);
    } };
    const inv = details?.inventory;
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("input", { placeholder: "IP address or DNS name", value: target, onChange: (e) => setTarget(e.target.value), onKeyDown: (e) => { if (e.key === 'Enter' && target.trim())
                            void run(); }, style: { minWidth: 260 } }), _jsx("button", { className: "primary", disabled: busy || !target.trim(), onClick: run, children: busy ? 'Probing…' : 'Probe' })] }), _jsx(Err, { text: err }), res && _jsxs(Card, { title: res.hostname || res.target, children: [_jsx(Dl, { rows: [['Addresses', res.ips.join(', ')], ['Reachable', _jsx(Badge, { tone: res.reachable ? 'ok' : 'bad', children: res.reachable ? 'Yes' : 'No answer' })], ['Open ports', res.openNames.join(', ') || 'none found'], ['MAC address', res.mac], ['Looks like', [res.guess?.cat, res.guess?.sub].filter(Boolean).join(' / ')]] }), res.existing ? _jsxs("p", { className: "note", children: ["Already in the asset list: ", _jsx("a", { href: `#/assets/${res.existing.id}`, children: res.existing.name }), res.existing.why ? ` (same ${res.existing.why})` : null] }) : null, _jsxs("div", { className: "tools", children: [_jsx(Act, { label: "Fetch details", run: async () => { setDetails(await api('/api/assets/discover/lookup', { body: { host: res.target } })); } }), _jsx("button", { className: "primary", onClick: () => setAdopt(true), children: res.existing ? 'Update asset' : 'Add as asset' })] }), _jsx("p", { className: "muted", children: "Fetching details uses the agent when one is installed, otherwise a saved credential (WinRM for Windows, SSH for Linux)." })] }), details && inv && _jsx(Card, { title: `Details (from ${details.source})`, children: _jsx(Dl, { rows: [['Host', String(inv.hostname ?? '')], ['Operating system', [inv.os_name, inv.os_version].filter(Boolean).join(' ')], ['Make / model', [inv.make, inv.model].filter(Boolean).join(' ')], ['Serial', String(inv.serial_number ?? '')], ['Processor', String(inv.cpu_model ?? '')], ['Memory', inv.ram_bytes ? fmtBytes(Number(inv.ram_bytes)) : ''], ['Storage', inv.total_storage_bytes ? fmtBytes(Number(inv.total_storage_bytes)) : '']] }) }), adopt && res && _jsx(Adopt, { probe: res, raw: details?.raw, source: details?.source, onClose: (id) => { setAdopt(false); if (id)
                    go(`assets/${id}`); } })] });
}
function Adopt({ probe, raw, source, onClose }) {
    const companies = useLoad('/api/assets/companies');
    const [name, setName] = useState(probe.hostname || probe.target);
    const [co, setCo] = useState('');
    const [cat, setCat] = useState(probe.guess?.cat ?? '');
    const [loc, setLoc] = useState('');
    const [notes, setNotes] = useState('');
    const f = useSubmit(() => undefined);
    const [err, setErr] = useState('');
    return _jsx(Modal, { title: probe.existing ? 'Update asset' : 'Add as asset', onClose: () => onClose(), children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Name", children: _jsx("input", { value: name, onChange: (e) => setName(e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Customer", children: _jsxs("select", { value: co, onChange: (e) => setCo(e.target.value), children: [_jsx("option", { value: "", children: "(own organisation)" }), (Array.isArray(companies.data) ? companies.data : []).map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) }), _jsx(Field, { label: "Category", children: _jsx("input", { value: cat, onChange: (e) => setCat(e.target.value) }) })] }), _jsx(Field, { label: "Location", children: _jsx("input", { value: loc, onChange: (e) => setLoc(e.target.value) }) }), _jsx(Field, { label: "Notes", children: _jsx("textarea", { rows: 2, value: notes, onChange: (e) => setNotes(e.target.value) }) }), _jsx(Err, { text: err || f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => onClose(), children: "Cancel" }), _jsx("button", { className: "primary", onClick: async () => { setErr(''); try {
                                const r = await api('/api/assets/discover/adopt', { body: { probe, name, companyId: co ? Number(co) : undefined, category: cat, location: loc, notes, raw, source, assetId: probe.existing?.id } });
                                onClose(r.id);
                            }
                            catch (e) {
                                setErr(e.message);
                            } }, children: "Save" })] })] }) });
}
function Scan({ onPick }) {
    const [cidr, setCidr] = useState('192.168.1.0/24');
    const [res, setRes] = useState(null);
    const [err, setErr] = useState('');
    const [busy, setBusy] = useState(false);
    return _jsxs(_Fragment, { children: [_jsxs("div", { className: "tools", children: [_jsx("input", { value: cidr, onChange: (e) => setCidr(e.target.value), "aria-label": "Range" }), _jsx("button", { className: "primary", disabled: busy, onClick: async () => { setBusy(true); setErr(''); setRes(null); try {
                            setRes(await api('/api/assets/discover/scan', { body: { cidr } }));
                        }
                        catch (e) {
                            setErr(e.message);
                        }
                        finally {
                            setBusy(false);
                        } }, children: busy ? 'Scanning…' : 'Scan' }), _jsx("span", { className: "muted", children: "Up to 1024 addresses (a /22). The scan runs from the server, so it only sees networks the server can reach; use a site collector for other sites." })] }), _jsx(Err, { text: err }), res && (res.found.length === 0 ? _jsxs(Empty, { children: ["No device answered among ", res.scanned, " addresses."] }) : _jsxs(_Fragment, { children: [_jsxs("p", { className: "muted", children: [res.found.length, " of ", res.scanned, " addresses answered."] }), _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "Address" }), _jsx("th", { children: "Name" }), _jsx("th", { children: "Looks like" }), _jsx("th", { children: "Open ports" }), _jsx("th", {})] }) }), _jsx("tbody", { children: res.found.map((h) => _jsxs("tr", { children: [_jsx("td", { className: "mono", children: h.ip }), _jsx("td", { children: h.hostname }), _jsx("td", { children: [h.cat, h.sub].filter(Boolean).join(' / ') }), _jsx("td", { className: "mono", children: h.open.join(', ') }), _jsx("td", { children: h.known ? _jsx(Badge, { tone: "ok", children: "in assets" }) : _jsx("a", { href: "#/assets/discover", onClick: (e) => { e.preventDefault(); onPick(h.ip); }, children: "Look up" }) })] }, h.ip)) })] })] }))] });
}
