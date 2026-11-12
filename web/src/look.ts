/** The look of the web app. Clean is the default because it is the quickest; Glass is the frosted one. */
export const LOOKS: [string, string][] = [['clean', 'Clean'], ['compact', 'Compact'], ['glass', 'Glass']];
const KEY = 'deskask.look';

export function currentLook(): string {
  try { const v = localStorage.getItem(KEY); if (v && LOOKS.some(([k]) => k === v)) return v; } catch { /* storage not available */ }
  return 'clean';
}
export function applyLook(look: string = currentLook()): void { document.documentElement.setAttribute('data-look', look); }
export function setLook(look: string): void {
  try { localStorage.setItem(KEY, look); } catch { /* storage not available */ }
  applyLook(look);
}
