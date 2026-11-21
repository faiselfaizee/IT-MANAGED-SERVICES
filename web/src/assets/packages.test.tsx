import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Packages, { InstallOn } from './Packages';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });
type Call = [string, string, unknown];
function api(routes: Record<string, unknown>, calls: Call[]) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url), o?.body]);
    const key = Object.keys(routes).sort((a, b) => b.length - a.length).find((k) => String(url).startsWith(k));
    return key ? new Response(JSON.stringify(routes[key]), { status: 200 }) : new Response('{"ok":true}', { status: 200 });
  }));
}

test('an installer is uploaded in pieces, then added with its details', async () => {
  const calls: Call[] = [];
  let length = 0;
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url), o?.body]);
    if (String(url) === '/api/assets/packages/uploads') return new Response(JSON.stringify({ id: 'upABCDEFGHIJKLMNOPQRST', maxPart: 4 }), { status: 200 });
    if (String(url).startsWith('/api/assets/packages/uploads/')) { length += (o!.body as Blob).size; return new Response(JSON.stringify({ length }), { status: 200 }); }
    if (String(url) === '/api/assets/packages' && o?.method === 'POST') return new Response(JSON.stringify({ id: 1 }), { status: 200 });
    return new Response(JSON.stringify({ items: [] }), { status: 200 });
  }));
  render(<Packages />);
  fireEvent.click(await screen.findByText('Add a program'));
  const file = new File(['0123456789'], '7z2409-x64.msi', { type: 'application/octet-stream' });
  fireEvent.change(screen.getByLabelText('Installer (.msi,.exe)'), { target: { files: [file] } });
  expect((screen.getByLabelText('Name') as HTMLInputElement).value).toBe('7z2409 x64');
  fireEvent.click(screen.getByText('Add'));
  await waitFor(() => expect(calls.some(([m, u]) => m === 'POST' && u === '/api/assets/packages')).toBe(true));
  const pieces = calls.filter(([m]) => m === 'PUT').map(([, u]) => u);
  expect(pieces).toEqual(['/api/assets/packages/uploads/upABCDEFGHIJKLMNOPQRST?offset=0', '/api/assets/packages/uploads/upABCDEFGHIJKLMNOPQRST?offset=4', '/api/assets/packages/uploads/upABCDEFGHIJKLMNOPQRST?offset=8']);
  const body = JSON.parse(String(calls.find(([m, u]) => m === 'POST' && u === '/api/assets/packages')![2]));
  expect(body).toMatchObject({ os: 'windows', kind: 'file', upload: 'upABCDEFGHIJKLMNOPQRST', fileName: '7z2409-x64.msi' });
});

test('installing on several computers shows what started and what was skipped', async () => {
  const calls: Call[] = [];
  api({ '/api/assets?': { items: [{ id: 5, name: 'PC-5', tag: 'AST-5', os: 'Windows 11 Pro' }, { id: 6, name: 'web01', tag: 'AST-6', os: 'Ubuntu 24.04' }, { id: 7, name: 'PC-7', tag: 'AST-7', os: 'Windows 10' }] },
    '/api/assets/packages/3/install': { queued: [{ assetId: 5, name: 'PC-5', job: 'j1' }], skipped: [{ assetId: 7, name: 'PC-7', reason: 'The agent on PC-7 is offline.' }] } }, calls);
  render(<InstallOn p={{ id: 3, name: '7-Zip', version: '24.09', os: 'windows' }} onClose={() => {}} />);
  await waitFor(() => expect(screen.getByLabelText('Choose PC-5')).toBeTruthy());
  expect(screen.queryByLabelText('Choose web01')).toBeNull();   // a Windows program is offered only for Windows computers
  fireEvent.click(screen.getByLabelText('Choose PC-5')); fireEvent.click(screen.getByLabelText('Choose PC-7'));
  fireEvent.click(screen.getByText('Install on 2'));
  await waitFor(() => expect(screen.getByText('1 install started.')).toBeTruthy());
  expect(screen.getByText('The agent on PC-7 is offline.')).toBeTruthy();
  expect(JSON.parse(String(calls.find(([m]) => m === 'POST')![2]))).toEqual({ assetIds: [5, 7] });
});
