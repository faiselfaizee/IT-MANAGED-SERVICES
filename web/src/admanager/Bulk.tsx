import { useState } from 'react';
import { api, useLoad, type Rec } from '../http';
import { csv } from '../vault/Tools';
import { Badge, Card, Empty, Err } from '../ui';
import { ALL_FIELDS, applyName, cleanSam } from './fields';
import { OuPicker } from './pickers';
import { act } from './Objects';

const NAME_COLS = ['first', 'last', 'initials', 'display', 'sam', 'upn', 'email', 'ou', 'password', 'groups', 'manager', 'expires'];
const ATTR_COLS = ALL_FIELDS.map((f) => f.k);
const known = new Map([...NAME_COLS, ...ATTR_COLS].map((c) => [c.toLowerCase(), c]));
// the column names people use in their sheets
const ALIAS: Record<string, string> = { firstname: 'first', 'first name': 'first', givenname: 'first', lastname: 'last', 'last name': 'last', surname: 'last', sn: 'last',
  displayname: 'display', samaccountname: 'sam', username: 'sam', 'user name': 'sam', login: 'sam', mail: 'email', userprincipalname: 'upn', jobtitle: 'title', phone: 'telephoneNumber', city: 'l', country: 'co' };

function readSheet(text: string): { rows: Rec[]; unknown: string[] } {
  const t = csv(text).filter((r) => r.some((c) => c.trim() !== ''));
  if (t.length < 2) return { rows: [], unknown: [] };
  const head = t[0].map((h) => { const k = h.trim().toLowerCase(); return ALIAS[k] ?? known.get(k) ?? ''; });
  const unknown = t[0].filter((_, i) => head[i] === '').map((h) => h.trim()).filter(Boolean);
  return { unknown, rows: t.slice(1).map((r) => { const o: Rec = {}; head.forEach((k, i) => { if (k && (r[i] ?? '').trim() !== '') o[k] = r[i].trim(); }); return o; }) };
}

