import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import VaultApp from './VaultApp';
import SendOpen from './SendOpen';
import * as S from './session';
import * as C from './crypto';
import { csv, parseImport } from './Tools';

type Rec = Record<string, any>; // eslint-disable-line @typescript-eslint/no-explicit-any

/** A small stand-in for the Vault API that keeps what the page stores, so the test can check it is all ciphertext. */
function fakeServer() {
  const st = { keys: null as Rec | null, vaults: [] as Rec[], vkeys: new Map<string, Rec>(), items: [] as Rec[], events: [] as Rec[], sends: new Map<string, Rec>() };
  const ok = (v: unknown, status = 200) => new Response(JSON.stringify(v), { status });
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    const u = String(url); const m = init?.method ?? 'GET'; const b = init?.body ? JSON.parse(String(init.body)) : {};
    const path = u.split('?')[0];
    if (path === '/api/vault/me') return ok(st.keys ? { setUp: true, ...st.keys, settings: { role: 'admin', canCreate: true, autoLockMinutes: 15, userId: 'me' } } : { setUp: false, settings: { role: 'admin', canCreate: true, userId: 'me' } });
    if (path === '/api/vault/keys' && m === 'POST') { st.keys = { publicKey: b.publicKey, wrappedPrivate: b.wrappedPrivate, kdf: b.kdf, wrappedRecovery: b.wrappedRecovery }; return ok({ ok: true }); }
    if (path === '/api/vault/vaults' && m === 'POST') { const id = st.vaults.length + 1; st.vaults.push({ id, name: b.kind === 'personal' ? 'My vault' : b.name, kind: b.kind, role: 'manage', keyVersion: 1, needsRotation: false, items: 0 }); st.vkeys.set(id + ':me', { wrappedKey: b.wrappedKey, wrappedDirKey: b.wrappedDirKey }); return ok({ id }); }
    if (path === '/api/vault/vaults') return ok({ items: st.vaults.map((v) => ({ ...v, ...st.vkeys.get(v.id + ':me'), items: st.items.filter((i) => i.vaultId === v.id).length })) });
    if (path === '/api/vault/pending') return ok({ items: [] });
    if (/^\/api\/vault\/vaults\/\d+\/folders$/.test(path)) return ok({ items: [] });
    if (/^\/api\/vault\/vaults\/\d+\/items$/.test(path) && m === 'POST') { const id = st.items.length + 100; st.items.push({ id, vaultId: Number(path.split('/')[4]), type: b.type, titleEnc: b.titleEnc, dataEnc: b.dataEnc, keyVersion: b.keyVersion, revision: 1, managed: false, requiresApproval: false, canRead: true, canEdit: true, updatedAt: new Date().toISOString(), createdAt: new Date().toISOString() }); return ok({ id }); }
    if (path === '/api/vault/items') return ok({ items: u.includes('trash=true') ? [] : st.items });
    if (/^\/api\/vault\/items\/\d+\/events$/.test(path)) { if (m === 'POST') st.events.push(b); return ok({ items: [] }); }
    if (/^\/api\/vault\/items\/\d+\/managed$/.test(path)) return ok({}, 404);
    if (path === '/api/vault/requests') return ok({ items: [] });
    if (path === '/api/vault/sends' && m === 'POST') { st.sends.set('S'.repeat(22), b); return ok({ id: 'S'.repeat(22) }); }
    if (path === `/api/public/vault/sends/${'S'.repeat(22)}`) return ok({ passcode: !!st.sends.get('S'.repeat(22))?.passcode, expiresAt: new Date(Date.now() + 3600e3).toISOString(), viewsLeft: 1, from: 'eng@acme.test' });
    if (path === `/api/public/vault/sends/${'S'.repeat(22)}/open`) { const s = st.sends.get('S'.repeat(22))!; return s.passcode && b.passcodeCheck !== s.passcodeCheck ? ok({ error: 'passcode', detail: 'Wrong passcode. 9 tries left.' }, 403) : ok({ dataEnc: s.dataEnc, viewsLeft: 0 }); }
    return ok({});
  }));
  return st;
}

