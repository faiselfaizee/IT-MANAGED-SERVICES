import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, expect, test, vi } from 'vitest';
import Branding from './Branding';
import { DEFAULT_BRAND, applyBrand, contrast, inkOn, setBrand } from './brand';

beforeEach(() => setBrand(DEFAULT_BRAND));
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

test('colours become CSS variables with readable text, and the theme is forced', () => {
  const root = document.createElement('div') as unknown as HTMLElement;
  applyBrand({ ...DEFAULT_BRAND, accent: '#ffdd00', nav: '#112233', theme: 'dark' }, root);
  expect(root.style.getPropertyValue('--accent')).toBe('#ffdd00');
  expect(root.style.getPropertyValue('--accent-ink')).toBe('#101820');   // dark text on yellow
  expect(root.style.getPropertyValue('--nav')).toBe('#112233');
  expect(root.getAttribute('data-theme')).toBe('dark');
  applyBrand(DEFAULT_BRAND, root);
  expect(root.style.getPropertyValue('--accent')).toBe(''); expect(root.hasAttribute('data-theme')).toBe(false);
});

test('ink and contrast', () => {
  expect(inkOn('#0d3b66')).toBe('#ffffff'); expect(inkOn('#ffffff')).toBe('#101820');
  expect(contrast('#000000', '#ffffff')).toBeGreaterThan(20);
});

test('the page saves what the administrator typed and rejects a bad colour before sending', async () => {
  const calls: { url: string; method?: string; body?: string }[] = [];
  vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
    calls.push({ url, method: init?.method, body: init?.body as string });
    return new Response(JSON.stringify({ ...DEFAULT_BRAND, name: 'Acme IT', accent: '#0d8a7f' }), { status: 200 });
  }));
  render(<Branding />);
  fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Acme IT' } });
  fireEvent.change(screen.getByLabelText('Accent colour'), { target: { value: 'teal' } });
  expect((screen.getByText('Save') as HTMLButtonElement).disabled).toBe(true);
  fireEvent.change(screen.getByLabelText('Accent colour'), { target: { value: '#0d8a7f' } });
  fireEvent.click(screen.getByText('Save'));
  await waitFor(() => expect(calls.length).toBe(1));
  expect(calls[0].method).toBe('PUT'); expect(JSON.parse(calls[0].body!)).toMatchObject({ name: 'Acme IT', accent: '#0d8a7f', theme: 'auto' });
  await waitFor(() => expect(document.documentElement.style.getPropertyValue('--accent')).toBe('#0d8a7f'));
});

test('a logo that is not a picture is refused without a request', () => {
  const f = vi.fn(); vi.stubGlobal('fetch', f);
  render(<Branding />);
  const input = screen.getByLabelText('Logo file');
  fireEvent.change(input, { target: { files: [new File(['x'], 'a.exe', { type: 'application/x-msdownload' })] } });
  expect(screen.getByText('Use a PNG, JPEG, WebP, GIF or SVG picture.')).toBeTruthy(); expect(f).not.toHaveBeenCalled();
});
