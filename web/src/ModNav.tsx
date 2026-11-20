import type { ReactNode } from 'react';

/** One entry of a module's side menu. `badge` is a small count shown beside it (for example waiting requests). */
export type NavItem = { key: string; label: string; badge?: number };
export type NavGroup = { group: string; items: NavItem[] };

/**
 * The side menu every module uses, in the same order everywhere: Overview, the module's working groups, Reports, Settings.
 * Groups with nothing the person may open are left out.
 */
export function ModNav({ title, base, groups, section, head, children }: { title: string; base: string; groups: NavGroup[]; section: string; head?: ReactNode; children: ReactNode }) {
  return <div className="modshell">
    <aside className="modnav" aria-label={title}>
      <div className="modnav-head"><strong>{title}</strong>{head}</div>
      {groups.filter((g) => g.items.length > 0).map((g) => <div key={g.group} className="modnav-group">
        {g.group && <h4>{g.group}</h4>}
        {g.items.map((i) => <a key={i.key} href={'#/' + base + (i.key ? '/' + i.key : '')} className={section === i.key ? 'on' : ''} aria-current={section === i.key ? 'page' : undefined}>
          {i.label}{i.badge ? <span className="pill">{i.badge}</span> : null}</a>)}
      </div>)}
    </aside>
    <main className="modmain">{children}</main>
  </div>;
}
