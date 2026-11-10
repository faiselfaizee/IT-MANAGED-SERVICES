import { useHash, useLoad, type Rec } from '../http';
import Detail from './Detail';
import List from './List';
import NewTicket from './NewTicket';
import { People, Settings } from './Admin';

/** The Service Desk module: routes inside the #/servicedesk address. */
export default function ServiceDeskApp() {
  const parts = useHash(); const [, a] = parts;
  const me = useLoad<Rec>('/api/servicedesk/me');
  const m = me.data; const staff = !!m?.staff; const admin = m?.role === 'admin';
  const isId = /^\d+$/.test(a ?? '');
  let page;
  if (isId) page = <Detail id={a} />;
  else if (a === 'new') page = <NewTicket me={m} />;
  else if (a === 'settings' && admin) page = <Settings />;
  else if (a === 'people' && admin) page = <People />;
  else page = <List me={m} />;
  const nav: [string, string, boolean][] = [['', 'Tickets', true], ['new', 'New ticket', !!m?.canRaise], ['people', 'Customer users', admin], ['settings', 'Settings', admin]];
  const section = isId ? '' : a ?? '';
  return <>
    <nav className="sub" aria-label="Service Desk">{nav.filter(([, , show]) => show).map(([k, l]) => <a key={k} href={'#/servicedesk' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</nav>
    {me.error ? <p role="alert" className="err">{me.error}</p> : null}
    {m || me.error ? page : <p className="muted">Loading…</p>}
    {staff ? null : null}
  </>;
}
