import { useState } from 'react';
import { go, useHash, useLoad, type Rec } from '../http';
import { Badge, Card, Empty, Loading } from '../ui';
import { Ring } from '../Rings';
import Domains from './Domains';
import Reports from './Reports';
import Requests from './Requests';
import { ObjectList, Panel, has } from './Objects';
import { NewUser } from './UserForm';
import { BulkCreate, BulkModify } from './Bulk';
import { Automation, Templates } from './Settings';
import { CreateOther } from './Create';

type Item = { key: string; label: string; admin?: boolean; right?: string };
const MENU: { group: string; items: Item[] }[] = [
  { group: '', items: [{ key: '', label: 'Overview' }] },
  { group: 'Management', items: [{ key: 'users', label: 'Users' }, { key: 'groups', label: 'Groups' }, { key: 'computers', label: 'Computers' }, { key: 'ous', label: 'OUs' }] },
  { group: 'Create', items: [{ key: 'new-user', label: 'New user', right: 'create' }, { key: 'new-other', label: 'New group or OU', right: 'create' }, { key: 'bulk-create', label: 'Users from a file', right: 'create' }, { key: 'bulk-modify', label: 'Change from a file', right: 'modify' }] },
  { group: 'Reports', items: [{ key: 'reports', label: 'All reports' }] },
  { group: 'Workflow', items: [{ key: 'requests', label: 'Account requests' }, { key: 'approvals', label: 'Approvals' }] },
  { group: 'Settings', items: [{ key: 'automation', label: 'Automation', admin: true }, { key: 'templates', label: 'User templates', admin: true }, { key: 'domains', label: 'Domains and delegation', admin: true }] },
];

/** Remembers which domain is open, so every screen works on the same one. */
export function useDomain(domains: Rec[]): [Rec | null, (id: number) => void] {
  const [id, setId] = useState<number>(() => { try { return Number(sessionStorage.getItem('ad.domain') ?? 0); } catch { return 0; } });
  const d = domains.find((x) => x.id === id) ?? domains.find((x) => x.enabled) ?? domains[0] ?? null;
  return [d, (n) => { setId(n); try { sessionStorage.setItem('ad.domain', String(n)); } catch { /* not kept */ } }];
}

export function DomainPick({ domains, value, onPick }: { domains: Rec[]; value: Rec | null; onPick: (id: number) => void }) {
  return <select aria-label="Domain" value={value?.id ?? ''} onChange={(e) => onPick(Number(e.target.value))}>
    {domains.map((d) => <option key={d.id} value={d.id}>{d.company ? d.company + ' · ' : ''}{d.name}</option>)}</select>;
}

function Overview({ domain }: { domain: Rec }) {
  const o = useLoad<Rec>(`/api/adsuite/domains/${domain.id}/overview`, [domain.id]);
  if (!o.data) return <Loading loading={o.loading} error={o.error} />;
  const u = o.data.users as Rec, a = o.data.activity as Rec, c = o.data.computers as Rec;
  return <>
    <h1>{String(domain.name)}</h1>
    <div className="rings">
      <Ring title="User accounts" parts={[{ key: 'on', label: 'Enabled', value: u.enabled - u.locked, tone: 'ok', href: '#/admanager/users' }, { key: 'lock', label: 'Locked', value: u.locked, tone: 'bad', href: '#/admanager/users' }, { key: 'off', label: 'Disabled', value: u.disabled, tone: 'none', href: '#/admanager/users' }]} />
      <Ring title="Sign-in activity" parts={[{ key: '30', label: 'Last 30 days', value: a.last30, tone: 'ok' }, { key: '90', label: '30 to 90 days', value: a.last90, tone: 'warn' }, { key: 'old', label: 'Over 90 days', value: a.older, tone: 'bad', href: '#/admanager/reports' }, { key: 'never', label: 'Never', value: a.never, tone: 'none' }]} />
      <Ring title="Computers" parts={[{ key: 'a', label: 'Active', value: c.active, tone: 'ok' }, { key: 's', label: 'Stale (90 days)', value: c.stale, tone: 'warn', href: '#/admanager/reports' }, { key: 'd', label: 'Disabled', value: c.disabled, tone: 'none' }]} />
    </div>
    <div className="tiles">
      <a className="tile-s" href="#/admanager/reports"><strong>{String(u.privileged)}</strong><span>Privileged accounts</span></a>
      <a className="tile-s" href="#/admanager/reports"><strong>{String(u.expired)}</strong><span>Expired accounts</span></a>
      <a className="tile-s" href="#/admanager/approvals"><strong>{String(o.data.waiting)}</strong><span>Waiting for approval</span></a>
    </div>
    {o.data.truncated && <p className="muted small">Counted the first 20,000 objects only.</p>}
  </>;
}

