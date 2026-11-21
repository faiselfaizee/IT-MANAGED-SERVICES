import { jsx as _jsx } from "react/jsx-runtime";
import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import Setup from './Setup';
vi.mock('./assets/Companies', () => ({ default: () => _jsx("p", { children: "companies page" }) }));
vi.mock('./assets/Credentials', () => ({ default: () => _jsx("p", { children: "credentials page" }) }));
vi.mock('./assets/Fleet', () => ({ default: () => _jsx("p", { children: "fleet page" }) }));
vi.mock('./assets/Discover', () => ({ default: () => _jsx("p", { children: "discover page" }) }));
vi.mock('./assets/Duplicates', () => ({ default: () => _jsx("p", { children: "duplicates page" }) }));
afterEach(() => { cleanup(); location.hash = ''; });
test('Devices setup opens on customers and lists the shared pages', () => {
    location.hash = '#/setup';
    render(_jsx(Setup, {}));
    expect(screen.getByText('companies page')).toBeTruthy();
    for (const l of ['Customers', 'Credentials', 'Agents & collectors', 'Discover', 'Duplicates'])
        expect(screen.getByRole('link', { name: l })).toBeTruthy();
});
test('Devices setup opens the page named in the address', () => {
    location.hash = '#/setup/credentials';
    render(_jsx(Setup, {}));
    expect(screen.getByText('credentials page')).toBeTruthy();
});
