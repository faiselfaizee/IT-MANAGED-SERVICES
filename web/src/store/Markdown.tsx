import type { ReactNode } from 'react';

/** A small, safe markdown renderer for the website's text pages: headings, paragraphs, lists, tables, bold, italic, links. No HTML is passed through. */
export function Markdown({ text }: { text: string }) {
  const lines = text.replace(/\r/g, '').split('\n');
  const out: ReactNode[] = [];
  let i = 0; let k = 0;
  while (i < lines.length) {
    const l = lines[i];
    if (!l.trim()) { i++; continue; }
    const h = l.match(/^(#{1,4})\s+(.*)$/);
    if (h) { const level = Math.min(4, h[1].length + 1); const H = ('h' + level) as 'h2'; out.push(<H key={k++}>{inline(h[2])}</H>); i++; continue; }
    if (/^\s*\|/.test(l)) {
      const rows: string[][] = [];
      while (i < lines.length && /^\s*\|/.test(lines[i])) { rows.push(lines[i].trim().replace(/^\||\|$/g, '').split('|').map((c) => c.trim())); i++; }
      const body = rows.filter((r) => !r.every((c) => /^:?-{3,}:?$/.test(c) || c === ''));
      const head = rows.length > 1 && rows[1].every((c) => /^:?-{3,}:?$/.test(c)) && rows[0].some((c) => c) ? rows[0] : null;
      const data = head ? body.slice(1) : body;
      out.push(<div className="md-table" key={k++}><table>{head && <thead><tr>{head.map((c, j) => <th key={j}>{inline(c)}</th>)}</tr></thead>}
        <tbody>{data.map((r, n) => <tr key={n}>{r.map((c, j) => j === 0 && !head ? <th key={j} scope="row">{inline(c)}</th> : <td key={j}>{inline(c)}</td>)}</tr>)}</tbody></table></div>);
      continue;
    }
    if (/^\s*(\d+\.|[-*])\s+/.test(l)) {
      const ordered = /^\s*\d+\./.test(l); const items: string[] = [];
      while (i < lines.length && /^\s*(\d+\.|[-*])\s+/.test(lines[i])) { items.push(lines[i].replace(/^\s*(\d+\.|[-*])\s+(\[[ x]\]\s*)?/, '')); i++; }
      const List = ordered ? 'ol' : 'ul';
      out.push(<List key={k++}>{items.map((t, n) => <li key={n}>{inline(t)}</li>)}</List>);
      continue;
    }
    const para: string[] = [];
    while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|\s*\||\s*(\d+\.|[-*])\s)/.test(lines[i])) { para.push(lines[i].trim()); i++; }
    out.push(<p key={k++}>{inline(para.join(' '))}</p>);
  }
  return <div className="md">{out}</div>;
}

const SAFE = /^(https?:|mailto:|tel:|#\/)/i;

function inline(s: string): ReactNode[] {
  const out: ReactNode[] = []; let k = 0;
  const re = /\*\*(.+?)\*\*|\*(.+?)\*|\[([^\]]+)\]\(([^)\s]+)\)|([A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,})|\\([[\]*_\\])/g;
  let last = 0; let m: RegExpExecArray | null;
  while ((m = re.exec(s))) {
    if (m.index > last) out.push(s.slice(last, m.index));
    if (m[1] != null) out.push(<strong key={k++}>{inline(m[1])}</strong>);
    else if (m[2] != null) out.push(<em key={k++}>{inline(m[2])}</em>);
    else if (m[3] != null) out.push(SAFE.test(m[4]) ? <a key={k++} href={m[4]} rel="noreferrer">{m[3]}</a> : m[3]);
    else if (m[5] != null) out.push(<a key={k++} href={'mailto:' + m[5]}>{m[5]}</a>);
    else if (m[6] != null) out.push(m[6]);
    last = re.lastIndex;
  }
  if (last < s.length) out.push(s.slice(last));
  return out;
}
