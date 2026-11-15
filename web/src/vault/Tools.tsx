import { useState } from 'react';
import { api, type Rec } from '../http';
import { copyText, Empty } from '../ui';
import * as S from './session';
import * as C from './crypto';
import { customOf, typeDef, type Custom } from './types';
import { Strength } from './Gate';

/** The password generator; with onUse it fills a field, on its own it copies. */
export function Generator({ onUse }: { onUse?: (p: string) => void }) {
  const [o, setO] = useState<C.GenOptions>(C.DEFAULT_GEN);
  const [pw, setPw] = useState(() => C.generate(C.DEFAULT_GEN));
  const next = (x: C.GenOptions) => { setO(x); setPw(C.generate(x)); };
  return <div className="generator">
    <p className="gen-out"><code>{pw}</code></p>
    <Strength pw={pw} />
    <label><span>Length: {o.length}</span><input type="range" min={8} max={64} value={o.length} onChange={(e) => next({ ...o, length: Number(e.target.value) })} /></label>
    <div className="checks">
      {([['upper', 'A–Z'], ['lower', 'a–z'], ['digits', '0–9'], ['symbols', '!#$%…'], ['avoidAmbiguous', 'Avoid look-alikes (O 0 l 1)']] as [keyof C.GenOptions, string][]).map(([k, l]) =>
        <label key={k} className="check"><input type="checkbox" checked={!!o[k]} onChange={(e) => next({ ...o, [k]: e.target.checked })} /> {l}</label>)}
    </div>
    <div className="tools"><button type="button" onClick={() => setPw(C.generate(o))}>Another one</button>
      {onUse ? <button type="button" className="primary" onClick={() => onUse(pw)}>Use this password</button> : <button type="button" className="primary" onClick={() => copyText(pw)}>Copy</button>}</div>
  </div>;
}

// ---- import

type Draft = { title: string; type: string; data: S.Data; folder?: string };

/** Splits CSV text into rows (quotes, doubled quotes and line breaks inside quotes). */
export function csv(text: string): string[][] {
  const rows: string[][] = []; let row: string[] = []; let cell = ''; let q = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (q) { if (ch === '"' && text[i + 1] === '"') { cell += '"'; i++; } else if (ch === '"') q = false; else cell += ch; }
    else if (ch === '"') q = true;
    else if (ch === ',') { row.push(cell); cell = ''; }
    else if (ch === '\n' || ch === '\r') { if (ch === '\r' && text[i + 1] === '\n') i++; row.push(cell); cell = ''; if (row.some((c) => c !== '')) rows.push(row); row = []; }
    else cell += ch;
  }
  row.push(cell); if (row.some((c) => c !== '')) rows.push(row);
  return rows;
}

