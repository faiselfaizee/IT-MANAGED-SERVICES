import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Renewals from './Renewals';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('shows what ends soon and saves the reminder settings', async () => {
  const calls: [string, string, string][] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    const u = String(url); calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
    if (u.includes('/renewals/settings') && o?.method !== 'PUT') return new Response(JSON.stringify({ enabled: false, days: [30, 7], to: '', customers: true }), { status: 200 });
    if (u.includes('/renewals/upcoming')) return new Response(JSON.stringify({ items: [{ kind: 'amc', id: 1, title: 'Server AMC', due: '2026-11-20', company: 'Acme', daysLeft: 8 }] }), { status: 200 });
    return new Response('{}', { status: 200 });
  }));
  render(<Renewals />);
  await waitFor(() => expect(screen.getByText('AMC contract: Server AMC')).toBeTruthy());
  expect(screen.getByText('8 days')).toBeTruthy();
  await waitFor(() => expect(screen.getByLabelText(/Send reminders/)).toBeTruthy());
  fireEvent.click(screen.getByLabelText(/Send reminders/)); fireEvent.change(screen.getByPlaceholderText('it@yourcompany.com'), { target: { value: 'it@acme.test' } });
  fireEvent.click(screen.getByText('Save'));
  await waitFor(() => expect(calls.some((c) => c[0] === 'PUT')).toBe(true));
  const body = JSON.parse(calls.find((c) => c[0] === 'PUT')![2]);
  expect(body).toEqual({ enabled: true, days: [30, 7], to: 'it@acme.test', customers: true });
});
