import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Duplicates from './Duplicates';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });

const groups = [{ company: 'Acme', why: ['same host name'], items: [
  { id: 1, tag: 'AST-1', name: 'SQL01', hostname: 'sql01', ip: '10.0.0.5', serial: 'SN1', agent: true, source: 'agent', lastSeen: '2026-11-15T10:00:00Z' },
  { id: 2, tag: 'AST-2', name: 'sql01', hostname: 'sql01', ip: '', serial: '', agent: false, source: 'manual', lastSeen: null }] }];

test('lists duplicates and merges them into the chosen asset', async () => {
  const calls: [string, string, string][] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, o?: RequestInit) => {
    const u = String(url); calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
    if (u === '/api/assets/duplicates') return new Response(JSON.stringify({ groups }), { status: 200 });
    return new Response(JSON.stringify({ ok: true }), { status: 200 });
  }));
  render(<Duplicates />);
  await waitFor(() => expect(screen.getByText('AST-2')).toBeTruthy());
  fireEvent.click(screen.getByText('Merge…'));
  fireEvent.click(screen.getByText('Merge into AST-1'));
  await waitFor(() => expect(calls.some((c) => c[1] === '/api/assets/merge')).toBe(true));
  expect(JSON.parse(calls.find((c) => c[1] === '/api/assets/merge')![2])).toEqual({ keepId: 1, mergeIds: [2] });
});
