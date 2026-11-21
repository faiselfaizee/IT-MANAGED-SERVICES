import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useCallback, useEffect, useRef, useState } from 'react';
import { api, useInterval } from '../http';
import { Act, Badge, Err } from '../ui';
import { LiveLog } from './LiveLog';
const LABEL = { running: 'Updating', done: 'Updated', failed: 'Update failed', stalled: 'No answer' };
/** The agent version, whether an update is due, and "Update agent now" with a live log of every step. */
export function AgentUpdate({ id, admin, onDone }) {
    const [items, setItems] = useState([]);
    const [st, setSt] = useState(null);
    const [err, setErr] = useState('');
    const last = useRef(0);
    const was = useRef('');
    const pull = useCallback(async () => {
        try {
            const r = await api(`/api/assets/${id}/agent-update/log?after=${last.current}`);
            const add = (r.items ?? []);
            if (add.length) {
                last.current = Number(add[add.length - 1].seq);
                setItems((x) => [...x, ...add]);
            }
            setSt(r);
            if (was.current === 'running' && r.status !== 'running')
                onDone?.();
            was.current = String(r.status);
            setErr('');
        }
        catch (e) {
            setErr(e.message);
        }
    }, [id, onDone]);
    useEffect(() => { void pull(); }, [pull]);
    const running = st?.status === 'running';
    useInterval(() => { void pull(); }, running ? 1500 : null);
    if (!st)
        return null;
    const due = !!st.latest && !!st.version && st.version !== st.latest;
    const tone = st.status === 'done' ? 'ok' : st.status === 'failed' || st.status === 'stalled' ? 'bad' : running ? 'warn' : undefined;
    return _jsxs("div", { className: "agent-update", children: [_jsxs("p", { children: ["Agent version ", _jsx("b", { className: "mono", children: String(st.version || 'unknown') }), ' ', due ? _jsxs(Badge, { tone: "warn", children: ["update due: ", String(st.latest)] }) : st.latest ? _jsx(Badge, { tone: "ok", children: "up to date" }) : null, ' ', LABEL[String(st.status)] && _jsx(Badge, { tone: tone, children: LABEL[String(st.status)] }), ' ', admin && due && !running && _jsx(Act, { label: "Update agent now", primary: true, run: async () => {
                            last.current = 0;
                            setItems([]);
                            setErr('');
                            await api(`/api/assets/${id}/agent-update`, { body: {} });
                            was.current = 'running';
                            await pull();
                        } })] }), due && !st.online && !running && _jsx("p", { className: "note", children: "The agent is offline, so it cannot be asked to update. When the computer is on again it updates by itself; otherwise reinstall it through the site controller." }), st.status === 'stalled' && _jsx("p", { className: "note", children: "The agent did not come back with the new version within 15 minutes. Check that the computer is on and that DeskAsk agent is running, then press Update agent now again." }), _jsx(Err, { text: err }), (items.length > 0 || running) && _jsx(LiveLog, { items: items, running: running })] });
}
