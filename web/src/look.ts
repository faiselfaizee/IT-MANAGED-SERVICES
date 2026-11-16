/** The look of the web app: Standard, or Compact for more rows on a screen. (The frosted Glass look of v2–v4 was retired in v5.) */
export const LOOKS: [string, string][] = [['standard', 'Standard'], ['compact', 'Compact']];
const KEY = 'deskask.look';

export function currentLook(): string {
  try { const v = localStorage.getItem(KEY); if (v && LOOKS.some(([k]) => k === v)) return v; } catch { /* storage not available */ }
  return 'standard';
}
export function applyLook(look: string = currentLook()): void { document.documentElement.setAttribute('data-look', look); }
export function setLook(look: string): void {
  try { localStorage.setItem(KEY, look); } catch { /* storage not available */ }
  applyLook(look);
}
