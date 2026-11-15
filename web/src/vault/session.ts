import { useEffect, useState } from 'react';
import { api, type Rec } from '../http';
import * as C from './crypto';

/**
 * The unlocked vault lives only in this page's memory: the private key and the vault keys. Locking (by hand, after the idle time, or
 * by leaving the page) forgets them; the server never had them.
 */
export type Me = { setUp: boolean; publicKey?: string; wrappedPrivate?: string; kdf?: C.Kdf; settings: Rec };
export type VaultRow = Rec & { id: number; name: string; kind: 'customer' | 'internal' | 'personal'; role: 'request' | 'view' | 'edit' | 'manage'; keyVersion: number; needsRotation: boolean };
type Keys = { item?: CryptoKey; dir?: CryptoKey; version: number };
export type Item = Rec & { id: number; vaultId: number; type: string; titleEnc: string; dataEnc: string | null; revision: number; keyVersion: number; managed: boolean; requiresApproval: boolean; canRead: boolean; canEdit: boolean };
export type Data = Record<string, string> & { custom?: string };

let me: Me | null = null;
let privateKey: CryptoKey | null = null;
const keys = new Map<number, Keys>();
let vaults: VaultRow[] = [];
let timer: ReturnType<typeof setTimeout> | undefined;
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((f) => f());

export const rank = (r?: string) => ['request', 'view', 'edit', 'manage'].indexOf(r ?? '') + 1;
export function isUnlocked() { return privateKey !== null; }
export function current() { return { me, vaults, unlocked: privateKey !== null }; }
export function key() { if (!privateKey) throw new Error('The vault is locked.'); return privateKey; }

export async function loadMe(): Promise<Me> { me = await api<Me>('/api/vault/me'); emit(); return me; }

export function lock() {
  privateKey = null; keys.clear(); vaults = []; if (timer) clearTimeout(timer); emit();
}
function touch() {
  if (!privateKey) return;
  if (timer) clearTimeout(timer);
  const mins = Number(me?.settings?.autoLockMinutes ?? 15) || 15;
  timer = setTimeout(lock, mins * 60_000);
}
if (typeof window !== 'undefined') for (const ev of ['pointerdown', 'keydown']) window.addEventListener(ev, touch, { passive: true });

async function opened(pk: CryptoKey) { privateKey = pk; touch(); await refresh(); void givePending(); }
export async function unlockWith(password: string) {
  if (!me?.wrappedPrivate || !me.kdf) throw new Error('Set up your vault first.');
  await opened(await C.unlock(password, me.wrappedPrivate, me.kdf));
}
/** First set-up: makes the keys, stores them, and opens a personal vault. Returns the recovery code to show once. */
export async function setUp(password: string, reset = false): Promise<string> {
  const k = await C.createKeys(password);
  await api('/api/vault/keys', { method: 'POST', body: { publicKey: k.publicKey, wrappedPrivate: k.wrappedPrivate, wrappedRecovery: k.wrappedRecovery, kdf: k.kdf, reset } });
  await loadMe(); await opened(k.privateKey);
  await createVault('personal', 'My vault');
  return k.recoveryCode;
}
export async function recover(code: string, newPassword: string) {
  const { wrappedRecovery } = await api<Rec>('/api/vault/keys/recovery');
  const r = await C.rewrap({ recoveryCode: code, wrappedRecovery: String(wrappedRecovery) }, newPassword);
  await api('/api/vault/keys/password', { method: 'PUT', body: { wrappedPrivate: r.wrappedPrivate, kdf: r.kdf, viaRecovery: true } });
  await loadMe(); await opened(r.privateKey);
}
export async function changePassword(oldPw: string, newPw: string) {
  if (!me?.wrappedPrivate || !me.kdf) throw new Error('Not set up.');
  const r = await C.rewrap({ password: oldPw, wrappedPrivate: me.wrappedPrivate, kdf: me.kdf }, newPw);
  await api('/api/vault/keys/password', { method: 'PUT', body: { wrappedPrivate: r.wrappedPrivate, kdf: r.kdf } });
  await loadMe(); privateKey = r.privateKey;
}
export async function newRecoveryCode(pw: string): Promise<string> {
  if (!me?.wrappedPrivate || !me.kdf) throw new Error('Not set up.');
  const r = await C.newRecovery(pw, me.wrappedPrivate, me.kdf);
  await api('/api/vault/keys/recovery', { method: 'PUT', body: { wrappedRecovery: r.wrappedRecovery } });
  return r.recoveryCode;
}

