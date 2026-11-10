import { cleanup, render, screen, waitFor } from '@testing-library/react';
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
