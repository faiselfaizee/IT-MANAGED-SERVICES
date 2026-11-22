/** The Devices setup pages (shared by Assets and Monitoring), also listed in ⚙ Administration's side menu. */
const PAGES: [string, string][] = [['companies', 'Customers'], ['company', 'Our organisation'], ['credentials', 'Credentials'], ['fleet', 'Agents & collectors'], ['discover', 'Discover'], ['duplicates', 'Duplicates']];

/** One company: "Our organisation" (nobody adds customers); several: "Customers". */
export function setupPages(one: boolean): [string, string][] { return one ? PAGES.filter(([k]) => k !== 'companies') : PAGES.filter(([k]) => k !== 'company'); }