/** The AD Manager module: manage a customer's Active Directory through its site controller. */
export default function AdApp() {
  const parts = useHash(); const section = parts[1] ?? '';
  const me = useLoad<Rec>('/api/platform/me');
  const list = useLoad<Rec[]>('/api/adsuite/domains');
  const domains = (list.data ?? []) as Rec[];
  const [domain, pick] = useDomain(domains);
  const [open, setOpen] = useState('');
  const admin = me.data?.role === 'admin';
  if (list.error || list.loading || !list.data) return <Loading loading={list.loading} error={list.error} />;
  const may = (i: Item) => (!i.admin || admin) && (!i.right || admin || has(domain, i.right));

  let page;
  if (section === 'domains' && admin) page = <Domains domains={domains} onChanged={list.reload} />;
  else if (!domain) page = <Card><Empty>{admin ? <>No directory is connected yet. <a href="#/admanager/domains">Add a domain</a>.</> : 'No directory has been shared with you yet. Ask your administrator.'}</Empty></Card>;
  else if (!domain.enabled) page = <Card><p className="err">This domain is switched off.</p></Card>;
  else {
    const kinds: Record<string, 'user' | 'group' | 'computer' | 'ou'> = { users: 'user', groups: 'group', computers: 'computer', ous: 'ou' };
    if (kinds[section]) page = <div className={open ? 'masterdetail open' : 'masterdetail'}>
      <ObjectList key={section + domain.id} domain={domain} kind={kinds[section]} open={open} onOpen={setOpen} />
      {open && <Panel key={open} domain={domain} dn={open} onChanged={() => undefined} onOpen={setOpen} onClose={() => setOpen('')} />}</div>;
    else if (section === 'new-user') page = <><h1>New user</h1><NewUser domain={domain} onDone={(dn) => { setOpen(dn); go('admanager/users'); }} /></>;
    else if (section === 'new-other') page = <><h1>New group or OU</h1><CreateOther domain={domain} /></>;
    else if (section === 'bulk-create') page = <><h1>Users from a file</h1><BulkCreate domain={domain} /></>;
    else if (section === 'bulk-modify') page = <><h1>Change users from a file</h1><BulkModify domain={domain} /></>;
    else if (section === 'reports') page = <><h1>Reports</h1><Reports domains={domains} /></>;
    else if (section === 'requests') page = <><h1>Account requests</h1><Requests domains={domains} mode="requests" /></>;
    else if (section === 'approvals') page = <><h1>Approvals</h1><Requests domains={domains} mode="approvals" /></>;
    else if (section === 'automation' && admin) page = <><h1>Automation</h1><Automation domain={domain} /></>;
    else if (section === 'templates' && admin) page = <><h1>User templates</h1><Templates domain={domain} /></>;
    else page = <Overview domain={domain} />;
  }
  return <div className="modshell">
    <aside className="modnav" aria-label="AD Manager">
      <div className="modnav-head"><strong>AD Manager</strong>
        {domains.length > 0 && <DomainPick domains={domains} value={domain} onPick={(n) => { pick(n); setOpen(''); }} />}
        {domain && !domain.enabled && <Badge tone="bad">Off</Badge>}</div>
      {MENU.map((g) => { const items = g.items.filter(may); return items.length === 0 ? null : <div key={g.group} className="modnav-group">
        {g.group && <h4>{g.group}</h4>}
        {items.map((i) => <a key={i.key} href={'#/admanager' + (i.key ? '/' + i.key : '')} className={section === i.key ? 'on' : ''} onClick={() => setOpen('')}>{i.label}</a>)}</div>; })}
    </aside>
    <main className="modmain">{page}</main>
  </div>;
}
