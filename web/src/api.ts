export type WhoAmI = { tenant: string; name: string };
/** Phase 0 hand-written client; replaced by the generated OpenAPI client when the first module lands. */
export async function whoAmI(fetcher: typeof fetch = fetch): Promise<WhoAmI> {
  const r = await fetcher('/api/platform/whoami');
  if (!r.ok) throw new Error(r.status === 404 ? 'This address is not set up for any customer.' : `Server answered ${r.status}`);
  return r.json();
}
