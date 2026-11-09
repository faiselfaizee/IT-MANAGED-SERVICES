import { useCallback, useEffect, useRef, useState } from 'react';
import { accessToken, signIn } from './auth';

export type Rec = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

export class ApiError extends Error {
  constructor(public status: number, message: string, public body?: Rec) { super(message); }
}

/** The one place that talks to the server: adds the token, turns errors into readable messages, signs in again on 401. */
export async function api<T = Rec>(path: string, o: { method?: string; body?: unknown; signal?: AbortSignal } = {}): Promise<T> {
  const headers: Record<string, string> = {};
  const t = accessToken(); if (t) headers.authorization = 'Bearer ' + t;
  if (o.body !== undefined) headers['content-type'] = 'application/json';
  const r = await fetch(path, { method: o.method ?? (o.body !== undefined ? 'POST' : 'GET'), headers, body: o.body !== undefined ? JSON.stringify(o.body) : undefined, signal: o.signal });
  if (r.status === 401) { void signIn(); throw new ApiError(401, 'Signing in again…'); }
  const text = await r.text();
  let j: Rec | undefined; try { j = text ? JSON.parse(text) : undefined; } catch { j = undefined; }
  if (!r.ok) throw new ApiError(r.status, message(r.status, j), j);
  return (j ?? {}) as T;
}
export async function apiText(path: string): Promise<string> {
  const t = accessToken();
  const r = await fetch(path, { headers: t ? { authorization: 'Bearer ' + t } : {} });
  if (r.status === 401) { void signIn(); throw new ApiError(401, 'Signing in again…'); }
  if (!r.ok) throw new ApiError(r.status, message(r.status, undefined));
  return r.text();
}
function message(status: number, j?: Rec): string {
  if (j?.detail) return String(j.detail);
  if (j?.error === 'module_not_licensed') return 'Asset management is not part of this licence.';
  if (j?.error === 'limit_reached') return 'The licence limit for assets has been reached.';
  if (j?.error) return String(j.error).replace(/_/g, ' ');
  return status === 403 ? 'Only administrators can do this.' : status === 404 ? 'Not found.' : `The server answered ${status}.`;
}
/** Opens text the server returned (for example a print page) in a new tab; the page needs the token, so it is fetched here. */
export async function openPage(path: string, type = 'text/html') {
  const w = window.open('', '_blank'); const text = await apiText(path);
  const url = URL.createObjectURL(new Blob([text], { type: type + ';charset=utf-8' }));
  if (w) w.location.href = url; else window.open(url, '_blank');
}
export async function download(path: string, name: string) {
  const text = await apiText(path); const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }));
  const a = document.createElement('a'); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url);
}

export function useLoad<T = Rec>(path: string | null, deps: unknown[] = []): { data: T | null; error: string; loading: boolean; reload: () => void } {
  const [data, setData] = useState<T | null>(null); const [error, setError] = useState(''); const [loading, setLoading] = useState(path !== null);
  const [n, setN] = useState(0); const reload = useCallback(() => setN((x) => x + 1), []);
  useEffect(() => {
    if (path === null) { setLoading(false); return; }
    const ac = new AbortController(); setLoading(true);
    api<T>(path, { signal: ac.signal }).then((d) => { setData(d); setError(''); }, (e: Error) => { if (e.name !== 'AbortError') setError(e.message); }).finally(() => { if (!ac.signal.aborted) setLoading(false); });
    return () => ac.abort();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, n, ...deps]);
  return { data, error, loading, reload };
}

/** Runs fn on an interval while mounted (live logs). */
export function useInterval(fn: () => void, ms: number | null) {
  const ref = useRef(fn); ref.current = fn;
  useEffect(() => { if (ms === null) return; const id = setInterval(() => ref.current(), ms); return () => clearInterval(id); }, [ms]);
}

export function useHash(): string[] {
  const read = () => location.hash.replace(/^#\/?/, '').split('?')[0].split('/').filter(Boolean);
  const [parts, setParts] = useState<string[]>(read);
  useEffect(() => { const f = () => setParts(read()); window.addEventListener('hashchange', f); return () => window.removeEventListener('hashchange', f); }, []);
  return parts;
}
export const go = (path: string) => { location.hash = '#/' + path.replace(/^\/+/, ''); };
