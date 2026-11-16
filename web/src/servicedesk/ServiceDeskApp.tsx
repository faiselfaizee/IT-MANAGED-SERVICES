import { useHash, useLoad, type Rec } from '../http';
import Detail from './Detail';
import List from './List';
import NewTicket from './NewTicket';
import { People, Settings } from './Admin';
import Integrations from './Integrations';
import Reports from './Reports';
import { ChangeDetail, ChangeList, NewChange } from './Changes';
import { NewProblem, ProblemDetail, ProblemList } from './Problems';
import { Approvals, Catalog, CatalogAdmin, ChangeBoard, ChangeCalendar, KnownErrors, StandardChanges } from './Itil';

/** The Service Desk module: routes inside the #/servicedesk address. */
export default function ServiceDeskApp() {
  const parts = useHash(); const [, a, b] = parts;
  const me = useLoad<Rec>('/api/servicedesk/me');
  const m = me.data; const staff = !!m?.staff; const admin = m?.role === 'admin'; const sees = staff || m?.role === 'viewer';
  const isId = /^\d+$/.test(a ?? '');
  let page;
  if (isId) page = <Detail id={a} />;
  else if (a === 'changes' && sees) page = b === 'new' && staff ? <NewChange /> : /^\d+$/.test(b ?? '') ? <ChangeDetail id={b} staff={staff} admin={admin} myId={m?.id} /> : <ChangeList staff={staff} />;
  else if (a === 'problems' && sees) page = b === 'new' && staff ? <NewProblem /> : /^\d+$/.test(b ?? '') ? <ProblemDetail id={b} staff={staff} /> : <ProblemList staff={staff} />;
  else if (a === 'incidents') page = <List key="incidents" me={m} kind="incident" />;
  else if (a === 'major') page = <List key="major" me={m} kind="incident" major />;
  else if (a === 'requests') page = <List key="requests" me={m} kind="request" />;
  else if (a === 'new') page = <NewTicket me={m} />;
  else if (a === 'catalog') page = <Catalog staff={staff} />;
  else if (a === 'approvals' && staff) page = <Approvals />;
  else if (a === 'known-errors' && sees) page = <KnownErrors />;
  else if (a === 'calendar' && sees) page = <ChangeCalendar />;
  else if (a === 'standard' && sees) page = <StandardChanges admin={admin} />;
  else if (a === 'catalog-items' && admin) page = <><h1>Catalogue items</h1><CatalogAdmin /></>;
  else if (a === 'cab' && admin) page = <><h1>Change advisory board</h1><ChangeBoard /></>;
  else if (a === 'settings' && admin) page = <Settings />;
  else if (a === 'reports' && (sees || m?.role === 'cadmin')) page = <Reports staff={sees} />;
  else if (a === 'connections' && admin) page = <Integrations />;
  else if (a === 'people' && admin) page = <People />;
  else page = <List key="all" me={m} />;
  const menu: { group: string; items: [string, string, boolean][] }[] = [
    { group: '', items: [['', 'All tickets', true], ['new', 'New ticket', !!m?.canRaise]] },
    { group: 'Incidents', items: [['incidents', 'Incidents', true], ['major', 'Major incidents', sees]] },
    { group: 'Service requests', items: [['catalog', 'Service catalogue', !!m?.canRaise || staff], ['requests', 'Requests', true], ['approvals', 'Approvals', staff]] },
    { group: 'Problems', items: [['problems', 'Problems', sees], ['known-errors', 'Known errors', sees]] },
    { group: 'Changes', items: [['changes', 'Changes', sees], ['calendar', 'Change calendar', sees], ['standard', 'Standard changes', sees]] },
    { group: 'Insight', items: [['reports', 'Reports', sees || m?.role === 'cadmin']] },
    { group: 'Admin', items: [['catalog-items', 'Catalogue items', admin], ['cab', 'Change board', admin], ['settings', 'Settings', admin], ['connections', 'Connections', admin], ['people', 'People', admin]] },
  ];
  const section = isId ? '' : a ?? '';
  return <>
    <div className="modshell">
      <aside className="modnav" aria-label="Service Desk">
        <div className="modnav-head"><strong>Service Desk</strong></div>
        {menu.map((g) => { const items = g.items.filter(([, , show]) => show); return items.length === 0 ? null : <div key={g.group} className="modnav-group">
          {g.group && <h4>{g.group}</h4>}
          {items.map(([k, l]) => <a key={k} href={'#/servicedesk' + (k ? '/' + k : '')} className={section === k ? 'on' : ''}>{l}</a>)}</div>; })}
      </aside>
      <main className="modmain">
        {me.error ? <p role="alert" className="err">{me.error}</p> : null}
        {m || me.error ? page : <p className="muted">Loading…</p>}
      </main>
    </div>
  </>;
}
