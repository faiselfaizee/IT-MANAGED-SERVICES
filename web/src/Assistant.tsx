import { useEffect, useRef, useState } from 'react';
import { api } from './http';

type Table = { title: string; headers: string[]; rows: string[][]; links: string[]; more: boolean };
type Answer = { text: string; count: number | null; tables: Table[]; suggestions: string[] };
type Turn = { q: string; a?: Answer; error?: string };

const START = ['How many servers do we have?', 'Which assets have a warranty ending in 60 days?', 'Which computers are low on disk space?', 'Which devices are down?',
  'Show open critical alerts', 'Show overdue tickets', 'Tickets with no engineer', 'Which computers have Chrome installed?', 'What is affected if the main switch fails?', 'Computers not reporting for 7 days'];

const csvCell = (s: string) => '"' + s.replace(/"/g, '""') + '"';
export function tableCsv(t: Table): string { return [t.headers, ...t.rows].map((r) => r.map(csvCell).join(',')).join('\r\n'); }

function Result({ t }: { t: Table }) {
  const download = () => {
    const url = URL.createObjectURL(new Blob(['\ufeff' + tableCsv(t)], { type: 'text/csv;charset=utf-8' }));
    const a = document.createElement('a'); a.href = url; a.download = t.title.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') + '.csv'; a.click(); URL.revokeObjectURL(url);
  };
  return <div className="ask-table">
    <div className="ask-table-head"><strong>{t.title}</strong><span className="no-print"><button onClick={download}>Download CSV</button> <button onClick={() => window.print()}>Print or save as PDF</button></span></div>
    <table><thead><tr>{t.headers.map((h) => <th key={h}>{h}</th>)}</tr></thead><tbody>
      {t.rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j}>{j === 0 && t.links[i] ? <a href={t.links[i]}>{c}</a> : c}</td>)}</tr>)}</tbody></table>
    {t.more && <p className="muted small">Only the first 100 rows are shown. Ask a narrower question to see the rest.</p>}
  </div>;
}

/** Ask DeskAsk (6.23.0): plain-English questions about assets, tickets, alerts and dependencies, answered from your own data. */
export default function Assistant() {
  const [q, setQ] = useState('');
  const [turns, setTurns] = useState<Turn[]>([]);
  const [busy, setBusy] = useState(false);
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { end.current?.scrollIntoView?.({ block: 'end' }); }, [turns.length, busy]);

  const ask = async (text: string) => {
    const question = text.trim(); if (!question || busy) return;
    setQ(''); setBusy(true); setTurns((t) => [...t, { q: question }]);
    try {
      const a = await api<Answer>('/api/assets/assistant/ask', { method: 'POST', body: { q: question } });
      setTurns((t) => t.map((x, i) => i === t.length - 1 ? { ...x, a } : x));
    } catch (e) {
      setTurns((t) => t.map((x, i) => i === t.length - 1 ? { ...x, error: e instanceof Error ? e.message : 'Something went wrong.' } : x));
    } finally { setBusy(false); }
  };

  return <div className="page ask">
    <div className="page-head"><h1>Ask DeskAsk</h1><span className="muted">Ask in plain English about your assets, tickets, alerts and what depends on what. Answers come only from your own data, and only for the customers you may see.</span></div>
    <div className="ask-log" aria-live="polite">
      {turns.length === 0 && <div className="ask-start"><p className="muted">Try one of these:</p><div className="ask-chips">{START.map((s) => <button key={s} className="ask-chip" onClick={() => void ask(s)}>{s}</button>)}</div></div>}
      {turns.map((t, i) => <div key={i} className="ask-turn">
        <p className="ask-q">{t.q}</p>
        {t.error ? <p role="alert" className="err">{t.error}</p> : !t.a ? <p className="muted">Looking…</p> : <div className="ask-a">
          <p>{t.a.count != null && t.a.tables.length > 0 ? <strong className="ask-count">{t.a.count}</strong> : null} {t.a.text}</p>
          {t.a.tables.map((x) => <Result key={x.title} t={x} />)}
          {t.a.suggestions.length > 0 && <div className="ask-chips no-print">{t.a.suggestions.map((s) => <button key={s} className="ask-chip" onClick={() => void ask(s)}>{s}</button>)}</div>}
        </div>}
      </div>)}
      <div ref={end} />
    </div>
    <form className="ask-form no-print" onSubmit={(e) => { e.preventDefault(); void ask(q); }}>
      <input value={q} onChange={(e) => setQ(e.target.value)} maxLength={300} placeholder="For example: which computers have less than 10% free disk space?" aria-label="Your question" autoFocus />
      <button className="primary" disabled={busy || q.trim() === ''}>Ask</button>
    </form>
  </div>;
}
