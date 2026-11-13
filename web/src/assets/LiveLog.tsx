import { useEffect, useRef } from 'react';
import type { Rec } from '../http';

/** A record of what is happening now: one line per step, newest at the bottom, kept in view while it grows (unless the reader scrolled up). */
export function LiveLog({ items, running, empty }: { items: Rec[]; running?: boolean; empty?: string }) {
  const box = useRef<HTMLDivElement>(null);
  const stick = useRef(true);
  useEffect(() => { const el = box.current; if (el && stick.current) el.scrollTop = el.scrollHeight; }, [items.length, running]);
  return <div className="livelog" role="log" aria-live="polite" aria-label="Live log" ref={box}
    onScroll={(e) => { const el = e.currentTarget; stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 24; }}>
    {items.length === 0 && <div className="ll muted"><time /><span>{empty ?? 'Nothing yet.'}</span></div>}
    {items.map((l) => <div key={l.seq} className={'ll ' + String(l.level ?? 'info')}><time>{new Date(l.at).toLocaleTimeString()}</time><span>{l.text}</span></div>)}
    {running && <div className="ll wait"><time /><span>working<i className="dots" aria-hidden="true" /></span></div>}
  </div>;
}
