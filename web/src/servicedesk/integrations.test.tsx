import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Integrations from './Integrations';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

function mock(calls: [string, string, string][] = []) {
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    const u = String(url); calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
    if (u.endsWith('/keys') && o?.method === 'POST') return new Response(JSON.stringify({ id: 2, key: 'dk_secretvalue123' }), { status: 200 });
    if (u.endsWith('/keys')) return new Response(JSON.stringify({ items: [{ id: 1, name: 'Dashboard', prefix: 'dk_abc123', scope: 'read', createdAt: '2026-11-01T00:00:00Z', createdBy: 'Admin', lastUsedAt: null, revokedAt: null }] }), { status: 200 });
    if (u.endsWith('/webhooks')) return new Response(JSON.stringify({ items: [{ id: 1, url: 'https://hooks.example/desk', events: '*', active: true, failed: 2, lastSentAt: null }], events: ['ticket.created', 'ticket.status'] }), { status: 200 });
    return new Response('{}', { status: 200 });
  }));
}

test('lists keys and webhooks and flags failed messages', async () => {
  mock(); render(<Integrations />);
  await waitFor(() => expect(screen.getByText('Dashboard')).toBeTruthy());
  expect(screen.getByText('dk_abc123…')).toBeTruthy(); expect(screen.getByText('Read only')).toBeTruthy();
  expect(screen.getByText('https://hooks.example/desk')).toBeTruthy(); expect(screen.getByText('2 failed')).toBeTruthy(); expect(screen.getByText('All events')).toBeTruthy();
});

test('a new key is shown once after it is created', async () => {
  const calls: [string, string, string][] = []; mock(calls); render(<Integrations />);
  await waitFor(() => expect(screen.getByText('Dashboard')).toBeTruthy());
  fireEvent.click(screen.getByText('New API key'));
  fireEvent.change(screen.getByPlaceholderText('The program that will use it'), { target: { value: 'Monitoring tool' } });
  fireEvent.click(screen.getByText('Create key'));
  await waitFor(() => expect(screen.getByText('dk_secretvalue123')).toBeTruthy());
  expect(calls.find((c) => c[0] === 'POST')![2]).toContain('Monitoring tool');
});
