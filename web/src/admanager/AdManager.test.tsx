import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import AdApp from './AdApp';
import { applyName, cleanSam } from './fields';
import { ouPath } from './pickers';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });

const domain = { id: 1, company: 'Acme', name: 'acme.test', dnsName: 'acme.test', baseDn: 'DC=acme,DC=test', enabled: true, tls: true, rights: ['read', 'unlock'], scoped: false };
const json = (o: unknown, status = 200) => new Response(JSON.stringify(o), { status });

test('the users list opens a panel that offers only what the person may do, and the menu hides what they cannot', async () => {
  location.hash = '#/admanager/users';
  const calls: string[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    calls.push(String(url));
    if (url === '/api/platform/me') return json({ role: 'engineer' });
    if (url === '/api/adsuite/domains') return json([domain]);
    if (url.includes('/search')) return json({ items: [{ dn: 'CN=Ana,OU=Sales,DC=acme,DC=test', kind: 'user', name: 'Ana', sam: 'ana', disabled: false, locked: true }], truncated: false });
    if (url.includes('/object')) return json({ obj: { dn: 'CN=Ana,OU=Sales,DC=acme,DC=test', kind: 'user', name: 'Ana', sam: 'ana', locked: true, disabled: false }, values: {}, memberOf: [], held: ['read', 'unlock'] });
    return json({});
  }));
  render(<AdApp />);
  fireEvent.click(await screen.findByText('Ana'));
  expect(await screen.findByText('Unlock')).toBeTruthy();
  expect(screen.queryByText('Reset password')).toBeNull();
  expect(screen.queryByText('Offboard')).toBeNull();
  expect(screen.queryByText('New user')).toBeNull();                 // no create right
  expect(screen.queryByText('Domains and delegation')).toBeNull();   // administrators only
  expect(calls.some((c) => c.includes('/object?dn='))).toBe(true);
});

test('with no domain shared, an administrator is shown where to add one', async () => {
  location.hash = '#/admanager';
  vi.stubGlobal('fetch', vi.fn(async (url: string) => (url === '/api/platform/me' ? json({ role: 'admin' }) : json([]))));
  render(<AdApp />);
  await waitFor(() => expect(screen.getByText('Add a domain')).toBeTruthy());
});

test('names follow the naming rules and OUs read as paths', () => {
  expect(applyName('{first}.{last}', 'Asha', 'Nair')).toBe('Asha.Nair');
  expect(applyName('{f}{last}', 'Asha', 'Nair')).toBe('ANair');
  expect(cleanSam('José María.Núñez-Ortega Long')).toBe('JoseMaria.Nunez-Orte');
  expect(ouPath('OU=Sales,OU=Staff,DC=acme,DC=test', 'DC=acme,DC=test')).toBe('Staff › Sales');
  expect(ouPath('DC=acme,DC=test', 'DC=acme,DC=test')).toBe('Whole domain');
});
