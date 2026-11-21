import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Users from './Users';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('Administration › Users lists only your team and points to the companies for customer users', async () => {
  vi.stubGlobal('fetch', vi.fn(async (url: string) => new Response(JSON.stringify(String(url).startsWith('/api/platform/mode') ? { singleCompany: false } : [
    { id: 'a', email: 'admin@acme.test', name: 'Ada', role: 'admin', active: true, twoStep: true },
    { id: 'e', email: 'eve@acme.test', name: 'Eve', role: 'engineer', active: false, twoStep: false },
    { id: 'b', email: 'sam@cust.test', name: 'Sam', role: 'cadmin', active: true, twoStep: false },
  ]), { status: 200 })));
  render(<Users meId="a" />);
  await waitFor(() => expect(screen.getByText('Ada')).toBeTruthy());
  expect(screen.getByText('Administrator')).toBeTruthy();
  expect(screen.queryByText('Sam')).toBeNull();   // a customer administrator is kept on their company
  expect(screen.getByRole('note').textContent).toContain('(1 today)');
  expect(screen.getByRole('link', { name: /Customers › a company › Users/ }).getAttribute('href')).toBe('#/setup/companies');
  expect(screen.queryAllByText('Disable').length).toBe(0);   // Ada is you; Eve is already disabled
  expect(screen.getByText('Enable')).toBeTruthy();
});

test('adding a user here offers only staff roles', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([]), { status: 200 })));
  render(<Users meId="a" />);
  fireEvent.click(await screen.findByText('Add user'));
  const roles = [...(screen.getByLabelText('Role') as HTMLSelectElement).options].map((o) => o.value);
  expect(roles).toEqual(['admin', 'engineer', 'viewer']);
});
