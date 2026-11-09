import { Fragment, useState, type ReactNode } from 'react';
import type { Rec } from './http';

export const fmtBytes = (n?: number | null) => {
  if (!n || n <= 0) return '–'; const u = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']; let i = 0; let v = n;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return (v >= 100 || i === 0 ? Math.round(v) : Math.round(v * 10) / 10) + ' ' + u[i];
};
export const fmtDate = (s?: string | null) => (s ? new Date(s).toLocaleString() : '–');
export const fmtDay = (s?: string | null) => (s ? s.slice(0, 10) : '–');
export const fmtUptime = (s?: number | null) => { if (!s) return '–'; const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600); return d ? `${d}d ${h}h` : `${h}h ${Math.floor((s % 3600) / 60)}m`; };
export const ago = (s?: string | null) => {
  if (!s) return 'never'; const sec = Math.max(0, (Date.now() - new Date(s).getTime()) / 1000);
  return sec < 90 ? 'just now' : sec < 5400 ? Math.round(sec / 60) + ' min ago' : sec < 129600 ? Math.round(sec / 3600) + ' h ago' : Math.round(sec / 86400) + ' days ago';
};

export function Badge({ tone, children }: { tone?: 'ok' | 'warn' | 'bad'; children: ReactNode }) { return <span className={'badge ' + (tone ?? '')}>{children}</span>; }
export function Err({ text }: { text?: string }) { return text ? <p className="err" role="alert">{text}</p> : null; }
export function Loading({ loading, error }: { loading: boolean; error: string }) { return error ? <Err text={error} /> : loading ? <p className="muted">Loading…</p> : null; }
export function Empty({ children }: { children: ReactNode }) { return <p className="muted">{children}</p>; }

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  return <div className="modal" role="dialog" aria-label={title} onMouseDown={(e) => { if (e.target === e.currentTarget) onClose(); }}><div><h2>{title}</h2>{children}</div></div>;
}
export function Field({ label, children }: { label: string; children: ReactNode }) { return <label><span>{label}</span>{children}</label>; }

export function Dl({ rows }: { rows: [string, ReactNode][] }) {
  return <dl className="kv">{rows.filter(([, v]) => v !== '' && v !== null && v !== undefined).map(([k, v]) => <Fragment key={k}><dt>{k}</dt><dd>{v}</dd></Fragment>)}</dl>;
}
export function Card({ title, children }: { title?: string; children: ReactNode }) { return <section className="card">{title && <h2 style={{ marginTop: 0 }}>{title}</h2>}{children}</section>; }
export function Meter({ pct }: { pct: number }) { const p = Math.max(0, Math.min(100, pct)); return <div className="bar" title={Math.round(p) + '%'}><i className={p >= 90 ? 'hot' : ''} style={{ width: p + '%' }} /></div>; }

/** A button that asks the server and shows the error next to itself. */
export function Act({ label, run, primary, danger, confirm, disabled }: { label: string; run: () => Promise<unknown>; primary?: boolean; danger?: boolean; confirm?: string; disabled?: boolean }) {
  const [busy, setBusy] = useState(false); const [err, setErr] = useState('');
  return <>
    <button className={(primary ? 'primary ' : '') + (danger ? 'danger' : '')} disabled={busy || disabled}
      onClick={async () => { if (confirm && !window.confirm(confirm)) return; setBusy(true); setErr(''); try { await run(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } }}>{label}</button>
    {err && <span className="err">{err}</span>}
  </>;
}

/** Runs a save, shows the error inside the form, closes on success. */
export function useSubmit(done: () => void) {
  const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  return { err, busy, submit: async (fn: () => Promise<unknown>) => { setBusy(true); setErr(''); try { await fn(); done(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); } } };
}
export function str(o: Rec, k: string): string { const v = o[k]; return v === null || v === undefined ? '' : String(v); }
export const copyText = (t: string) => { try { void navigator.clipboard.writeText(t); } catch { /* clipboard not available */ } };
export function Copy({ text }: { text: string }) { return <code className="copy" onClick={() => copyText(text)} title="Click to copy">{text}</code>; }
