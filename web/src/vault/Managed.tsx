import { useState } from 'react';
import { api, useInterval, useLoad, type Rec } from '../http';
import { ago, Empty, fmtDate, Loading } from '../ui';
import * as S from './session';

const STATE: Record<string, [string, string]> = { ok: ['Up to date', 'ok'], rotating: ['Changing now', ''], failed: ['Change failed', 'bad'], unknown: ['Unsure', 'warn'] };

/** Every account DeskAsk changes the password of, with when it last and next happens. */
export default function Managed() {
  const list = useLoad<Rec>('/api/vault/managed');
  const items = (list.data?.items ?? []) as Rec[];
  useInterval(list.reload, items.some((i) => i.state === 'rotating') ? 4000 : 60_000);
  const titles = S.useTitles(items.map((i) => ({ id: Number(i.itemId), vaultId: Number(i.vaultId), titleEnc: String(i.titleEnc) })));
  const [err, setErr] = useState('');
  return <>
    <h1>Managed accounts</h1>
    <p className="muted">Passwords DeskAsk changes through the agent on each computer: Windows local accounts, Active Directory accounts (with the agent on a domain controller) and Linux accounts. To add one, open a server account linked to its asset and choose "Let DeskAsk change this password".</p>
    {err && <p className="err" role="alert">{err}</p>}
    <Loading loading={list.loading && !list.data} error={list.error} />
    {list.data && (items.length === 0 ? <Empty>No managed accounts yet.</Empty> : <div className="table-wrap"><table>
      <thead><tr><th>Account</th><th>Computer</th><th>Status</th><th>Last changed</th><th>Next</th><th /></tr></thead>
      <tbody>{items.map((m) => { const [w, tone] = STATE[m.state] ?? [m.state, '']; const v = S.vaultById(Number(m.vaultId)); return <tr key={m.itemId}>
        <td><a href={`#/vault?item=${m.itemId}`}>{titles[Number(m.itemId)] ?? '…'}</a><div className="muted small">{m.account} · {m.target === 'domain' ? 'Active Directory' : m.target === 'linux' ? 'Linux' : 'Windows local'} · {v?.name}</div></td>
        <td><a href={`#/assets/${m.assetId}`}>{m.asset || 'asset ' + m.assetId}</a></td>
        <td><span className={'badge ' + tone}>{w}</span>{m.lastError ? <div className="muted small">{m.lastError}</div> : null}</td>
        <td>{m.lastRotatedAt ? <span title={fmtDate(m.lastRotatedAt)}>{ago(m.lastRotatedAt)}</span> : <span className="muted">not yet</span>}</td>
        <td>{m.nextAt ? fmtDate(m.nextAt) : <span className="muted">when asked</span>}{m.resetAfterUse ? <div className="muted small">and after each use</div> : null}</td>
        <td>{v && S.rank(v.role) >= 3 && <button className="small" disabled={m.state === 'rotating'} onClick={async () => { setErr(''); try { await api(`/api/vault/items/${m.itemId}/rotate`, { method: 'POST', body: {} }); list.reload(); } catch (e) { setErr((e as Error).message); } }}>Change now</button>}</td>
      </tr>; })}</tbody></table></div>)}
  </>;
}
