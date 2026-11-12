namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Schema "servicedesk" in every customer database: tickets, their timeline, attachments and the event log other parts (mail, webhooks) read.</summary>
public sealed class ServiceDeskSchema : ITenantSchema
{
    public string Name => "servicedesk";
    public static readonly Migration[] Migrations =
    {
        new(1, "people, tickets, comments, attachments, events", @"
            create schema if not exists servicedesk;
            create sequence if not exists servicedesk.ticket_seq;
            create table if not exists servicedesk.people (
              user_id uuid primary key, company_id int, may_raise boolean not null default true, may_modify boolean not null default true,
              updated_at timestamptz not null default now());
            create table if not exists servicedesk.tickets (
              id bigserial primary key,
              number text not null default ('TKT-' || lpad(nextval('servicedesk.ticket_seq')::text, 6, '0')),
              subject text not null, description text not null default '',
              status text not null default 'open' check (status in ('open','progress','hold','resolved','closed')),
              priority text not null default 'medium' check (priority in ('critical','high','medium','low')),
              category text not null default '',
              company_id int, company_name text not null default '',
              requester_id uuid, requester_name text not null default '', requester_email text not null default '',
              assignee_id uuid, assignee_name text not null default '',
              asset_id bigint, asset_name text not null default '',
              source text not null default 'web',
              created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
              first_response_at timestamptz, resolved_at timestamptz, closed_at timestamptz,
              respond_due timestamptz, resolve_due timestamptz, paused_at timestamptz, paused_s bigint not null default 0,
              resp_flag boolean not null default false, sla_warned int not null default 0,
              created_by text not null default '');
            create unique index if not exists tickets_number on servicedesk.tickets (number);
            create index if not exists tickets_status on servicedesk.tickets (status);
            create index if not exists tickets_company on servicedesk.tickets (company_id);
            create index if not exists tickets_assignee on servicedesk.tickets (assignee_id);
            create index if not exists tickets_requester on servicedesk.tickets (requester_id);
            create index if not exists tickets_created on servicedesk.tickets (created_at desc);
            create table if not exists servicedesk.comments (
              id bigserial primary key, ticket_id bigint not null references servicedesk.tickets(id) on delete cascade,
              at timestamptz not null default now(), author_id uuid, author_name text not null default '',
              kind text not null check (kind in ('public','internal','system')), body text not null);
            create index if not exists comments_ticket on servicedesk.comments (ticket_id, id);
            create table if not exists servicedesk.attachments (
              id bigserial primary key, ticket_id bigint not null references servicedesk.tickets(id) on delete cascade,
              name text not null, content_type text not null default 'application/octet-stream', size int not null, data bytea not null,
              at timestamptz not null default now(), by_name text not null default '', internal boolean not null default false);
            create index if not exists attachments_ticket on servicedesk.attachments (ticket_id, id);
            create table if not exists servicedesk.events (
              id bigserial primary key, ticket_id bigint not null, kind text not null, at timestamptz not null default now(), data text not null default '{}');
            create index if not exists events_ticket on servicedesk.events (ticket_id, id)"),
        new(2, "changes, approvals, problems, links, notes", @"
            create sequence if not exists servicedesk.change_seq;
            create sequence if not exists servicedesk.problem_seq;
            create table if not exists servicedesk.changes (
              id bigserial primary key,
              number text not null default ('CHG-' || lpad(nextval('servicedesk.change_seq')::text, 6, '0')),
              title text not null, description text not null default '',
              type text not null default 'normal' check (type in ('normal','standard','emergency')),
              status text not null default 'draft' check (status in ('draft','review','approved','rejected','scheduled','implementing','completed','failed','cancelled')),
              risk text not null default 'low' check (risk in ('low','medium','high')),
              impact text not null default 'low' check (impact in ('low','medium','high')),
              reason text not null default '', plan text not null default '', rollback text not null default '', outcome text not null default '',
              company_id int, company_name text not null default '',
              owner_id uuid, owner_name text not null default '',
              start_at timestamptz, end_at timestamptz, quorum int not null default 1,
              created_at timestamptz not null default now(), updated_at timestamptz not null default now(), created_by text not null default '');
            create unique index if not exists changes_number on servicedesk.changes (number);
            create index if not exists changes_status on servicedesk.changes (status);
            create table if not exists servicedesk.change_votes (
              change_id bigint not null references servicedesk.changes(id) on delete cascade,
              user_id uuid not null, user_name text not null default '', decision text not null check (decision in ('approve','reject')),
              comment text not null default '', at timestamptz not null default now(), primary key (change_id, user_id));
            create table if not exists servicedesk.problems (
              id bigserial primary key,
              number text not null default ('PRB-' || lpad(nextval('servicedesk.problem_seq')::text, 6, '0')),
              title text not null, description text not null default '',
              status text not null default 'open' check (status in ('open','investigating','known_error','resolved','closed')),
              priority text not null default 'medium' check (priority in ('critical','high','medium','low')),
              category text not null default '', root_cause text not null default '', workaround text not null default '', resolution text not null default '',
              owner_id uuid, owner_name text not null default '',
              created_at timestamptz not null default now(), updated_at timestamptz not null default now(), created_by text not null default '');
            create unique index if not exists problems_number on servicedesk.problems (number);
            create table if not exists servicedesk.problem_tickets (problem_id bigint not null references servicedesk.problems(id) on delete cascade, ticket_id bigint not null references servicedesk.tickets(id) on delete cascade, primary key (problem_id, ticket_id));
            create table if not exists servicedesk.change_tickets (change_id bigint not null references servicedesk.changes(id) on delete cascade, ticket_id bigint not null references servicedesk.tickets(id) on delete cascade, primary key (change_id, ticket_id));
            create table if not exists servicedesk.change_problems (change_id bigint not null references servicedesk.changes(id) on delete cascade, problem_id bigint not null references servicedesk.problems(id) on delete cascade, primary key (change_id, problem_id));
            create table if not exists servicedesk.item_notes (
              id bigserial primary key, kind text not null check (kind in ('change','problem')), item_id bigint not null,
              at timestamptz not null default now(), author_name text not null default '', body text not null);
            create index if not exists item_notes_item on servicedesk.item_notes (kind, item_id, id)"),
        new(3, "api keys and webhooks", @"
            create table if not exists servicedesk.api_keys (
              id serial primary key, name text not null, prefix text not null, hash text not null unique, scope text not null check (scope in ('read','write')),
              created_at timestamptz not null default now(), created_by text not null default '', last_used_at timestamptz, revoked_at timestamptz);
            create table if not exists servicedesk.webhooks (
              id serial primary key, url text not null, secret text not null, events text not null default '*', active boolean not null default true,
              created_at timestamptz not null default now(), created_by text not null default '');
            create table if not exists servicedesk.webhook_deliveries (
              id bigserial primary key, hook_id int not null references servicedesk.webhooks(id) on delete cascade, event_id bigint not null, kind text not null, payload text not null,
              status text not null default 'pending' check (status in ('pending','sent','failed')), attempts int not null default 0, http_code int, error text not null default '',
              created_at timestamptz not null default now(), sent_at timestamptz, next_at timestamptz not null default now());
            create index if not exists webhook_deliveries_due on servicedesk.webhook_deliveries (status, next_at);
            create index if not exists webhook_deliveries_hook on servicedesk.webhook_deliveries (hook_id, id desc);
            create table if not exists servicedesk.webhook_state (id int primary key check (id = 1), last_event bigint not null)"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) =>
        SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}
