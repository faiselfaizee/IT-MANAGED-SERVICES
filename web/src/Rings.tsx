import { useEffect, useRef, useState } from 'react';

/** One part of a ring: a count, a name, the state colour it carries, and where clicking it leads. */
export type Part = { key: string; label: string; value: number; tone: 'ok' | 'warn' | 'bad' | 'info' | 'none'; href?: string };

const R = 42, W = 12, C = 2 * Math.PI * R, GAP = 2;

/** True when the person asked for less motion, or the page cannot animate (tests, old browsers): then everything shows at once. */
export function stillMotion(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function' || typeof window.requestAnimationFrame !== 'function') return true;
  return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
}

const ease = (t: number) => 1 - Math.pow(1 - t, 3);

/**
 * The values to draw right now. On first show they grow from zero, so a ring sweeps round and its numbers count up; when the values
 * change later they move from the old ones to the new ones. delay staggers several rings on one page.
 */
export function useAnimated(values: number[], delay = 0, ms = 900): number[] {
  const [now, setNow] = useState<number[]>(() => (stillMotion() ? values : values.map(() => 0)));
  const from = useRef<number[]>(now);
  const key = values.join(',');
  useEffect(() => {
    if (stillMotion()) { from.current = values; setNow(values); return; }
    const start = from.current.length === values.length ? from.current : values.map(() => 0);
    let raf = 0; let t0 = 0; let first = true;
    const step = (ts: number) => {
      if (first) { t0 = ts + (from.current.every((x) => x === 0) ? delay : 0); first = false; }
      const t = Math.min(1, Math.max(0, (ts - t0) / ms));
      const cur = values.map((v, i) => start[i] + (v - start[i]) * ease(t));
      from.current = cur; setNow(cur);
      if (t < 1) raf = requestAnimationFrame(step); else from.current = values;
    };
    raf = requestAnimationFrame(step);
    return () => cancelAnimationFrame(raf);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, delay, ms]);
  return now;
}

function arcs(parts: Part[], vals: number[], total: number) {
  let at = 0;
  return parts.map((p, i) => {
    const len = total > 0 ? (vals[i] / total) * C : 0;
    const gap = len > 0 && parts.filter((_, j) => vals[j] > 0).length > 1 ? Math.min(GAP, len / 2) : 0;
    const a = { p, dash: Math.max(len - gap, 0), offset: -at };
    at += len; return a;
  }).filter((a) => a.dash > 0.01);
}

/**
 * A ring of a few parts with the total in the middle, and a legend that gives every count as text, so nothing is read from colour alone.
 * Hovering a part or its legend row lifts both; a legend row with an address opens the matching list.
 */
export function Ring({ title, parts, unit, empty, delay = 0 }: { title: string; parts: Part[]; unit?: string; empty?: string; delay?: number }) {
  const [hot, setHot] = useState<string | null>(null);
  const total = parts.reduce((s, p) => s + p.value, 0);
  const vals = useAnimated(parts.map((p) => p.value), delay);
  const shownTotal = Math.round(vals.reduce((s, v) => s + v, 0));
  const summary = total === 0 ? `${title}: ${empty ?? 'nothing yet'}` : `${title}: ` + parts.map((p) => `${p.value} ${p.label.toLowerCase()}`).join(', ');
  return <figure className="ring">
    <figcaption>{title}</figcaption>
    <div className="ring-body">
      <svg viewBox="0 0 110 110" width="110" height="110" role="img" aria-label={summary}>
        <circle cx="55" cy="55" r={R} fill="none" className="ring-track" strokeWidth={W} />
        {arcs(parts, vals, total).map(({ p, dash, offset }) => <circle key={p.key} cx="55" cy="55" r={R} fill="none" strokeWidth={hot === p.key ? W + 3 : W}
          className={`ring-arc tone-${p.tone}${hot && hot !== p.key ? ' dim' : ''}`} strokeDasharray={`${dash} ${C - dash}`} strokeDashoffset={offset}
          transform="rotate(-90 55 55)" onMouseEnter={() => setHot(p.key)} onMouseLeave={() => setHot(null)}>
          <title>{`${p.label}: ${p.value} of ${total}`}</title>
        </circle>)}
        <text x="55" y="53" textAnchor="middle" className="ring-total">{shownTotal}</text>
        <text x="55" y="70" textAnchor="middle" className="ring-unit">{unit ?? ''}</text>
      </svg>
      <ul className="ring-legend">
        {parts.map((p, i) => {
          const body = <><i className={`dot tone-${p.tone}`} aria-hidden="true" /><span className="name">{p.label}</span><b className="num">{Math.round(vals[i] ?? p.value)}</b></>;
          return <li key={p.key} className={hot === p.key ? 'hot' : ''} onMouseEnter={() => setHot(p.key)} onMouseLeave={() => setHot(null)}>
            {p.href ? <a href={p.href}>{body}</a> : <span>{body}</span>}
          </li>;
        })}
      </ul>
    </div>
    {total === 0 && empty && <p className="muted ring-empty">{empty}</p>}
  </figure>;
}

/** A small ring without a legend, for a customer tile: the number in the middle and the parts named in its label. */
export function MiniRing({ parts, label, delay = 0 }: { parts: Part[]; label: string; delay?: number }) {
  const total = parts.reduce((s, p) => s + p.value, 0);
  const vals = useAnimated(parts.map((p) => p.value), delay, 800);
  const summary = `${label}: ` + (total === 0 ? 'none' : parts.map((p) => `${p.value} ${p.label.toLowerCase()}`).join(', '));
  return <svg viewBox="0 0 110 110" width="76" height="76" role="img" aria-label={summary} className="mini-ring">
    <circle cx="55" cy="55" r={R} fill="none" className="ring-track" strokeWidth={W + 2} />
    {arcs(parts, vals, total).map(({ p, dash, offset }) => <circle key={p.key} cx="55" cy="55" r={R} fill="none" strokeWidth={W + 2} className={`ring-arc tone-${p.tone}`}
      strokeDasharray={`${dash} ${C - dash}`} strokeDashoffset={offset} transform="rotate(-90 55 55)" />)}
    <text x="55" y="64" textAnchor="middle" className="ring-total mini">{Math.round(vals.reduce((s, v) => s + v, 0))}</text>
  </svg>;
}

/** A number that counts up to its value on first show and moves to a new value when it changes. */
export function CountUp({ value, delay = 0 }: { value: number; delay?: number }) {
  const [v] = useAnimated([value], delay, 800);
  return <>{Math.round(v)}</>;
}
