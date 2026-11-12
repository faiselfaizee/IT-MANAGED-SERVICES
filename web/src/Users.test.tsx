import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Users from './Users';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('lists people with role names and hides disable for yourself', async () => {
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify([
    { id: 'a', email: 'admin@acme.test', name: 'Ada', role: 'admin', active: true, twoStep: true },
    { id: 'b', email: 'sam@acme.test', name: 'Sam', role: 'cadmin', active: false, twoStep: false },
  ]), { status: 200 })));
  render(<Users meId="a" />);
  await waitFor(() => expect(screen.getByText('Ada')).toBeTruthy());
  expect(screen.getByText('Administrator')).toBeTruthy();
  expect(screen.getByText('Customer administrator')).toBeTruthy();
  expect(screen.getByText('Disabled')).toBeTruthy();
  expect(screen.queryAllByText('Disable').length).toBe(0);   // Ada is you; Sam is already disabled
  expect(screen.getByText('Enable')).toBeTruthy();
});

test('adding a customer user links them to a company in the same step', async () => {
  const calls: [string, string][] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    calls.push([o?.method ?? 'GET', String(url)]);
    if (String(url).startsWith('/api/assets/companies')) return new Response(JSON.stringify([{ id: 4, name: 'Acme' }]), { status: 200 });
    if (String(url) === '/api/platform/users' && o?.method === 'POST') return new Response(JSON.stringify({ id: 'new1' }), { status: 201 });
    return new Response(JSON.stringify([]), { status: 200 });
  }));
  render(<Users meId="a" />);
  fireEvent.click(await screen.findByText('Add user'));
  expect(screen.queryByText('Company')).toBeNull();
  fireEvent.change(screen.getByLabelText('Role'), { target: { value: 'cadmin' } });
  fireEvent.change(await screen.findByLabelText('Company'), { target: { value: '4' } });
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Sam' } });
  fireEvent.change(screen.getByLabelText('Email'), { target: { value: 'sam@acme.test' } });
  fireEvent.change(screen.getByLabelText(/Password/), { target: { value: 'Long-Enough-1' } });
  fireEvent.click(screen.getAllByText('Add user').at(-1)!);
  await waitFor(() => expect(calls).toContainEqual(['PUT', '/api/servicedesk/people/new1']));
});
