import { jsx as _jsx } from "react/jsx-runtime";
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { afterEach, expect, test, vi } from 'vitest';
import Locations from './Locations';
import Vendors from './Vendors';
import { placePath, warrantyEnd } from './Purchase';
afterEach(() => { cleanup(); vi.unstubAllGlobals(); location.hash = ''; });
test('warranty end from purchase date and period', () => {
    expect(warrantyEnd('2024-01-15', 36)).toBe('2027-01-14');
    expect(warrantyEnd('2024-01-31', 1)).toBe('2024-02-28');
    expect(warrantyEnd('2023-03-31', 12)).toBe('2024-03-30');
    expect(warrantyEnd('', 12)).toBe('');
    const ps = [{ id: 1, level: 'country', name: 'India', parentId: null }, { id: 2, level: 'state', name: 'Karnataka', parentId: 1 }, { id: 3, level: 'site', name: 'Bengaluru HQ', code: 'BLR01', parentId: 2 }];
    expect(placePath(ps, 3)).toBe('Bengaluru HQ (BLR01), Karnataka, India');
});
const places = [
    { id: 1, companyId: 7, parentId: null, level: 'country', name: 'India', code: '', assets: 0, canEdit: true, active: true },
    { id: 2, companyId: 7, parentId: 1, level: 'state', name: 'Karnataka', code: 'KA', assets: 0, canEdit: true, active: true },
    { id: 3, companyId: 7, parentId: 2, level: 'site', name: 'Bengaluru HQ', code: 'BLR01', city: 'Bengaluru', assets: 12, canEdit: true, active: true },
    { id: 4, companyId: 7, parentId: 1, level: 'site', name: 'Chennai office', code: 'MAA01', assets: 3, canEdit: true, active: true },
];
test('a customer\'s locations as a tree with asset counts, and adding a site', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, o) => {
        const u = String(url);
        calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
        if (u.startsWith('/api/assets/companies'))
            return new Response(JSON.stringify([{ id: 7, name: 'Acme India' }]), { status: 200 });
        if (u.startsWith('/api/assets/places') && (o?.method ?? 'GET') === 'GET')
            return new Response(JSON.stringify({ items: places, canManage: true, manageAll: true }), { status: 200 });
        return new Response(JSON.stringify({ id: 9 }), { status: 200 });
    }));
    render(_jsx(Locations, {}));
    await waitFor(() => expect(screen.getByText('Karnataka')).toBeTruthy());
    expect(screen.getByText('15 assets')).toBeTruthy(); // India: 12 in Karnataka + 3 in Chennai
    expect(screen.getAllByText('12 assets').map((a) => a.getAttribute('href'))).toEqual(['#/assets/list?company=7&place=2', '#/assets/list?company=7&place=3']);
    const ka = screen.getByText('Karnataka').closest('li');
    fireEvent.click(within(ka.querySelector('.loc-row')).getByText('Add site'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Mysuru plant' } });
    fireEvent.change(screen.getByLabelText('Site code'), { target: { value: 'mys01' } });
    fireEvent.click(screen.getByText('Save'));
    await waitFor(() => expect(calls.some((c) => c[0] === 'POST')).toBe(true));
    expect(JSON.parse(calls.find((c) => c[0] === 'POST')[2])).toMatchObject({ companyId: 7, level: 'site', parentId: 2, name: 'Mysuru plant', code: 'MYS01' });
});
test('adding a vendor: the GSTIN fills in the PAN', async () => {
    const calls = [];
    vi.stubGlobal('fetch', vi.fn(async (url, o) => {
        const u = String(url);
        calls.push([o?.method ?? 'GET', u, String(o?.body ?? '')]);
        if (u.startsWith('/api/assets/companies'))
            return new Response(JSON.stringify([{ id: 7, name: 'Acme India' }]), { status: 200 });
        if (u.startsWith('/api/assets/vendors') && (o?.method ?? 'GET') === 'GET')
            return new Response(JSON.stringify({ items: [{ id: 1, name: 'Dell Technologies', companyId: null, gst: '29AABCD1234E1ZX', assets: 4, contracts: 1, nextWarrantyEnd: null, active: true }], canManage: true, manageAll: true }), { status: 200 });
        return new Response(JSON.stringify({ id: 5 }), { status: 200 });
    }));
    render(_jsx(Vendors, {}));
    await waitFor(() => expect(screen.getByText('Dell Technologies')).toBeTruthy());
    expect(screen.getByText('Every customer')).toBeTruthy();
    fireEvent.click(screen.getByText('Add vendor'));
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Sri Sai Systems' } });
    fireEvent.change(screen.getByLabelText('GSTIN'), { target: { value: '29abcde1234f1z5' } });
    expect(screen.getByLabelText('PAN').value).toBe('ABCDE1234F');
    fireEvent.click(screen.getByText('Save'));
    await waitFor(() => expect(calls.some((c) => c[0] === 'POST')).toBe(true));
    expect(JSON.parse(calls.find((c) => c[0] === 'POST')[2])).toMatchObject({ name: 'Sri Sai Systems', gst: '29ABCDE1234F1Z5', pan: 'ABCDE1234F', companyId: null, remind: true });
});
