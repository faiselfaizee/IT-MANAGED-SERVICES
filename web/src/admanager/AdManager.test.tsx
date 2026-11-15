import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import AdApp from './AdApp';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });

const domain = { id: 1, company: 'Acme', name: 'acme.test', baseDn: 'DC=acme,DC=test', enabled: true, rights: ['read', 'unlock'], scoped: false };
const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status });

test('searching shows people, and the panel offers only what the person may do', async () => {
  location.hash = '#/admanager';
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(String(url));
    if (url === '/api/platform/me') return json({ role: 'technician' });
    if (url === '/api/adsuite/domains') return json([domain]);
    if (url.includes('/tree')) return json({ baseDn: 'DC=acme,DC=test', items: [] });
    if (url.includes('/search')) return json({ items: [{ dn: 'CN=Ana,DC=acme,DC=test', kind: 'user', name: 'Ana', sam: 'ana', disabled: false, locked: true }], truncated: false });
    if (url.includes('/object')) return json({ obj: { dn: 'CN=Ana,DC=acme,DC=test', kind: 'user', name: 'Ana', sam: 'ana', locked: true, disabled: false }, fields: {}, memberOf: [], held: ['read', 'unlock'] });
    return json({});
  }));
  render(<AdApp />);
  fireEvent.click(await screen.findByText('Ana'));
  expect(await screen.findByText('Unlock')).toBeTruthy();
  expect(screen.queryByText('Reset password')).toBeNull();
  expect(screen.queryByText('Delete')).toBeNull();
  expect(screen.queryByText('Domains')).toBeNull();   // only administrators see the setup screen
  expect(calls.some((c) => c.includes('/object?dn='))).toBe(true);
});

test('with no domain shared, a person is told so and an administrator is shown where to add one', async () => {
  location.hash = '#/admanager';
  vi.stubGlobal('fetch', vi.fn(async (url: string) => (url === '/api/platform/me' ? json({ role: 'admin' }) : json([]))));
  render(<AdApp />);
  await waitFor(() => expect(screen.getByText('Add a domain')).toBeTruthy());
});
