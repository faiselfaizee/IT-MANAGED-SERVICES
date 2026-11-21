import { useCallback, useEffect, useRef, useState } from 'react';
import { accessToken, signIn } from './auth';
export class ApiError extends Error {
    status;
    body;
    constructor(status, message, body) {
        super(message);
        this.status = status;
        this.body = body;
    }
}
/** The one place that talks to the server: adds the token, turns errors into readable messages, signs in again on 401. */
export async function api(path, o = {}) {
    const headers = {};
    const t = accessToken();
    if (t)
        headers.authorization = 'Bearer ' + t;
    if (o.body !== undefined)
        headers['content-type'] = 'application/json';
    const r = await fetch(path, { method: o.method ?? (o.body !== undefined ? 'POST' : 'GET'), headers, body: o.body !== undefined ? JSON.stringify(o.body) : undefined, signal: o.signal });
    if (r.status === 401) {
        void signIn();
        throw new ApiError(401, 'Signing in again…');
    }
    const text = await r.text();
    let j;
    try {
        j = text ? JSON.parse(text) : undefined;
    }
    catch {
        j = undefined;
    }
    if (!r.ok)
        throw new ApiError(r.status, message(r.status, j), j);
    return (j ?? {});
}
export async function apiText(path) {
    const t = accessToken();
    const r = await fetch(path, { headers: t ? { authorization: 'Bearer ' + t } : {} });
    if (r.status === 401) {
        void signIn();
        throw new ApiError(401, 'Signing in again…');
    }
    if (!r.ok)
        throw new ApiError(r.status, message(r.status, undefined));
    return r.text();
}
const MODULE_NAMES = { servicedesk: 'Service Desk', assets: 'Asset management', monitoring: 'Monitoring', adsuite: 'AD Manager', vault: 'Vault', backup: 'Backup' };
export function message(status, j) {
    if (j?.detail)
        return String(j.detail);
    if (j?.error === 'module_not_licensed')
        return `${MODULE_NAMES[String(j.module)] ?? 'This part of DeskAsk'} is not part of this licence. Ask your administrator to add it to the licence.`;
    if (j?.error === 'limit_reached')
        return 'The licence limit for assets has been reached.';
    if (j?.error)
        return String(j.error).replace(/_/g, ' ');
    return status === 403 ? 'Only administrators can do this.' : status === 404 ? 'Not found.' : `The server answered ${status}.`;
}
/** Opens text the server returned (for example a print page) in a new tab; the page needs the token, so it is fetched here. */
export async function openPage(path, type = 'text/html') {
    const w = window.open('', '_blank');
    const text = await apiText(path);
    const url = URL.createObjectURL(new Blob([text], { type: type + ';charset=utf-8' }));
    if (w)
        w.location.href = url;
    else
        window.open(url, '_blank');
}
export async function download(path, name) {
    const text = await apiText(path);
    const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    URL.revokeObjectURL(url);
}
export function useLoad(path, deps = []) {
    const [data, setData] = useState(null);
    const [error, setError] = useState('');
    const [loading, setLoading] = useState(path !== null);
    const [n, setN] = useState(0);
    const reload = useCallback(() => setN((x) => x + 1), []);
    useEffect(() => {
        if (path === null) {
            setLoading(false);
            return;
        }
        const ac = new AbortController();
        setLoading(true);
        api(path, { signal: ac.signal }).then((d) => { setData(d); setError(''); }, (e) => { if (e.name !== 'AbortError')
            setError(e.message); }).finally(() => { if (!ac.signal.aborted)
            setLoading(false); });
        return () => ac.abort();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [path, n, ...deps]);
    return { data, error, loading, reload };
}
/** Runs fn on an interval while mounted (live logs). */
export function useInterval(fn, ms) {
    const ref = useRef(fn);
    ref.current = fn;
    useEffect(() => { if (ms === null)
        return; const id = setInterval(() => ref.current(), ms); return () => clearInterval(id); }, [ms]);
}
export function useHash() {
    const read = () => location.hash.replace(/^#\/?/, '').split('?')[0].split('/').filter(Boolean);
    const [parts, setParts] = useState(read);
    useEffect(() => { const f = () => setParts(read()); window.addEventListener('hashchange', f); return () => window.removeEventListener('hashchange', f); }, []);
    return parts;
}
export const go = (path) => { location.hash = '#/' + path.replace(/^\/+/, ''); };
/** Sends a file as the raw request body (the Service Desk takes attachments this way). */
export async function upload(path, file) {
    const headers = { 'content-type': file.type || 'application/octet-stream' };
    const t = accessToken();
    if (t)
        headers.authorization = 'Bearer ' + t;
    const r = await fetch(path, { method: 'POST', headers, body: file });
    if (r.status === 401) {
        void signIn();
        throw new ApiError(401, 'Signing in again…');
    }
    const text = await r.text();
    let j;
    try {
        j = text ? JSON.parse(text) : undefined;
    }
    catch {
        j = undefined;
    }
    if (!r.ok)
        throw new ApiError(r.status, j?.detail ? String(j.detail) : j?.error ? String(j.error).replace(/_/g, ' ') : `The server answered ${r.status}.`, j);
    return j ?? {};
}
/** Saves a file the server returned; the request needs the token, so it is fetched here. */
export async function saveFile(path, name) {
    const t = accessToken();
    const r = await fetch(path, { headers: t ? { authorization: 'Bearer ' + t } : {} });
    if (!r.ok)
        throw new ApiError(r.status, r.status === 404 ? 'Not found.' : `The server answered ${r.status}.`);
    const url = URL.createObjectURL(await r.blob());
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    a.click();
    URL.revokeObjectURL(url);
}
