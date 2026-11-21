import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, render, screen, waitFor, fireEvent } from '@testing-library/react';
import Domains from './Domains';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('an unchecked address shows the two DNS records and can be checked', async () => {
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push((init?.method ?? 'GET') + ' ' + url);
    if (String(url).endsWith('/verify')) return new Response(JSON.stringify({ verified: false, detail: 'not yet' }), { status: 200, headers: { 'content-type': 'application/json' } });
    return new Response(JSON.stringify({ builtIn: 'svj.deskask.in', target: 'svj.deskask.in', items: [{ host: 'desk.svjlasertech.com', verified: false, primary: false, txtName: '_deskask-verify.desk.svjlasertech.com', txtValue: 'abc123', cname: 'svj.deskask.in' }] }), { status: 200, headers: { 'content-type': 'application/json' } });
  }));
  render(<Domains />);
  await waitFor(() => expect(screen.getAllByText('desk.svjlasertech.com').length).toBeGreaterThan(0));
  expect(screen.getByText('abc123')).toBeTruthy();
  fireEvent.click(screen.getByRole('button', { name: 'Check' }));
  await waitFor(() => expect(screen.getByText('not yet')).toBeTruthy());
  expect(calls).toContain('POST /api/platform/domains/desk.svjlasertech.com/verify');
});
