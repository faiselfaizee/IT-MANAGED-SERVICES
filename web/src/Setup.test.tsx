import { afterEach, expect, test, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import Setup from './Setup';

vi.mock('./assets/Companies', () => ({ default: () => <p>companies page</p> }));
vi.mock('./assets/Credentials', () => ({ default: () => <p>credentials page</p> }));
vi.mock('./assets/Fleet', () => ({ default: () => <p>fleet page</p> }));
vi.mock('./assets/Discover', () => ({ default: () => <p>discover page</p> }));
vi.mock('./assets/Duplicates', () => ({ default: () => <p>duplicates page</p> }));
afterEach(() => { cleanup(); location.hash = ''; });

test('Devices setup opens on customers and lists the shared pages', () => {
  location.hash = '#/setup';
  render(<Setup />);
  expect(screen.getByText('companies page')).toBeTruthy();
  for (const l of ['Customers', 'Credentials', 'Agents & collectors', 'Discover', 'Duplicates']) expect(screen.getByRole('link', { name: l })).toBeTruthy();
});

test('Devices setup opens the page named in the address', () => {
  location.hash = '#/setup/credentials';
  render(<Setup />);
  expect(screen.getByText('credentials page')).toBeTruthy();
});
