import { useLoad, type Rec } from '../http';

/** Pages and asset tabs that belong to Asset management itself (lifecycle, remote tools); a tenant with Monitoring only does not have them. */
export const LIFECYCLE_PAGES = ['people', 'stock', 'software', 'licenses', 'amc', 'rentals', 'renewals', 'vendors', 'commands', 'remote-log', 'reports', 'delete-requests'];
export const LIFECYCLE_TABS = ['services', 'commands', 'shell', 'ise', 'remote'];

/** True when the licence has Monitoring but not Asset management: the Assets screens then show the shared device list only. */
export function useAssetsLite(): boolean {
  const mods = useLoad<Rec[]>('/api/platform/modules');
  if (!Array.isArray(mods.data)) return false;
  const has = (k: string) => mods.data!.some((m) => m.key === k && (m.licensed || m.readOnly));
  return !has('assets') && has('monitoring');
}