function saveCsv(name: string, rows: string[][]) {
  const text = rows.map((r) => r.map((c) => (/[",\n\r]/.test(c) ? '"' + c.replace(/"/g, '""') + '"' : /^[=+\-@]/.test(c) ? "'" + c : c)).join(',')).join('\r\n');
  const a = document.createElement('a'); a.href = URL.createObjectURL(new Blob(['﻿' + text], { type: 'text/csv' })); a.download = name; a.click();
}

/** Create many accounts from a sheet: read it, check every row with the directory, then create them one by one. */
export function BulkCreate({ domain }: { domain: Rec }) {
  const templates = useLoad<Rec[]>(`/api/adsuite/domains/${domain.id}/templates`);
  const [rows, setRows] = useState<Rec[]>([]); const [unknown, setUnknown] = useState<string[]>([]); const [tid, setTid] = useState(0); const [ou, setOu] = useState('');
  const [check, setCheck] = useState<Rec | null>(null); const [out, setOut] = useState<Rec[]>([]); const [busy, setBusy] = useState(false); const [err, setErr] = useState('');
  const tpl = ((templates.data ?? []) as Rec[]).find((t) => t.id === tid);
  // a row as it will be sent: the sheet's values first, then the template's, then the defaults
  const shape = (r: Rec): Rec => {
    const n = (tpl?.naming ?? {}) as Rec; const first = r.first ?? '', last = r.last ?? '', ini = r.initials ?? '';
    const sam = r.sam ?? cleanSam(applyName(String(n.sam || '{first}.{last}'), first, last, ini)).toLowerCase();
    const attrs: Rec = { ...((tpl?.attrs ?? {}) as Rec) }; for (const k of ATTR_COLS) if (r[k]) attrs[k] = r[k];
    if (ini) attrs.initials = ini;
    return { action: 'create-user', ou: r.ou || ou || tpl?.ou || '', first, last, sam, display: r.display ?? applyName(String(n.display || '{first} {last}'), first, last, ini),
      upn: r.upn ?? (n.upnSuffix ? `${sam}@${n.upnSuffix}` : ''), email: r.email ?? (n.email ? applyName(String(n.email), first, last, ini).toLowerCase() : ''),
      password: r.password ?? '', expires: r.expires ?? '', mustChange: tpl?.options?.mustChange ?? true, pwdNeverExpires: !!tpl?.options?.pwdNeverExpires,
      groups: [...((tpl?.groups ?? []) as string[]), ...String(r.groups ?? '').split(';').map((g) => g.trim()).filter((g) => g.includes('='))], attrs };
  };
  const load = async (f: File) => { const s = readSheet(await f.text()); setRows(s.rows); setUnknown(s.unknown); setCheck(null); setOut([]); setErr(s.rows.length ? '' : 'The file has no rows under its header.'); };
  const doCheck = async () => { setBusy(true); setErr(''); try { setCheck(await api<Rec>(`/api/adsuite/domains/${domain.id}/bulk/check`, { method: 'POST', body: { mode: 'create', rows: rows.map(shape) } })); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); } };
  const run = async () => {
    setBusy(true); const res: Rec[] = []; const good = ((check?.rows ?? []) as Rec[]).filter((r) => r.ok).map((r) => Number(r.row) - 1);
    for (const i of good) {
      const b = shape(rows[i]);
      try { const r = await act(domain.id, b); res.push({ row: i + 1, sam: b.sam, ok: true, dn: r.dn, password: r.password ?? '' }); }
      catch (e) { res.push({ row: i + 1, sam: b.sam, ok: false, error: e instanceof Error ? e.message : String(e) }); }
      setOut([...res]);
    }
    setBusy(false);
  };
  const bad = ((check?.rows ?? []) as Rec[]).filter((r) => !r.ok);
  return <Card title="Create users from a file">
    <p className="muted">A CSV file with a header row. Columns: <code>first, last, sam, email, ou, password, groups, expires</code> and any field such as <code>title, department, telephoneNumber, l, company</code>.
      Empty values come from the template. <code>groups</code> holds group DNs separated by <code>;</code>. A row without a password gets a generated one, listed at the end.</p>
    <p><button className="link" onClick={() => saveCsv('new-users-sample.csv', [['first', 'last', 'sam', 'email', 'title', 'department', 'ou', 'groups'], ['Asha', 'Nair', 'asha.nair', 'asha.nair@' + String(domain.dnsName), 'Accountant', 'Finance', '', '']])}>Download a sample file</button></p>
    <div className="filters">
      <label className="inline">Template <select value={tid} onChange={(e) => { setTid(Number(e.target.value)); setCheck(null); }}><option value={0}>None</option>{((templates.data ?? []) as Rec[]).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}</select></label>
      <div className="scope"><span className="lbl">OU for rows without one</span><OuPicker domain={domain} value={ou} onChange={(v) => { setOu(v); setCheck(null); }} /></div>
      <input type="file" accept=".csv,text/csv" aria-label="CSV file" onChange={(e) => e.target.files?.[0] && void load(e.target.files[0])} />
    </div>
    {unknown.length > 0 && <p className="muted small">Columns not used: {unknown.join(', ')}</p>}
    <Err text={err} />
    {rows.length > 0 && !check && <div className="actions"><button className="primary" disabled={busy} onClick={() => void doCheck()}>{busy ? 'Checking…' : `Check ${rows.length} row(s)`}</button></div>}
    {check && <>
      <p><Badge tone="ok">{String(check.ok)} ready</Badge> {bad.length > 0 && <Badge tone="bad">{bad.length} with problems</Badge>}</p>
      {bad.length > 0 && <table className="adtable small"><thead><tr><th>Row</th><th>Sign-in</th><th>Problem</th></tr></thead><tbody>{bad.map((r) => <tr key={String(r.row)}><td>{String(r.row)}</td><td>{String(r.sam)}</td><td className="err">{(r.problems as string[]).join('; ')}</td></tr>)}</tbody></table>}
      {out.length === 0 && Number(check.ok) > 0 && <div className="actions"><button className="primary" disabled={busy} onClick={() => void run()}>Create {String(check.ok)} account(s)</button>{bad.length > 0 && <span className="muted small">Rows with problems are skipped.</span>}</div>}
    </>}
    {out.length > 0 && <>
      <h3>{busy ? `Creating… ${out.length} of ${String(check?.ok)}` : 'Finished'}</h3>
      <p>{out.filter((o) => o.ok).length} created, {out.filter((o) => !o.ok).length} failed.</p>
      {out.some((o) => !o.ok) && <ul className="plain small">{out.filter((o) => !o.ok).map((o) => <li key={String(o.row)}>Row {String(o.row)} ({String(o.sam)}): <span className="err">{String(o.error)}</span></li>)}</ul>}
      {!busy && <button className="primary" onClick={() => saveCsv('created-users.csv', [['row', 'sign-in name', 'result', 'password (shown once)'], ...out.map((o) => [String(o.row), String(o.sam), o.ok ? 'created' : String(o.error), String(o.password ?? '')])])}>Download the results with passwords</button>}
    </>}
  </Card>;
}

/** Change many accounts from a file: the sign-in name finds the account, the other columns are the new values. */
export function BulkModify({ domain }: { domain: Rec }) {
  const [rows, setRows] = useState<Rec[]>([]); const [check, setCheck] = useState<Rec | null>(null); const [out, setOut] = useState<Rec[]>([]); const [busy, setBusy] = useState(false); const [err, setErr] = useState('');
  const attrsOf = (r: Rec) => { const a: Rec = {}; for (const k of [...ATTR_COLS, 'email', 'first', 'last', 'display']) if (r[k] !== undefined) a[k === 'email' ? 'mail' : k === 'first' ? 'givenName' : k === 'last' ? 'sn' : k === 'display' ? 'displayName' : k] = r[k] === '-' ? '' : r[k]; return a; };
  const load = async (f: File) => { const s = readSheet(await f.text()); setRows(s.rows); setCheck(null); setOut([]); setErr(s.rows.length ? '' : 'The file has no rows under its header.'); };
  const doCheck = async () => { setBusy(true); setErr(''); try { setCheck(await api<Rec>(`/api/adsuite/domains/${domain.id}/bulk/check`, { method: 'POST', body: { mode: 'modify', rows: rows.map((r) => ({ sam: r.sam ?? '', attrs: Object.fromEntries(Object.keys(attrsOf(r)).filter((k) => !['givenName', 'sn', 'displayName', 'mail'].includes(k)).map((k) => [k, ''])) })) } })); } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); } };
  const run = async () => {
    setBusy(true); const res: Rec[] = [];
    for (const c of ((check?.rows ?? []) as Rec[]).filter((r) => r.ok)) {
      const r = rows[Number(c.row) - 1]; const attrs = attrsOf(r);
      try { if (Object.keys(attrs).length) await act(domain.id, { action: 'update', dn: c.dn, attrs }); res.push({ row: c.row, sam: c.sam, ok: true }); }
      catch (e) { res.push({ row: c.row, sam: c.sam, ok: false, error: e instanceof Error ? e.message : String(e) }); }
      setOut([...res]);
    }
    setBusy(false);
  };
  const bad = ((check?.rows ?? []) as Rec[]).filter((r) => !r.ok);
  return <Card title="Change users from a file">
    <p className="muted">A CSV file with <code>sam</code> (the sign-in name) and the columns to change, for example <code>sam, title, department, telephoneNumber, l, company</code>. An empty cell leaves the value alone; <code>-</code> clears it.</p>
    <input type="file" accept=".csv,text/csv" aria-label="CSV file" onChange={(e) => e.target.files?.[0] && void load(e.target.files[0])} />
    <Err text={err} />
    {rows.length > 0 && !check && <div className="actions"><button className="primary" disabled={busy} onClick={() => void doCheck()}>{busy ? 'Checking…' : `Check ${rows.length} row(s)`}</button></div>}
    {check && <>
      <p><Badge tone="ok">{String(check.ok)} found</Badge> {bad.length > 0 && <Badge tone="bad">{bad.length} with problems</Badge>}</p>
      {bad.length > 0 && <table className="adtable small"><tbody>{bad.map((r) => <tr key={String(r.row)}><td>Row {String(r.row)}</td><td>{String(r.sam)}</td><td className="err">{(r.problems as string[]).join('; ')}</td></tr>)}</tbody></table>}
      {out.length === 0 && Number(check.ok) > 0 && <div className="actions"><button className="primary" disabled={busy} onClick={() => void run()}>Change {String(check.ok)} account(s)</button></div>}
    </>}
    {out.length > 0 && <p>{busy ? 'Changing… ' : 'Finished: '}{out.filter((o) => o.ok).length} changed, {out.filter((o) => !o.ok).length} failed.</p>}
    {out.some((o) => !o.ok) && <ul className="plain small">{out.filter((o) => !o.ok).map((o) => <li key={String(o.row)}>Row {String(o.row)} ({String(o.sam)}): <span className="err">{String(o.error)}</span></li>)}</ul>}
    {rows.length === 0 && <Empty>Choose a file to begin.</Empty>}
  </Card>;
}