/** Reloads the vault list and opens the keys I hold. */
export async function refresh() {
  const list = (await api<Rec>('/api/vault/vaults')).items as VaultRow[];
  for (const v of list) {
    const have = keys.get(v.id);
    if (have && have.version === v.keyVersion) continue;
    const k: Keys = { version: v.keyVersion };
    try { if (v.wrappedDirKey) k.dir = await C.unwrapKey(key(), v.wrappedDirKey); } catch { /* not given yet */ }
    try { if (v.wrappedKey) k.item = await C.unwrapKey(key(), v.wrappedKey); } catch { /* not given yet */ }
    keys.set(v.id, k);
  }
  vaults = list; emit();
}
export const vaultKeys = (id: number) => keys.get(id);
export const vaultById = (id: number) => vaults.find((v) => v.id === id);

/** Gives the vault's keys to members who are waiting, in vaults where I am a manager. Runs after unlocking and from time to time. */
export async function givePending(): Promise<number> {
  if (!privateKey) return 0;
  const { items } = await api<Rec>('/api/vault/pending');
  const byVault = new Map<number, Rec[]>();
  for (const p of items as Rec[]) byVault.set(p.vaultId, [...(byVault.get(p.vaultId) ?? []), p]);
  let n = 0;
  for (const [vid, ps] of byVault) {
    const k = keys.get(vid); if (!k?.item || !k.dir) continue;
    const out = [];
    for (const p of ps) out.push({ userId: p.userId, keyVersion: p.keyVersion, wrappedDirKey: await C.wrapFor(p.publicKey, k.dir), wrappedKey: p.full ? await C.wrapFor(p.publicKey, k.item) : undefined });
    try { n += Number((await api<Rec>(`/api/vault/vaults/${vid}/keys`, { method: 'POST', body: { keys: out } })).given ?? 0); } catch { /* someone else did it, or the key changed */ }
  }
  return n;
}

export async function createVault(kind: string, name: string, companyId?: number) {
  const item = await C.newAesKey(), dir = await C.newAesKey();
  const pub = me?.publicKey; if (!pub) throw new Error('Set up your vault first.');
  const r = await api<Rec>('/api/vault/vaults', { method: 'POST', body: { kind, name, companyId, wrappedKey: await C.wrapFor(pub, item), wrappedDirKey: await C.wrapFor(pub, dir) } });
  await refresh();
  return Number(r.id);
}

// ---- items

export async function title(it: { vaultId: number; titleEnc: string }): Promise<string> {
  const k = keys.get(it.vaultId)?.dir; if (!k) return 'Locked item';
  try { return String((await C.decryptJson<Rec>(k, it.titleEnc)).title ?? ''); } catch { return 'Unreadable item'; }
}
export async function data(it: Item): Promise<Data | null> {
  const k = keys.get(it.vaultId)?.item; if (!k || !it.dataEnc) return null;
  return C.decryptJson<Data>(k, it.dataEnc);
}
export async function encItem(vaultId: number, t: string, d: Data) {
  const k = keys.get(vaultId); if (!k?.item || !k.dir) throw new Error('You do not hold this vault\'s key yet. A manager of the vault gives it to you when they next open the Vault.');
  return { titleEnc: await C.encryptJson(k.dir, { title: t }), dataEnc: await C.encryptJson(k.item, d), keyVersion: k.version };
}
export async function encFolder(vaultId: number, name: string) { const k = keys.get(vaultId)?.item; if (!k) throw new Error('No key for this vault.'); return C.encryptJson(k, { name }); }
export async function folderName(vaultId: number, nameEnc: string) { const k = keys.get(vaultId)?.item; if (!k) return 'Folder'; try { return String((await C.decryptJson<Rec>(k, nameEnc)).name); } catch { return 'Folder'; } }

