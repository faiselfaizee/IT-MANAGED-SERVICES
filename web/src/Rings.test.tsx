import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, expect, test } from 'vitest';
import { Ring } from './Rings';

afterEach(cleanup);

test('a ring shows the total, every count as text, and links each part to its list', () => {
  render(<Ring title="Agents" unit="devices" parts={[
    { key: 'on', label: 'Live', value: 12, tone: 'ok', href: '#/assets?agent=online' },
    { key: 'off', label: 'Not reporting', value: 3, tone: 'bad', href: '#/assets?agent=offline' },
    { key: 'none', label: 'No agent', value: 0, tone: 'none' }]} />);
  expect(screen.getByText('15')).toBeTruthy();
  expect(screen.getByRole('img').getAttribute('aria-label')).toBe('Agents: 12 live, 3 not reporting, 0 no agent');
  expect(screen.getByText('Not reporting').closest('a')?.getAttribute('href')).toBe('#/assets?agent=offline');
  expect(document.querySelectorAll('circle.ring-arc').length).toBe(2); // an empty part draws no arc but keeps its legend row
  fireEvent.mouseEnter(screen.getByText('Live').closest('li')!);
  expect(document.querySelectorAll('circle.ring-arc.dim').length).toBe(1);
});

test('an empty ring says so', () => {
  render(<Ring title="Open tickets" empty="No open tickets." parts={[{ key: 'o', label: 'New', value: 0, tone: 'info' }]} />);
  expect(screen.getByText('No open tickets.')).toBeTruthy();
  expect(document.querySelectorAll('circle.ring-arc').length).toBe(0);
});
