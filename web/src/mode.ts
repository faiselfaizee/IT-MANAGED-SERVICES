import { useEffect, useState } from 'react';
import { api, type Rec } from './http';

let cached: Promise<boolean> | null = null;
/** Forget the answer (after signing in as someone else, and in tests). */
export function resetMode() { cached = null; }

/**
 * Single-company mode: the licence is for an organisation that runs its own IT, so everything about several customer companies is hidden.
 * The answer is asked once per page load; until it arrives, and when it cannot be read, the normal screens show.
 */
export function useSingleCompany(): boolean {
  const [on, setOn] = useState(false);
  useEffect(() => {
    let live = true;
    cached ??= api<Rec>('/api/platform/mode').then((m) => m.singleCompany === true).catch(() => false);
    void cached.then((v) => { if (live) setOn(v); });
    return () => { live = false; };
  }, []);
  return on;
}