beforeEach(() => { location.hash = ''; S.lock(); });
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('set-up, recovery code, adding an item, and only ciphertext reaches the server', async () => {
  const st = fakeServer();
  await S.loadMe();
  location.hash = '#/vault'; render(<VaultApp />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Set up your vault' })).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Master password'), { target: { value: 'short' } });
  fireEvent.change(screen.getByLabelText('Type it again'), { target: { value: 'short' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create my vault' }));
  expect(screen.getByRole('alert').textContent).toContain('at least 12 characters');
  fireEvent.change(screen.getByLabelText('Master password'), { target: { value: 'purple tractor in the rain 42' } });
  fireEvent.change(screen.getByLabelText('Type it again'), { target: { value: 'purple tractor in the rain 42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Create my vault' }));
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Keep your recovery code' })).toBeTruthy(), { timeout: 15000 });
  const code = screen.getByLabelText('Recovery code').textContent!;
  expect(code).toMatch(/^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$/);
  expect((screen.getByRole('button', { name: 'Continue' }) as HTMLButtonElement).disabled).toBe(true);
  fireEvent.click(screen.getByRole('checkbox'));
  fireEvent.click(screen.getByRole('button', { name: 'Continue' }));
  expect(st.keys!.kdf.iterations).toBe(600000);
  expect(st.vaults).toHaveLength(1);

  await waitFor(() => expect(screen.getByRole('heading', { name: 'All items' })).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Add item' }));
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Firewall admin' } });
  fireEvent.change(screen.getByLabelText('User name'), { target: { value: 'admin' } });
  fireEvent.change(screen.getByLabelText('Password'), { target: { value: 'Tr0ub4dor&3-horse' } });
  fireEvent.click(within(screen.getByRole('dialog')).getByRole('button', { name: 'Add item' }));
  await waitFor(() => expect(st.items).toHaveLength(1));
  const stored = JSON.stringify(st.items[0]);
  expect(stored).not.toContain('Firewall'); expect(stored).not.toContain('Tr0ub4dor'); expect(stored).not.toContain('admin');
  await waitFor(() => expect(screen.getAllByText('Firewall admin').length).toBeGreaterThan(0));
  await waitFor(() => expect(screen.getByText('User name')).toBeTruthy());
  expect(screen.queryByText('Tr0ub4dor&3-horse')).toBeNull();
  fireEvent.click(screen.getAllByRole('button', { name: 'Show' })[0]);
  expect(screen.getByText('Tr0ub4dor&3-horse')).toBeTruthy();
  expect(st.events.some((e) => e.action === 'revealed' && e.field === 'Password')).toBe(true);

  // locking forgets the keys; the master password opens them again
  fireEvent.click(screen.getByRole('button', { name: 'Lock' }));
  await waitFor(() => expect(screen.getByRole('heading', { name: 'The vault is locked' })).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Master password'), { target: { value: 'wrong one' } });
  fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
  await waitFor(() => expect(screen.getByRole('alert').textContent).toBe('Wrong master password.'), { timeout: 15000 });
  fireEvent.change(screen.getByLabelText('Master password'), { target: { value: 'purple tractor in the rain 42' } });
  fireEvent.click(screen.getByRole('button', { name: 'Unlock' }));
  await waitFor(() => expect(screen.getAllByText('Firewall admin').length).toBeGreaterThan(0), { timeout: 15000 });
}, 60000);

test('a send link opens with the key in the address and the passcode', async () => {
  const st = fakeServer();
  const frag = C.random(32);
  st.sends.set('S'.repeat(22), { dataEnc: await C.encryptJson(await C.sendKey(frag, '4821'), { text: 'Guest Wi-Fi: Blue-Lemon-77' }), passcode: true, passcodeCheck: await C.sendCheck(frag, '4821') });
  render(<SendOpen id={'S'.repeat(22)} frag={C.b64u(frag)} />);
  await waitFor(() => expect(screen.getByRole('heading', { name: 'eng@acme.test sent you something private' })).toBeTruthy());
  fireEvent.change(screen.getByLabelText('Passcode'), { target: { value: '0000' } });
  fireEvent.click(screen.getByRole('button', { name: 'Open' }));
  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('Wrong passcode'));
  fireEvent.change(screen.getByLabelText('Passcode'), { target: { value: '4821' } });
  fireEvent.click(screen.getByRole('button', { name: 'Open' }));
  await waitFor(() => expect(screen.getByRole('heading', { name: 'Here it is' })).toBeTruthy());
  fireEvent.click(screen.getByRole('button', { name: 'Show' }));
  expect(screen.getByText('Guest Wi-Fi: Blue-Lemon-77')).toBeTruthy();
});

test('imports Bitwarden JSON and browser CSV exports', () => {
  const bw = parseImport(JSON.stringify({ encrypted: false, folders: [{ id: 'f1', name: 'Acme' }], items: [
    { type: 1, name: 'Portal', folderId: 'f1', login: { username: 'jo', password: 'pw1', totp: 'JBSWY3DPEHPK3PXP', uris: [{ uri: 'https://a.test' }, { uri: 'https://b.test' }] }, fields: [{ name: 'PIN', value: '1234', type: 1 }] },
    { type: 2, name: 'Door code', notes: '4711' },
    { type: 3, name: 'Card', card: { cardholderName: 'J O', number: '4111', expMonth: '7', expYear: '2028', code: '123' } },
  ] }));
  expect(bw[0]).toMatchObject({ title: 'Portal', type: 'login', folder: 'Acme', data: { username: 'jo', password: 'pw1', url: 'https://a.test', totp: 'JBSWY3DPEHPK3PXP' } });
  expect(JSON.parse(bw[0].data.custom!)).toEqual([{ name: 'PIN', value: '1234', hidden: true }, { name: 'Web address 2', value: 'https://b.test', hidden: false }]);
  expect(bw[1]).toMatchObject({ type: 'note', data: { notes: '4711' } });
  expect(bw[2].data.expiry).toBe('07/28');
  expect(() => parseImport('{"encrypted":true}')).toThrow('encrypted Bitwarden export');
  const chrome = parseImport('name,url,username,password,note\n"Router, office",https://10.0.0.1,admin,"pa""ss",\n,https://mail.example.com/login,jo,x,"two\nlines"\n');
  expect(chrome[0]).toMatchObject({ title: 'Router, office', data: { username: 'admin', password: 'pa"ss', url: 'https://10.0.0.1' } });
  expect(chrome[1]).toMatchObject({ title: 'mail.example.com', data: { notes: 'two\nlines' } });
  expect(csv('a,b\r\n1,2\r\n')).toEqual([['a', 'b'], ['1', '2']]);
  expect(() => parseImport('foo,bar\n1,2')).toThrow('No "name" or "url" column');
});