/** Bitwarden JSON export (not the encrypted kind), Bitwarden CSV, or any CSV with name/url/username/password columns (Chrome, Edge, KeePass, LastPass). */
export function parseImport(text: string): Draft[] {
  const t = text.trim();
  if (t.startsWith('{')) {
    const j = JSON.parse(t) as Rec;
    if (j.encrypted) throw new Error('This is an encrypted Bitwarden export. Export again as "JSON" (not "JSON (Encrypted)").');
    const folders = new Map<string, string>(((j.folders ?? []) as Rec[]).map((f) => [String(f.id), String(f.name)]));
    return ((j.items ?? []) as Rec[]).map((it) => {
      const custom: Custom[] = ((it.fields ?? []) as Rec[]).map((f) => ({ name: String(f.name ?? ''), value: String(f.value ?? ''), hidden: f.type === 1 }));
      const d: S.Data = {}; let type = 'login';
      if (it.type === 1) { d.username = it.login?.username ?? ''; d.password = it.login?.password ?? ''; d.totp = it.login?.totp ?? ''; d.url = it.login?.uris?.[0]?.uri ?? ''; const more = ((it.login?.uris ?? []) as Rec[]).slice(1); more.forEach((u, i) => custom.push({ name: `Web address ${i + 2}`, value: String(u.uri), hidden: false })); }
      else if (it.type === 2) { type = 'note'; }
      else if (it.type === 3) { type = 'card'; const c = it.card ?? {}; Object.assign(d, { cardholder: c.cardholderName ?? '', brand: c.brand ?? '', number: c.number ?? '', expiry: c.expMonth ? `${String(c.expMonth).padStart(2, '0')}/${String(c.expYear ?? '').slice(-2)}` : '', cvv: c.code ?? '' }); }
      else if (it.type === 4) { type = 'identity'; const c = it.identity ?? {}; Object.assign(d, { fullName: [c.firstName, c.middleName, c.lastName].filter(Boolean).join(' '), email: c.email ?? '', phone: c.phone ?? '', company: c.company ?? '', address: [c.address1, c.address2, c.city, c.state, c.postalCode, c.country].filter(Boolean).join('\n') }); }
      else if (it.type === 5) { type = 'ssh'; d.privateKey = it.sshKey?.privateKey ?? ''; d.publicKey = it.sshKey?.publicKey ?? ''; }
      if (it.notes) d.notes = String(it.notes);
      if (custom.length) d.custom = JSON.stringify(custom);
      for (const k of Object.keys(d)) if (!d[k]) delete d[k];
      return { title: String(it.name || 'Imported item'), type, data: d, folder: it.folderId ? folders.get(String(it.folderId)) : undefined };
    });
  }
  const rows = csv(t); if (rows.length < 2) throw new Error('The file has no rows.');
  const head = rows[0].map((h) => h.trim().toLowerCase());
  const col = (...names: string[]) => { for (const n of names) { const i = head.indexOf(n); if (i >= 0) return i; } return -1; };
  const c = { name: col('name', 'title', 'account'), url: col('login_uri', 'url', 'website', 'web site'), user: col('login_username', 'username', 'user name', 'login', 'email'), pw: col('login_password', 'password'), totp: col('login_totp', 'totp', 'otpauth'), notes: col('notes', 'note', 'extra', 'comments'), folder: col('folder', 'group', 'grouping'), type: col('type') };
  if (c.name < 0 && c.url < 0) throw new Error('No "name" or "url" column found. The first line must name the columns.');
  return rows.slice(1).map((r) => {
    const g = (i: number) => (i >= 0 ? (r[i] ?? '').trim() : '');
    const note = g(c.type) === 'note';
    const d: S.Data = note ? { notes: g(c.notes) } : { username: g(c.user), password: g(c.pw), url: g(c.url), totp: g(c.totp), notes: g(c.notes) };
    for (const k of Object.keys(d)) if (!d[k]) delete d[k];
    let title = g(c.name); if (!title && d.url) { try { title = new URL(/^https?:/i.test(d.url) ? d.url : 'https://' + d.url).hostname; } catch { title = d.url; } }
    return { title: title || 'Imported item', type: note ? 'note' : 'login', data: d, folder: g(c.folder).replace(/^\/+/, '') || undefined };
  });
}

