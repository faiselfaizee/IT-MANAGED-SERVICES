import { jsx as _jsx, jsxs as _jsxs, Fragment as _Fragment } from "react/jsx-runtime";
import { useEffect, useState } from 'react';
import { api, go, useLoad } from '../http';
import { Act, Badge, Card, Empty, Err, Field, Loading, fmtDay, useSubmit } from '../ui';
const WHO = { everyone: 'Everyone', company: 'One company', staff: 'Staff only' };
/** Articles that answer common questions: search for one, or (staff) write them. */
export function KbList({ staff }) {
    const [q, setQ] = useState('');
    const [term, setTerm] = useState('');
    useEffect(() => { const h = setTimeout(() => setTerm(q.trim()), 250); return () => clearTimeout(h); }, [q]);
    const { data, error, loading } = useLoad('/api/servicedesk/kb?q=' + encodeURIComponent(term), [term]);
    const list = Array.isArray(data) ? data : [];
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: "Knowledge base" }), _jsxs("div", { className: "toolbar", children: [_jsx("input", { type: "search", "aria-label": "Search articles", placeholder: "Search for an answer", value: q, onChange: (e) => setQ(e.target.value) }), staff && _jsx("a", { className: "button primary", href: "#/servicedesk/kb/new", children: "New article" })] }), _jsx(Loading, { loading: loading && !data, error: error }), data && list.length === 0 && _jsx(Empty, { children: term ? 'No article matches. Open a ticket and we will help.' : staff ? 'No articles yet. Write the first one for the questions people ask most.' : 'No articles yet.' }), list.map((a) => _jsxs(Card, { children: [_jsxs("h3", { children: [_jsx("a", { href: '#/servicedesk/kb/' + a.id, children: a.title }), " ", staff && !a.published && _jsx(Badge, { tone: "warn", children: "Draft" }), " ", staff && _jsx(Badge, { children: WHO[a.visibility] ?? a.visibility })] }), _jsx("p", { className: "muted", children: a.snippet })] }, a.id))] });
}
/** One article, with "was this helpful?" for readers and the editor for staff. */
export function KbArticle({ id, staff }) {
    const { data, error, loading, reload } = useLoad(id === 'new' ? null : '/api/servicedesk/kb/' + id, [id]);
    const [editing, setEditing] = useState(false);
    const [voted, setVoted] = useState(null);
    if (id === 'new')
        return _jsx(KbEdit, { a: null, onDone: (n) => go('servicedesk/kb/' + n) });
    if (!data)
        return _jsx(Loading, { loading: loading, error: error });
    if (editing && staff)
        return _jsx(KbEdit, { a: data, onDone: () => { setEditing(false); reload(); } });
    return _jsxs(_Fragment, { children: [_jsx("p", { children: _jsx("a", { href: "#/servicedesk/kb", children: "\u2039 Knowledge base" }) }), _jsx("h1", { children: data.title }), staff && _jsxs("p", { className: "muted", children: [WHO[data.visibility], " \u00B7 ", data.published ? 'Published' : 'Draft', " \u00B7 ", data.views, " views \u00B7 ", data.helpful, " found it helpful, ", data.notHelpful, " did not \u00B7 updated ", fmtDay(data.updatedAt)] }), _jsx(Card, { children: _jsx("div", { style: { whiteSpace: 'pre-wrap' }, children: data.body }) }), data.published && _jsxs("p", { children: ["Was this helpful?", ' ', _jsx(Act, { label: "Yes", primary: voted === true, run: async () => { await api(`/api/servicedesk/kb/${id}/vote`, { body: { helpful: true } }); setVoted(true); } }), ' ', _jsx(Act, { label: "No", primary: voted === false, run: async () => { await api(`/api/servicedesk/kb/${id}/vote`, { body: { helpful: false } }); setVoted(false); } }), voted === false && _jsxs(_Fragment, { children: [" ", _jsx("a", { href: "#/servicedesk/new", children: "Open a ticket" })] })] }), staff && _jsxs("div", { className: "actions", style: { justifyContent: 'flex-start' }, children: [_jsx("button", { onClick: () => setEditing(true), children: "Edit" }), _jsx(Act, { label: "Delete", confirm: "Delete this article?", run: async () => { await api('/api/servicedesk/kb/' + id, { method: 'DELETE' }); go('servicedesk/kb'); } })] })] });
}
function KbEdit({ a, onDone }) {
    const companies = useLoad('/api/assets/companies');
    const [v, setV] = useState({ title: a?.title ?? '', body: a?.body ?? '', tags: a?.tags ?? '', visibility: a?.visibility ?? 'everyone', companyId: a?.companyId ?? '', published: !!a?.published });
    const set = (k, x) => setV({ ...v, [k]: x });
    const f = useSubmit(() => undefined);
    const cos = Array.isArray(companies.data) ? companies.data : [];
    const save = (publish) => f.submit(async () => {
        const body = { title: v.title, body: v.body, tags: v.tags, visibility: v.visibility, published: publish };
        if (v.visibility === 'company')
            body.companyId = Number(v.companyId);
        const r = a ? await api('/api/servicedesk/kb/' + a.id, { method: 'PUT', body }) : await api('/api/servicedesk/kb', { body });
        onDone(String(r.id));
    });
    return _jsxs(_Fragment, { children: [_jsx("h1", { children: a ? 'Edit article' : 'New article' }), _jsx(Card, { children: _jsxs("div", { className: "form", children: [_jsx(Field, { label: "Title", children: _jsx("input", { value: v.title, maxLength: 200, onChange: (e) => set('title', e.target.value), autoFocus: true }) }), _jsx(Field, { label: "Answer", children: _jsx("textarea", { rows: 12, value: v.body, onChange: (e) => set('body', e.target.value) }) }), _jsx(Field, { label: "Tags", hint: "Words people might search for, separated by commas.", children: _jsx("input", { value: v.tags, onChange: (e) => set('tags', e.target.value) }) }), _jsxs("div", { className: "row", children: [_jsx(Field, { label: "Who can read it", children: _jsx("select", { value: v.visibility, onChange: (e) => set('visibility', e.target.value), children: Object.entries(WHO).map(([k, n]) => _jsx("option", { value: k, children: n }, k)) }) }), v.visibility === 'company' && _jsx(Field, { label: "Company", children: _jsxs("select", { value: v.companyId, onChange: (e) => set('companyId', e.target.value), children: [_jsx("option", { value: "", children: "Choose\u2026" }), cos.map((c) => _jsx("option", { value: c.id, children: c.name }, c.id))] }) })] }), _jsx(Err, { text: f.err }), _jsxs("div", { className: "actions", children: [_jsx("button", { onClick: () => (a ? onDone(String(a.id)) : go('servicedesk/kb')), children: "Cancel" }), _jsx("button", { disabled: f.busy || !v.title.trim(), onClick: () => save(false), children: "Save as draft" }), _jsx("button", { className: "primary", disabled: f.busy || !v.title.trim() || (v.visibility === 'company' && !v.companyId), onClick: () => save(true), children: "Publish" })] })] }) })] });
}
/** On New ticket: articles that match the subject while it is typed, so many questions need no ticket. */
export function Suggestions({ subject }) {
    const [term, setTerm] = useState('');
    useEffect(() => { const h = setTimeout(() => setTerm(subject.trim().length >= 4 ? subject.trim() : ''), 400); return () => clearTimeout(h); }, [subject]);
    const { data } = useLoad(term ? '/api/servicedesk/kb?limit=4&q=' + encodeURIComponent(term) : null, [term]);
    const list = (Array.isArray(data) ? data : []).filter((a) => a.published);
    if (!term || list.length === 0)
        return null;
    return _jsxs("div", { className: "note", role: "region", "aria-label": "Articles that may help", children: [_jsx("strong", { children: "These articles may answer it" }), _jsx("ul", { children: list.map((a) => _jsx("li", { children: _jsx("a", { href: '#/servicedesk/kb/' + a.id, target: "_blank", rel: "noreferrer", children: a.title }) }, a.id)) })] });
}
