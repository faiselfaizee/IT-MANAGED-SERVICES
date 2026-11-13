import { useState } from 'react';

/** One part of a ring: a count, a name, the state colour it carries, and where clicking it leads. */
export type Part = { key: string; label: string; value: number; tone: 'ok' | 'warn' | 'bad' | 'info' | 'none'; href?: string };

const R = 42, W = 12, C = 2 * Math.PI * R, GAP = 2;

/**
 * A ring of a few parts with the total in the middle, and a legend that gives every count as text, so nothing is read from colour alone.
 * Hovering a part or its legend row lifts both; a legend row with an address opens the matching list.
 */
export function Ring({ title, parts, unit, empty }: { title: string; parts: Part[]; unit?: string; empty?: string }) {
  const [hot, setHot] = useState<string | null>(null);
  const total = parts.reduce((s, p) => s + p.value, 0);
  const shown = parts.filter((p) => p.value > 0);
  let at = 0;
  const arcs = shown.map((p) => {
    const len = (p.value / total) * C; const gap = shown.length > 1 ? Math.min(GAP, len / 2) : 0;
    const a = { p, dash: Math.max(len - gap, 0.01), offset: -at };
    at += len; return a;
  });
  const summary = total === 0 ? `${title}: ${empty ?? 'nothing yet'}` : `${title}: ` + parts.map((p) => `${p.value} ${p.label.toLowerCase()}`).join(', ');
  return <figure className="ring">
    <figcaption>{title}</figcaption>
    <div className="ring-body">
      <svg viewBox="0 0 110 110" width="110" height="110" role="img" aria-label={summary}>
        <circle cx="55" cy="55" r={R} fill="none" className="ring-track" strokeWidth={W} />
        {arcs.map(({ p, dash, offset }) => <circle key={p.key} cx="55" cy="55" r={R} fill="none" strokeWidth={hot === p.key ? W + 3 : W}
          className={`ring-arc tone-${p.tone}${hot && hot !== p.key ? ' dim' : ''}`} strokeDasharray={`${dash} ${C - dash}`} strokeDashoffset={offset}
          transform="rotate(-90 55 55)" onMouseEnter={() => setHot(p.key)} onMouseLeave={() => setHot(null)}>
          <title>{`${p.label}: ${p.value} of ${total}`}</title>
        </circle>)}
        <text x="55" y="53" textAnchor="middle" className="ring-total">{total}</text>
        <text x="55" y="70" textAnchor="middle" className="ring-unit">{unit ?? ''}</text>
      </svg>
      <ul className="ring-legend">
        {parts.map((p) => {
          const body = <><i className={`dot tone-${p.tone}`} aria-hidden="true" /><span className="name">{p.label}</span><b className="num">{p.value}</b></>;
          return <li key={p.key} className={hot === p.key ? 'hot' : ''} onMouseEnter={() => setHot(p.key)} onMouseLeave={() => setHot(null)}>
            {p.href ? <a href={p.href}>{body}</a> : <span>{body}</span>}
          </li>;
        })}
      </ul>
    </div>
    {total === 0 && empty && <p className="muted ring-empty">{empty}</p>}
  </figure>;
}