/** Tells the server what was done with a secret (for the item's log). */
export function note(itemId: number, action: 'viewed' | 'revealed' | 'copied' | 'opened' | 'exported', field = '') {
  void api(`/api/vault/items/${itemId}/events`, { method: 'POST', body: { action, field } }).catch(() => undefined);
}

/** New keys for a vault after someone lost access: every item, old version and folder is encrypted again, in this browser. */
export async function rotateVault(vid: number, onStep?: (s: string) => void) {
  const k = keys.get(vid); const v = vaults.find((x) => x.id === vid);
  if (!k?.item || !k.dir || !v) throw new Error('You need the vault\'s keys to change them.');
  onStep?.('Reading the vault…');
  const items = [...((await api<Rec>(`/api/vault/items?vault=${vid}`)).items as Item[]), ...((await api<Rec>(`/api/vault/items?vault=${vid}&trash=true`)).items as Item[])];
  const folders = (await api<Rec>(`/api/vault/vaults/${vid}/folders`)).items as Rec[];
  const members = await api<Rec>(`/api/vault/vaults/${vid}/members`);
  const users = ((await api<Rec>('/api/vault/users')).users as Rec[]);
  const newItem = await C.newAesKey(), newDir = await C.newAesKey();
  onStep?.(`Encrypting ${items.length} items again…`);
  const outItems = [], history = [];
  for (const it of items) {
    const t = await C.decryptJson(k.dir, it.titleEnc);
    const d = it.dataEnc ? await C.decryptJson(k.item, it.dataEnc) : {};
    outItems.push({ id: it.id, titleEnc: await C.encryptJson(newDir, t), dataEnc: await C.encryptJson(newItem, d) });
    for (const h of (await api<Rec>(`/api/vault/items/${it.id}/history`)).items as Rec[])
      history.push({ itemId: it.id, revision: h.revision, dataEnc: await C.encryptJson(newItem, await C.decryptJson(k.item, h.dataEnc)) });
  }
  const outFolders = [];
  for (const f of folders) outFolders.push({ id: f.id, nameEnc: await C.encryptJson(newItem, await C.decryptJson(k.item, f.nameEnc)) });
  const outKeys = [];
  for (const p of members.people as Rec[]) {
    const pub = users.find((u) => u.id === p.id)?.publicKey; if (!pub) continue;
    outKeys.push({ userId: p.id, wrappedDirKey: await C.wrapFor(pub, newDir), wrappedKey: p.role === 'request' ? undefined : await C.wrapFor(pub, newItem) });
  }
  onStep?.('Saving…');
  await api(`/api/vault/vaults/${vid}/rotate`, { method: 'POST', body: { keyVersion: v.keyVersion + 1, items: outItems, history, folders: outFolders, keys: outKeys } });
  keys.delete(vid); await refresh();
}

/** Re-renders a component whenever the session changes (locked, unlocked, vaults reloaded). */
export function useSession() {
  const [, set] = useState(0);
  useEffect(() => { const f = () => set((n) => n + 1); listeners.add(f); return () => { listeners.delete(f); }; }, []);
  return current();
}

/** Decrypts titles for a list of items (async), keyed by item id. */
export function useTitles(items: { id: number; vaultId: number; titleEnc: string }[]): Record<number, string> {
  const [t, setT] = useState<Record<number, string>>({});
  const sig = items.map((i) => i.id + ':' + i.titleEnc.length + ':' + i.vaultId).join(',');
  useEffect(() => {
    let live = true;
    void (async () => { const o: Record<number, string> = {}; for (const i of items) o[i.id] = await title(i); if (live) setT(o); })();
    return () => { live = false; };
  }, [sig]); // eslint-disable-line react-hooks/exhaustive-deps
  return t;
}
