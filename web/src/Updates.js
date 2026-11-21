import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { accessToken, signIn } from './auth';
import { useLoad } from './http';
import { Badge, Card, Err, Loading } from './ui';
const TONE = { done: 'ok', running: 'warn', rolled_back: 'bad', failed: 'bad' };
const LABEL = { done: 'Updated', running: 'Updating', rolled_back: 'Went back to the old version', failed: 'Failed' };
const PIECE = 5 * 1024 * 1024;
const hex = () => Array.from(crypto.getRandomValues(new Uint8Array(12)), (b) => b.toString(16).padStart(2, '0')).join('');
/** One piece (or the closing call) as its own small request, so a proxy with a time limit cannot cut the upload off. */
function sendPiece(url, body) {
    return new Promise((resolve, reject) => {
        const x = new XMLHttpRequest();
        x.open('POST', url);
        const t = accessToken();
        if (t)
            x.setRequestHeader('authorization', 'Bearer ' + t);
        x.onload = () => { let j = {}; try {
            j = JSON.parse(x.responseText);
        }
        catch { /* not json */ } resolve({ status: x.status, json: j }); };
        x.onerror = () => reject(new Error('The connection was interrupted.'));
        x.send(body);
    });
}
/** Sends the file in pieces with progress; the server only drops it in the inbox and the updater service does the rest. */
async function upload(file, onProgress) {
    if (!accessToken()) {
        void signIn();
        throw new Error('Your sign-in has ended. Signing in again; choose the file once more afterwards.');
    }
    const id = hex();
    let at = 0;
    let failures = 0;
    const base = `/api/platform/updates/part?upload=${id}`;
    for (;;) {
        const last = at + PIECE >= file.size;
        const body = file.slice(at, Math.min(file.size, at + PIECE));
        const q = `${base}&at=${at}` + (last ? `&done=1&name=${encodeURIComponent(file.name)}` : '');
        let r;
        try {
            r = await sendPiece(q, body);
        }
        catch (e) {
            if (++failures > 5)
                throw e;
            await new Promise((ok) => setTimeout(ok, 2000));
            continue;
        }
        if (r.status === 409 && r.json.error === 'out_of_order' && at > 0 && ++failures <= 5) {
            at = Number(r.json.have) || 0;
            continue;
        } // the server has a different amount: carry on from there
        if (r.status >= 500 && ++failures <= 5) {
            await new Promise((ok) => setTimeout(ok, 2000));
            continue;
        }
        if (r.status === 401) {
            void signIn();
            throw new Error('Your sign-in has ended. Signing in again; choose the file once more afterwards.');
        }
        if (r.status >= 300)
            throw new Error(String(r.json.reason ?? r.json.error ?? `The server answered ${r.status}.`));
        failures = 0;
        if (last) {
            onProgress(100);
            return r.json;
        }
        at += body.size;
        onProgress(Math.round((at / file.size) * 100));
    }
}
/** Admin page: put in a new release by uploading its .dkb file. No setup page, no other files. */
export default function Updates() {
    const u = useLoad('/api/platform/updates');
    const [file, setFile] = useState(null);
    const [pct, setPct] = useState(0);
    const [busy, setBusy] = useState(false);
    const [err, setErr] = useState('');
    const [ok, setOk] = useState('');
    const d = u.data;
    const jobs = (d?.jobs ?? []);
    const queued = (d?.queued ?? []);
    const working = busy || queued.length > 0 || !!d?.status?.running;
    useEffect(() => { if (!working)
        return; const i = setInterval(u.reload, 4000); return () => clearInterval(i); }, [working, u.reload]);
    const send = async () => {
        if (!file)
            return;
        setBusy(true);
        setErr('');
        setOk('');
        setPct(0);
        try {
            await upload(file, setPct);
            setOk('Uploaded. The updater is checking the signature and installing it. The page may go offline for a minute; refresh it afterwards.');
            setFile(null);
            u.reload();
        }
        catch (e) {
            setErr(e.message);
        }
        finally {
            setBusy(false);
        }
    };
    const when = (ms) => (ms ? new Date(ms).toLocaleString() : '');
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Updates" }), _jsx(Loading, { loading: u.loading && !d, error: u.error }), d && !d.installed && _jsx(Card, { children: _jsx("p", { children: "Updates from this page are not set up on this server. Run the setup page once with the install tab; after that, updates are one file." }) }), d?.installed && _jsxs(Card, { title: "Installed version", children: [_jsxs("p", { children: [_jsx("strong", { children: d.status?.current || 'Unknown' }), " ", d.serviceAlive ? _jsx(Badge, { tone: "ok", children: "Updater running" }) : _jsx(Badge, { tone: "bad", children: "Updater not responding" })] }), !d.serviceAlive && _jsx("p", { className: "muted", children: "The updater task did not report in the last 3 minutes. Check the DeskAskUpdater task in Task Scheduler." })] }), d?.installed && _jsxs(Card, { title: "Install a new version", children: [_jsxs("p", { className: "muted", children: ["Download ", _jsx("code", { children: "deskask-host-win-x64-<version>.dkb" }), " from the GitHub release and choose it here. Your settings, database and customers stay as they are. The signature is checked first; if the new version does not start, the old one comes back by itself."] }), _jsxs("p", { className: "muted", children: ["Uploading needs two-step sign-in. If you have not set it up: ", _jsx("a", { href: "/account/security?returnUrl=%2F", children: "switch on two-step sign-in" }), ", then sign out and sign in again with the code."] }), _jsxs("div", { className: "form", children: [_jsx("input", { type: "file", accept: ".dkb", disabled: busy, onChange: (e) => setFile(e.target.files?.[0] ?? null), "aria-label": "Release file" }), busy && _jsx("progress", { value: pct, max: 100, "aria-label": "Upload progress" }), _jsx(Err, { text: err }), ok && _jsx("p", { role: "status", children: ok }), queued.length > 0 && _jsxs("p", { role: "status", children: ["Waiting for the updater: ", queued.map((q) => q.file).join(', ')] }), _jsx("div", { className: "actions", children: _jsx("button", { className: "primary", disabled: busy || !file || queued.length > 0, onClick: send, children: "Upload and install" }) })] })] }), jobs.length > 0 && _jsx(Card, { title: "History", children: _jsxs("table", { children: [_jsx("thead", { children: _jsxs("tr", { children: [_jsx("th", { children: "File" }), _jsx("th", { children: "Result" }), _jsx("th", { children: "From" }), _jsx("th", { children: "To" }), _jsx("th", { children: "When" })] }) }), _jsx("tbody", { children: jobs.map((j) => _jsxs("tr", { children: [_jsx("td", { children: j.file }), _jsxs("td", { children: [_jsx(Badge, { tone: TONE[j.status], children: LABEL[j.status] ?? j.status }), j.error ? _jsx("div", { className: "muted", children: j.error }) : null] }), _jsx("td", { children: j.from }), _jsx("td", { children: j.to }), _jsx("td", { className: "muted", children: when(j.finished || j.started) })] }, j.id)) })] }) })] });
}
