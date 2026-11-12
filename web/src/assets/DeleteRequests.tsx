import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { Act, Badge, Empty, Err, Loading, fmtDate } from '../ui';

/** Requests to delete assets: anyone who may edit assets can ask, people with the delete right decide. */
export default function DeleteRequests() {
  const [status, setStatus] = useState('pending'); const { data, error, loading, reload } = useLoad<Rec>(`/api/assets/delete-requests?status=${status}`);
  const [note, setNote] = useState<Record<number, string>>({});
  const items = (data?.items ?? []) as Rec[];
  return <>
    <h1>Delete requests</h1>
    <div className="tools"><select value={status} onChange={(e) => setStatus(e.target.value)} aria-label="Status"><option value="pending">Waiting</option><option value="">All</option><option value="approved">Approved</option><option value="rejected">Rejected</option></select></div>
    <Loading loading={loading && !data} error={error} />
    {data && (items.length === 0 ? <Empty>No requests.</Empty> : <table><thead><tr><th>Asset</th><th>Asked by</th><th>Reason</th><th>Status</th><th /></tr></thead><tbody>
      {items.map((q) => <tr key={q.id}><td>{q.status === 'approved' ? <>{q.name} <span className="mono">{q.tag}</span></> : <a href={`#/assets/${q.assetId}`}>{q.name} <span className="mono">{q.tag}</span></a>}</td>
        <td>{q.requestedBy}<br /><span className="muted">{fmtDate(q.createdAt)}</span></td><td>{q.reason}{q.note && <><br /><span className="muted">Note: {q.note}</span></>}</td>
        <td><Badge tone={q.status === 'approved' ? 'ok' : q.status === 'rejected' ? 'bad' : 'warn'}>{q.status === 'pending' ? 'waiting' : q.status}</Badge>{q.decidedBy && <><br /><span className="muted">{q.decidedBy}</span></>}</td>
        <td>{q.status === 'pending' && (q.canDecide ? <>
          <input placeholder="Note (optional)" value={note[q.id] ?? ''} onChange={(e) => setNote({ ...note, [q.id]: e.target.value })} />
          <Act label="Approve and delete" danger confirm={`Delete ${q.name} (${q.tag}) for good?`} run={async () => { await api(`/api/assets/delete-requests/${q.id}/approve`, { body: { note: note[q.id] ?? '' } }); reload(); }} />
          <Act label="Reject" run={async () => { await api(`/api/assets/delete-requests/${q.id}/reject`, { body: { note: note[q.id] ?? '' } }); reload(); }} /></> : <span className="muted">Waiting for an approver</span>)}</td></tr>)}</tbody></table>)}
    <Err text="" />
  </>;
}
