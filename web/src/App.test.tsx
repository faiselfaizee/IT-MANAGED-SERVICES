import { render, screen, waitFor } from '@testing-library/react';
import { expect, test, vi } from 'vitest';
import App, { MODULES } from './App';

const ok = (async () => new Response(JSON.stringify({ tenant: 'acme', name: 'Acme Ltd' }), { status: 200 })) as unknown as typeof fetch;
const notFound = (async () => new Response('{}', { status: 404 })) as unknown as typeof fetch;

test('shows the customer name', async () => {
  render(<App fetcher={ok} />);
  await waitFor(() => expect(screen.getByText('Welcome, Acme Ltd')).toBeTruthy());
});
test('unknown address shows a message', async () => {
  render(<App fetcher={notFound} />);
  await waitFor(() => expect(screen.getByRole('alert').textContent).toContain('not set up'));
});
test('lists all six modules', () => { expect(MODULES.length).toBe(6); });

test('a module the licence does not include has no tile, for customers and for staff', async () => {
  const { Home } = await import('./App');
  const cust = render(<Home who={{ tenant: 'acme', name: 'Acme' }} live={false} assets={true} customer={true} />);
  expect(cust.container.querySelectorAll('.tile-soon').length).toBe(0);
  cust.unmount();
  const staff = render(<Home who={{ tenant: 'acme', name: 'Acme' }} live={false} assets={true} deskOn={false} />);
  expect(staff.container.querySelectorAll('.tile-soon').length).toBe(0);
  expect(staff.container.querySelector('#t-desk')).toBeNull();
  expect(staff.container.querySelector('#t-assets')).not.toBeNull();
});

test('products sit in the top row, Administration behind the gear, and its pages in the side menu', async () => {
  const { cleanup } = await import('@testing-library/react');
  cleanup();   // the tests above leave their pages mounted
  sessionStorage.setItem('deskask.token', JSON.stringify({ v: 't', exp: Date.now() + 3600_000 }));
  const body = (u: string): unknown => u === '/api/platform/whoami' ? { tenant: 't', name: 'Acme' }
    : u === '/api/platform/me' ? { role: 'admin', name: 'Ada', sub: 'a' }
    : u === '/api/platform/modules' ? [{ key: 'assets', licensed: true }, { key: 'monitoring', licensed: true }]
    : u === '/api/platform/access/mine' ? { assets: { read: 'all' }, monitoring: { read: 'all' } }
    : u === '/api/platform/mode' ? { singleCompany: false } : u === '/api/platform/users' ? [] : { items: [] };
  vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(body(String(url).split('?')[0])), { status: 200 })));
  location.hash = '#/mail';
  render(<App />);
  const products = await screen.findByRole('navigation', { name: 'Products' });
  await waitFor(() => expect(products.textContent).toContain('Monitoring'));
  expect(products.textContent).toContain('Assets'); expect(products.textContent).not.toContain('Vault');   // only licensed products
  for (const a of screen.getAllByRole('link', { name: 'Administration' })) expect(a.getAttribute('aria-current')).toBe('page');   // the gear, and its entry in the small-screen menu
  const side = await screen.findByRole('complementary', { name: 'Administration' });
  for (const t of ['Users', 'Customers', 'Credentials', 'Mail', 'Licence']) expect(side.textContent).toContain(t);
  location.hash = ''; sessionStorage.clear(); cleanup(); vi.unstubAllGlobals();
});
