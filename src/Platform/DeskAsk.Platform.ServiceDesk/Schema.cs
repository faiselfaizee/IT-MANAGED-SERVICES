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
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) =>
        SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}
