import { useState } from 'react';
import { useLoad, type Rec } from '../http';
import { Empty, Loading } from '../ui';
import * as S from './session';

const ACTIONS: Record<string, string> = { viewed: 'Opened', revealed: 'Showed a secret', copied: 'Copied', opened: 'Opened the address', exported: 'Exported' };

/** Vault › Reports › Access report: who used which item, in the vaults this person edits or manages. Titles are decrypted in the browser. */
export default function Access() {
  const [days, setDays] = useState('30'); const [action, setAction] = useState(''); const [user, setUser] = useState(''); const [typed, setTyped] = useState('');
  const qs = `?days=${days}&action=${action}&user=${encodeURIComponent(user)}`;
  const { data, error, loading } = useLoad<Rec>('/api/vault/reports/access' + qs, [qs]);
  const items = (data?.items ?? []) as Rec[];
  const titles = S.useTitles(items.filter((x) => x.titleEnc).map((x) => ({ id: Number(x.itemId), vaultId: Number(x.vaultId), titleEnc: String(x.titleEnc) })));
  const name = (x: Rec) => titles[Number(x.itemId)] ?? (x.titleEnc ? '…' : '(deleted item)');
  const csv = () => {
    const cell = (v: string) => /^[=+\-@\t\r]/.test(v) ? `'${v}` : /[",\r\n]/.test(v) ? `"${v.replace(/"/g, '""')}"` : v;
    const lines = [['When', 'Who', 'What', 'Field', 'Item', 'Vault'].join(',')].concat(items.map((x) => [new Date(String(x.at)).toLocaleString(), String(x.user), ACTIONS[String(x.action)] ?? String(x.action), String(x.field), name(x), String(x.vault)].map(cell).join(',')));
    const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv' })); a.download = 'vault-access-report.csv'; a.click(); URL.revokeObjectURL(a.href);
  };
  return <>
    <h1>Access report</h1>
    <p className="muted">Who opened, showed, copied or exported which item, in the vaults you edit or manage. Item names are decrypted in your browser and never sent to the server.</p>
    <form className="tools" onSubmit={(e) => { e.preventDefault(); setUser(typed.trim()); }}>
      <select value={days} onChange={(e) => setDays(e.target.value)} aria-label="Period"><option value="1">Last 24 hours</option><option value="7">Last 7 days</option><option value="30">Last 30 days</option><option value="90">Last 90 days</option><option value="366">Last year</option></select>
      <select value={action} onChange={(e) => setAction(e.target.value)} aria-label="What was done"><option value="">Everything</option>{Object.entries(ACTIONS).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select>
      <input type="search" value={typed} onChange={(e) => setTyped(e.target.value)} placeholder="Person" aria-label="Person" />
      <button type="submit">Find</button>
      <button type="button" onClick={csv} disabled={items.length === 0}>Export (CSV)</button>
    </form>
    <Loading loading={loading && !data} error={error} />
    {data && (Number(data.vaults) === 0 ? <Empty>You do not edit or manage any vault, so there is nothing to show.</Empty>
      : items.length === 0 ? <Empty>No one used an item in this period.</Empty>
      : <table><thead><tr><th>When</th><th>Who</th><th>What</th><th>Item</th><th>Vault</th></tr></thead><tbody>
        {items.map((x) => <tr key={x.id}><td>{new Date(String(x.at)).toLocaleString()}</td><td>{x.user}</td><td>{ACTIONS[String(x.action)] ?? x.action}{x.field ? <span className="muted"> ({x.field})</span> : null}</td>
          <td>{x.deleted ? <span className="muted">{name(x)} (deleted)</span> : <a href={`#/vault?item=${x.itemId}`}>{name(x)}</a>}</td><td>{x.vault}</td></tr>)}</tbody></table>)}
    {items.length >= 2000 && <p className="muted">Showing the latest 2,000. Narrow the period or the person to see older ones.</p>}
  </>;
}
