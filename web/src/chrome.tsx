import type { ReactNode } from 'react';

/** Small line icons, drawn on a 24 grid. One stroke weight everywhere so they read as a set. */
const PATHS: Record<string, ReactNode> = {
  home: <path d="M3 11.5 12 4l9 7.5V20a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z" />,
  'Service Desk': <><path d="M3 9a2 2 0 0 0 0 6v3a1 1 0 0 0 1 1h16a1 1 0 0 0 1-1v-3a2 2 0 0 1 0-6V6a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1z" /><path d="M14 5v3m0 3v2m0 3v3" /></>,
  Assets: <><rect x="4" y="5" width="16" height="11" rx="2" /><path d="M2 20h20" /></>,
  Monitoring: <path d="M3 12h4l3-8 4 16 3-8h4" />,
  'AD Manager': <><circle cx="9.5" cy="8" r="3.5" /><path d="M3.5 20v-1a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v1" /><path d="M16 4.5a3.5 3.5 0 0 1 0 7M20.5 19v-1a4 4 0 0 0-3-3.9" /></>,
  Vault: <><rect x="5" y="11" width="14" height="10" rx="2" /><path d="M8 11V8a4 4 0 0 1 8 0v3" /><circle cx="12" cy="16" r="1" /></>,
  Backup: <><ellipse cx="12" cy="6" rx="8" ry="3" /><path d="M4 6v6c0 1.7 3.6 3 8 3s8-1.3 8-3V6M4 12v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6" /></>,
  signout: <><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4" /><path d="m16 17 5-5-5-5M21 12H9" /></>,
};

export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  return <svg className="ico" width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{PATHS[name] ?? PATHS.home}</svg>;
}

/** The DeskAsk mark: a speech bubble (the "ask") on a tile of light. */
export function Logo({ size = 34 }: { size?: number }) {
  return <svg width={size} height={size} viewBox="0 0 34 34" aria-hidden="true">
    <defs>
      <linearGradient id="lg" x1="4" y1="2" x2="30" y2="32"><stop offset="0" stopColor="#2fe0cf" /><stop offset="1" stopColor="#5b6bff" /></linearGradient>
      <linearGradient id="lh" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#fff" stopOpacity=".55" /><stop offset="1" stopColor="#fff" stopOpacity="0" /></linearGradient>
    </defs>
    <rect width="34" height="34" rx="10" fill="url(#lg)" />
    <rect width="34" height="17" rx="10" fill="url(#lh)" />
    <path d="M10 12.5A3.5 3.5 0 0 1 13.5 9h7a3.5 3.5 0 0 1 3.5 3.5v5a3.5 3.5 0 0 1-3.5 3.5H17l-4.2 3.4V21h-.3A3.5 3.5 0 0 1 10 17.5z" fill="none" stroke="#fff" strokeWidth="1.9" strokeLinejoin="round" />
    <circle cx="17" cy="15" r="1.25" fill="#fff" />
  </svg>;
}
