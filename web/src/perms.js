import { useLoad } from './http';
/**
 * What the signed-in person may do in a module, so screens can hide buttons that the server would refuse.
 * Until the answer arrives (or if it cannot be had) nothing is hidden: the server still decides.
 */
export function usePerms(module) {
    const mine = useLoad('/api/platform/access/mine');
    const me = useLoad('/api/platform/me');
    const m = mine.data?.[module];
    if (!m)
        return { admin: true, add: true, modify: true, delete: true, readAll: true };
    return { admin: me.data?.role === 'admin', add: m.add === 'all' || Array.isArray(m.add), modify: m.modify === 'all' || Array.isArray(m.modify), delete: m.delete === 'all' || Array.isArray(m.delete), readAll: m.read === 'all' };
}
