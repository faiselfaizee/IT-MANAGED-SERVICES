import { useEffect, useState } from 'react';
import { useHash, useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import Domains from './Domains';
import Browse from './Browse';
import Reports from './Reports';
import Requests from './Requests';

const NAV: [string, string][] = [['', 'Browse'], ['reports', 'Reports'], ['requests', 'Account requests'], ['approvals', 'Approvals'], ['domains', 'Domains']];

/** Remembers which domain is open, so every screen works on the same one. */
export function useDomain(domains: Rec[]): [Rec | null, (id: number) => void] {
  const [id, setId] = useState<number>(() => Number(sessionStorage.getItem('ad.domain') ?? 0));
  const d = domains.find((x) => x.id === id) ?? domains.find((x) => x.enabled) ?? domains[0] ?? null;
  return [d, (n) => { setId(n); try { sessionStorage.setItem('ad.domain', String(n)); } catch { /* not kept */ } }];
}

export function DomainPick({ domains, value, onPick }: { domains: Rec[]; value: Rec | null; onPick: (id: number) => void }) {
  return <label className="inline">Domain <select aria-label="Domain" value={value?.id ?? ''} onChange={(e) => onPick(Number(e.target.value))}>
    {domains.map((d) => <option key={d.id} value={d.id}>{d.company ? d.company + ' · ' : ''}{d.name}</option>)}</select></label>;
}

/** The AD Manager module: browse and change a customer's Active Directory through the site controller. */
export default function AdApp() {
  const parts = useHash(); const section = parts[1] ?? '';
  const me = useLoad<Rec>('/api/platform/me');
  const list = useLoad<Rec[]>('/api/adsuite/domains');
  const [, setTick] = useState(0); useEffect(() => { setTick((n) => n + 1); }, [section]);
  const admin = me.data?.role === 'admin';
  const domains = (list.data ?? []) as Rec[];
  if (list.error || list.loading || !list.data) return <Loading loading={list.loading} error={list.error} />;
  const nav = NAV.filter(([k]) => k !== 'domains' || admin);
  let page;
  if (section === 'domains' && admin) page = <Domains domains={domains} onChanged={list.reload} />;
  else if (domains.length === 0) page = <Empty>{admin ? <>No directory is connected yet. <a href="#/admanager/domains">Add a domain</a>.</> : 'No directory has been shared with you yet. Ask your administrator.'}</Empty>;
  else if (section === 'reports') page = <Reports domains={domains} />;
  else if (section === 'requests') page = <Requests domains={domains} mode="requests" />;
  else if (section === 'approvals') page = <Requests domains={domains} mode="approvals" />;
  else page = <Browse domains={domains} />;
  return <>
    <nav className="sub" aria-label="AD Manager">{nav.map(([k, l]) => <a key={k} href={'#/admanager' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</nav>
    {page}
  </>;
}