function Import() {
  const writable = S.current().vaults.filter((v) => S.rank(v.role) >= 3 && S.vaultKeys(v.id)?.item).sort((a, b) => Number(a.kind === 'personal') - Number(b.kind === 'personal'));
  const [vid, setVid] = useState(writable[0]?.id ?? 0); const [drafts, setDrafts] = useState<Draft[] | null>(null);
  const [err, setErr] = useState(''); const [msg, setMsg] = useState(''); const [busy, setBusy] = useState(false);
  if (!writable.length) return <Empty>You cannot add items to any vault.</Empty>;
  const run = async () => {
    if (!drafts) return; setBusy(true); setErr('');
    try {
      const folderIds = new Map<string, number>();
      const existing = (await api<Rec>(`/api/vault/vaults/${vid}/folders`)).items as Rec[];
      for (const f of existing) folderIds.set((await S.folderName(vid, f.nameEnc)).toLowerCase(), Number(f.id));
      let n = 0;
      for (const d of drafts) {
        let folderId: number | null = null;
        if (d.folder) {
          const key = d.folder.toLowerCase();
          if (!folderIds.has(key)) folderIds.set(key, Number((await api<Rec>(`/api/vault/vaults/${vid}/folders`, { method: 'POST', body: { nameEnc: await S.encFolder(vid, d.folder) } })).id));
          folderId = folderIds.get(key)!;
        }
        const enc = await S.encItem(vid, d.title, d.data);
        await api(`/api/vault/vaults/${vid}/items`, { method: 'POST', body: { ...enc, type: d.type, folderId, hasPassword: !!d.data.password } });
        n++; setMsg(`Imported ${n} of ${drafts.length}…`);
      }
      setMsg(`Imported ${n} items.`); setDrafts(null);
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <section className="card">
    <h2>Import</h2>
    <p className="muted">From Bitwarden (Tools › Export vault, format "JSON" or "CSV"), or a CSV export of Chrome, Edge, KeePass or LastPass. The file is read and encrypted on this computer; delete the export file afterwards.</p>
    <div className="tools">
      <select value={vid} onChange={(e) => setVid(Number(e.target.value))} aria-label="Into vault">{writable.map((v) => <option key={v.id} value={v.id}>{v.name}</option>)}</select>
      <input type="file" accept=".json,.csv,text/csv,application/json" aria-label="Export file" onChange={async (e) => { setErr(''); setMsg(''); const f = e.target.files?.[0]; if (!f) return; try { setDrafts(parseImport(await f.text())); } catch (x) { setDrafts(null); setErr((x as Error).message); } }} />
    </div>
    {drafts && <>
      <p>{drafts.length} items found: {Object.entries(drafts.reduce<Record<string, number>>((a, d) => ({ ...a, [d.type]: (a[d.type] ?? 0) + 1 }), {})).map(([k, n]) => `${n} ${typeDef(k).plural.toLowerCase()}`).join(', ')}.</p>
      <ul className="plain small">{drafts.slice(0, 8).map((d, i) => <li key={i}>{d.title}{d.folder ? <span className="muted"> · {d.folder}</span> : null}</li>)}{drafts.length > 8 && <li className="muted">and {drafts.length - 8} more</li>}</ul>
      <div className="tools"><button className="primary" disabled={busy} onClick={() => void run()}>Import {drafts.length} items</button><button onClick={() => setDrafts(null)}>Cancel</button></div>
    </>}
    {err && <p className="err" role="alert">{err}</p>}
    {msg && <p className="muted" role="status">{msg}</p>}
  </section>;
}

function Export() {
  const [pw, setPw] = useState(''); const [fmt, setFmt] = useState<'enc' | 'csv'>('enc'); const [err, setErr] = useState(''); const [busy, setBusy] = useState(false);
  const run = async () => {
    setBusy(true); setErr('');
    try {
      const items = ((await api<Rec>('/api/vault/items')).items as S.Item[]).filter((i) => i.canRead && i.dataEnc);
      const out: Rec[] = [];
      for (const i of items) { const d = await S.data(i); if (!d) continue; out.push({ vault: S.vaultById(i.vaultId)?.name, type: i.type, title: await S.title(i), ...d }); S.note(i.id, 'exported'); }
      let blob: Blob; let name: string;
      if (fmt === 'enc') {
        if (pw.length < 12) throw new Error('Use a password of at least 12 characters for the export.');
        const kdf = C.newKdf();
        blob = new Blob([JSON.stringify({ format: 'deskask-vault-export', v: 1, kdf, data: await C.encryptJson(await C.masterKey(pw, kdf), out) })], { type: 'application/json' }); name = 'deskask-vault-export.json';
      } else {
        const esc = (s: unknown) => `"${String(s ?? '').replace(/"/g, '""')}"`;
        const rows = [['folder', 'type', 'name', 'login_uri', 'login_username', 'login_password', 'login_totp', 'notes'].join(',')];
        for (const o of out) rows.push([o.vault, o.type === 'note' ? 'note' : 'login', o.title, o.url ?? o.host ?? '', o.username ?? '', o.password ?? o.secret ?? '', o.totp ?? '', [o.notes, ...customOf(o as S.Data).map((c) => `${c.name}: ${c.value}`)].filter(Boolean).join('\n')].map(esc).join(','));
        blob = new Blob([rows.join('\n')], { type: 'text/csv' }); name = 'deskask-vault-export.csv';
      }
      const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = name; a.click();
    } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  return <section className="card form">
    <h2>Export</h2>
    <p className="muted">Everything you can open, in one file. The export is written to the item logs.</p>
    <label className="check"><input type="radio" checked={fmt === 'enc'} onChange={() => setFmt('enc')} /> Encrypted with a password of your choice (keep for a backup)</label>
    <label className="check"><input type="radio" checked={fmt === 'csv'} onChange={() => setFmt('csv')} /> Plain CSV (anyone with the file can read every password)</label>
    {fmt === 'enc' && <label><span>Export password</span><input type="password" value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="new-password" /></label>}
    {err && <p className="err" role="alert">{err}</p>}
    <div className="tools"><button className="primary" disabled={busy} onClick={() => void run()}>Export</button></div>
  </section>;
}

export default function Tools() {
  return <>
    <h1>Generator, import and export</h1>
    <div className="grid">
      <section className="card"><h2>Password generator</h2><Generator /></section>
      <Import />
      <Export />
    </div>
  </>;
}
