import { jsx as _jsx } from "react/jsx-runtime";
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Duplicates from './Duplicates';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
const groups = [{ company: 'Acme', why: ['same host name'], items: [
            { id: 1, tag: 'AST-1', name: 'SQL01', hostname: 'sql01', ip: '10.0.0.5', serial: 'SN1', agent: true, source: 'agent', lastSeen: '2026-11-15T10:00:00Z' },
            { id: 2, tag: 'AST-2', name: 'sql01', hostname: 'sql01', ip: '', serial: '', agent: false, source: 'manual', lastSeen: null }
        ] }];
test('lists duplicates and merges them into the chosen asset', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, o) => {
        const u = String(url);
        calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
        if (u === '/api/assets/duplicates')
            return new Response(JSON.stringify({ groups }), { status: 200 });
        return new Response(JSON.stringify({ ok: true }), { status: 200 });
    }));
    render(_jsx(Duplicates, {}));
    await waitFor(() => expect(screen.getByText('AST-2')).toBeTruthy());
    fireEvent.click(screen.getByText('Merge…'));
    fireEvent.click(screen.getByText('Merge into AST-1'));
    await waitFor(() => expect(calls.some((c) => c[1] === '/api/assets/merge')).toBe(true));
    expect(JSON.parse(calls.find((c) => c[1] === '/api/assets/merge')[2])).toEqual({ keepId: 1, mergeIds: [2] });
});
test('"Add anyway" is offered only to people who may use it', async () => {
    const { DuplicateNotice } = await import('./Duplicates');
    const info = { kind: 'possible_duplicate', text: 'x', matches: [{ id: 1, tag: 'AST-1', name: 'NAS', why: ['IP address'] }] };
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ allowed: false }), { status: 200 })));
    const a = render(_jsx(DuplicateNotice, { info: info, onAnyway: () => undefined }));
    expect(await screen.findByText(/Only an administrator/)).toBeTruthy();
    expect(screen.queryByText('Add anyway')).toBeNull();
    a.unmount();
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ allowed: true }), { status: 200 })));
    render(_jsx(DuplicateNotice, { info: info, onAnyway: () => undefined }));
    expect(await screen.findByText('Add anyway')).toBeTruthy();
});
