import { jsx as _jsx, jsxs as _jsxs } from "react/jsx-runtime";
import { useCallback, useEffect, useRef, useState } from 'react';
import { api, useInterval } from '../http';
import { Act, Badge, Err } from '../ui';
import { LiveLog } from './LiveLog';
const RUNNING = ['queued', 'running'];
const LABEL = { none: 'Not installed', queued: 'Queued', running: 'Installing', done: 'Installed', failed: 'Failed' };
/** Installs the agent through the controller that reaches this device, with a live log of every step from the moment the button is pressed. */
export function AgentInstall({ id, onDone }) {
    const [items, setItems] = useState([]);
    const [st, setSt] = useState({ status: 'none', message: '' });
    const [err, setErr] = useState('');
    const last = useRef(0);
    const was = useRef('none');
    const pull = useCallback(async () => {
        try {
            const r = await api(`/api/assets/${id}/agent-install/log?after=${last.current}`);
            const add = (r.items ?? []);
            if (add.length) {
                last.current = Number(add[add.length - 1].seq);
                setItems((x) => [...x, ...add]);
            }
            setSt({ status: r.status, message: r.message });
            if (RUNNING.includes(was.current) && !RUNNING.includes(String(r.status)))
                onDone?.();
            was.current = String(r.status);
            setErr('');
        }
        catch (e) {
            setErr(e.message);
        }
    }, [id, onDone]);
    useEffect(() => { void pull(); }, [pull]);
    const running = RUNNING.includes(String(st.status));
    useInterval(() => { void pull(); }, running ? 1500 : null);
    const tone = st.status === 'done' ? 'ok' : st.status === 'failed' ? 'bad' : running ? 'warn' : undefined;
    return _jsxs("div", { children: [_jsxs("p", { children: ["Agent installation: ", _jsx(Badge, { tone: tone, children: LABEL[String(st.status)] ?? String(st.status) }), ' ', !running && _jsx(Act, { label: st.status === 'none' ? 'Install agent through a collector' : 'Try again', primary: st.status === 'none', run: async () => {
                            last.current = 0;
                            setItems([]);
                            setErr('');
                            await api(`/api/assets/${id}/agent-install`, { body: {} });
                            was.current = 'queued';
                            setSt({ status: 'queued', message: '' });
                            await pull();
                        } })] }), st.status === 'failed' && st.message && _jsx("p", { className: "err", role: "alert", children: String(st.message) }), _jsx(Err, { text: err }), (items.length > 0 || running) && _jsx(LiveLog, { items: items, running: running })] });
}
