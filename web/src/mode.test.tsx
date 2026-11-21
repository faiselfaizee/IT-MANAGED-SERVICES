import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import Setup from './Setup';
import { resetMode } from './mode';

vi.mock('./assets/Companies', () => ({ default: () => <p>companies page</p> }));
vi.mock('./assets/Credentials', () => ({ default: () => <p>credentials page</p> }));
vi.mock('./assets/Fleet', () => ({ default: () => <p>fleet page</p> }));
vi.mock('./assets/Discover', () => ({ default: () => <p>discover page</p> }));
vi.mock('./assets/Duplicates', () => ({ default: () => <p>duplicates page</p> }));

beforeEach(() => resetMode());
afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
const mode = (single: boolean) => vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(String(url).startsWith('/api/platform/mode') ? { singleCompany: single } : {}), { status: 200 })));

test('with several companies, Devices setup starts on Customers', async () => {
  mode(false); location.hash = '#/setup';
  render(<Setup />);
  await waitFor(() => expect(screen.getByText('companies page')).toBeTruthy());
  expect(screen.getByRole('link', { name: 'Customers' })).toBeTruthy();
});

test('in single-company mode nobody adds customers: Devices setup has no Customers page and starts on Credentials', async () => {
  mode(true); location.hash = '#/setup/companies';
  render(<Setup />);
  await waitFor(() => expect(screen.queryByRole('link', { name: 'Customers' })).toBeNull());
  expect(screen.getByText('credentials page')).toBeTruthy();
  expect(screen.getByRole('link', { name: 'Agents & collectors' })).toBeTruthy();
});

test('if the mode cannot be read, the normal screens show', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 500 }))); location.hash = '#/setup';
  render(<Setup />);
  await waitFor(() => expect(screen.getByRole('link', { name: 'Customers' })).toBeTruthy());
});

test('in single-company mode the Assets home is the one company\'s dashboard, not a list of customers', async () => {
  const { default: Dashboard } = await import('./assets/Dashboard');
  vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(String(url).startsWith('/api/platform/mode') ? { singleCompany: true } : { customers: [{ companyId: 1, company: 'Acme', total: 3 }], all: { total: 3, company: 'All customers' }, customer: { company: 'All customers', total: 3 } }), { status: 200, headers: { 'content-type': 'application/json' } })));
  render(<Dashboard />);
  await waitFor(() => expect(screen.queryByLabelText('Find a customer')).toBeNull());
  await new Promise((r) => setTimeout(r, 50));
  expect(screen.queryByLabelText('Find a customer')).toBeNull();
  expect(screen.queryByText(/customers · /)).toBeNull();
});
