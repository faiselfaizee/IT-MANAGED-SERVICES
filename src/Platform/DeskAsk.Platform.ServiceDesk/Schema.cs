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
        new(4, "monthly report mail", @"
            create table if not exists servicedesk.report_sent (
              month text not null, scope text not null, recipient text not null, at timestamptz not null default now(), primary key (month, scope, recipient))"),
        new(5, "change stages", @"
            alter table servicedesk.changes drop constraint if exists changes_status_check;
            alter table servicedesk.changes add constraint changes_status_check check (status in ('draft','review','approved','rejected','scheduled','implementing','completed','failed','cancelled','closed'));
            alter table servicedesk.changes add column if not exists impact_analysis text not null default '';
            alter table servicedesk.changes add column if not exists review_notes text not null default '';
            alter table servicedesk.changes add column if not exists closure_code text not null default '';
            alter table servicedesk.changes add column if not exists closed_at timestamptz;
            create table if not exists servicedesk.change_tasks (
              id bigserial primary key, change_id bigint not null references servicedesk.changes(id) on delete cascade,
              stage text not null check (stage in ('planning','implementation')), title text not null,
              assignee_id uuid, assignee_name text not null default '', done boolean not null default false, done_at timestamptz, done_by text not null default '',
              position int not null default 0, created_at timestamptz not null default now());
            create index if not exists change_tasks_change on servicedesk.change_tasks (change_id, stage, position, id)"),
        new(6, "custom fields", @"
            create table if not exists servicedesk.custom_fields (
              id serial primary key, key text not null unique, label text not null,
              kind text not null check (kind in ('text','longtext','number','date','choice','checkbox')), options text not null default '',
              required boolean not null default false, staff_only boolean not null default false, active boolean not null default true,
              position int not null default 0, created_at timestamptz not null default now());
            alter table servicedesk.tickets add column if not exists custom jsonb not null default '{}'::jsonb"),
        new(7, "incidents and service requests, impact and urgency, major incidents", @"
            alter table servicedesk.tickets add column if not exists kind text not null default 'incident';
            alter table servicedesk.tickets drop constraint if exists tickets_kind_check;
            alter table servicedesk.tickets add constraint tickets_kind_check check (kind in ('incident','request'));
            alter table servicedesk.tickets add column if not exists impact text not null default '';
            alter table servicedesk.tickets add column if not exists urgency text not null default '';
            alter table servicedesk.tickets add column if not exists major boolean not null default false;
            alter table servicedesk.tickets add column if not exists major_at timestamptz;
            create index if not exists tickets_kind on servicedesk.tickets (kind, status)"),
        new(8, "ITIL numbers (INC, SRV, PRB, CHG), service catalogue and request approvals, change board, standard changes, linked assets", @"
            create sequence if not exists servicedesk.inc_seq;
            create sequence if not exists servicedesk.srv_seq;
            create or replace function servicedesk.ref(prefix text, n bigint) returns text language sql immutable as
              $f$ select prefix || '-' || case when n < 10000 then lpad(n::text, 4, '0') else n::text end $f$;
            alter table servicedesk.tickets add column if not exists aliases text[] not null default '{}';
            create index if not exists tickets_aliases on servicedesk.tickets using gin (aliases);
            alter table servicedesk.tickets alter column number drop default;
            -- existing tickets get their INC or SRV number in the order they were opened; the old TKT number stays as an alias (mail replies, searches)
            do $d$ declare r record; begin
              for r in select id, kind, number from servicedesk.tickets where number like 'TKT-%' order by id loop
                update servicedesk.tickets set aliases = aliases || r.number,
                  number = servicedesk.ref(case when r.kind = 'request' then 'SRV' else 'INC' end, nextval((case when r.kind = 'request' then 'servicedesk.srv_seq' else 'servicedesk.inc_seq' end)::regclass))
                  where id = r.id;
              end loop; end $d$;
            create or replace function servicedesk.ticket_number() returns trigger language plpgsql as $f$
            begin
              if tg_op = 'UPDATE' then
                if new.kind is not distinct from old.kind then return new; end if;
                new.aliases := old.aliases || old.number;
              end if;
              new.number := servicedesk.ref(case when new.kind = 'request' then 'SRV' else 'INC' end,
                nextval((case when new.kind = 'request' then 'servicedesk.srv_seq' else 'servicedesk.inc_seq' end)::regclass));
              return new;
            end $f$;
            drop trigger if exists tickets_number on servicedesk.tickets;
            create trigger tickets_number before insert or update of kind on servicedesk.tickets for each row execute function servicedesk.ticket_number();
            -- problems and changes: same four-digit style for new ones; existing numbers keep their value with less padding
            alter table servicedesk.problems alter column number set default servicedesk.ref('PRB', nextval('servicedesk.problem_seq'));
            alter table servicedesk.changes alter column number set default servicedesk.ref('CHG', nextval('servicedesk.change_seq'));
            update servicedesk.problems set number = servicedesk.ref('PRB', substring(number from 5)::bigint) where number ~ '^PRB-0+[0-9]{4,}$' or number ~ '^PRB-[0-9]{6}$';
            update servicedesk.changes set number = servicedesk.ref('CHG', substring(number from 5)::bigint) where number ~ '^CHG-[0-9]{6}$';
            alter table servicedesk.problems add column if not exists impact text not null default '';
            alter table servicedesk.problems add column if not exists urgency text not null default '';
            alter table servicedesk.problems add column if not exists known_error_at timestamptz;
            alter table servicedesk.changes add column if not exists category text not null default '';
            alter table servicedesk.changes add column if not exists test_plan text not null default '';
            alter table servicedesk.changes add column if not exists asset_ids bigint[] not null default '{}';
            alter table servicedesk.changes add column if not exists template_id int;
            -- the service catalogue: what people can ask for, with its own form and an approval step
            create table if not exists servicedesk.catalog (
              id serial primary key, name text not null, category text not null default 'General', description text not null default '',
              icon text not null default '', fields jsonb not null default '[]', approvers uuid[] not null default '{}',
              assignee_id uuid, assignee_name text not null default '', priority text not null default 'medium',
              resolve_hours int, staff_only boolean not null default false, active boolean not null default true, position int not null default 0,
              created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            alter table servicedesk.tickets add column if not exists catalog_id int;
            alter table servicedesk.tickets add column if not exists form jsonb not null default '{}';
            alter table servicedesk.tickets add column if not exists approval text not null default '';
            alter table servicedesk.tickets drop constraint if exists tickets_approval_check;
            alter table servicedesk.tickets add constraint tickets_approval_check check (approval in ('','pending','approved','rejected'));
            alter table servicedesk.tickets add column if not exists approval_by text not null default '';
            alter table servicedesk.tickets add column if not exists approval_at timestamptz;
            alter table servicedesk.tickets add column if not exists approval_note text not null default '';
            create index if not exists tickets_approval on servicedesk.tickets (approval) where approval = 'pending';
            -- standard changes: pre-approved, repeatable work
            create table if not exists servicedesk.change_templates (
              id serial primary key, name text not null unique, category text not null default '', description text not null default '',
              plan text not null default '', rollback text not null default '', test_plan text not null default '', risk text not null default 'low',
              active boolean not null default true, created_by text not null default '', created_at timestamptz not null default now())"),
        new(9, "knowledge base and satisfaction rating", @"
            create table if not exists servicedesk.articles (
              id serial primary key, title text not null, body text not null default '', tags text not null default '',
              visibility text not null default 'everyone' check (visibility in ('everyone','company','staff')),
              company_id int, published boolean not null default false,
              helpful int not null default 0, not_helpful int not null default 0, views int not null default 0,
              created_by text not null default '', created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            create index if not exists articles_published on servicedesk.articles (published, visibility);
            create table if not exists servicedesk.article_votes (
              article_id int not null references servicedesk.articles(id) on delete cascade, user_id uuid not null, helpful boolean not null,
              at timestamptz not null default now(), primary key (article_id, user_id));
            alter table servicedesk.tickets add column if not exists rating text;
            alter table servicedesk.tickets drop constraint if exists tickets_rating_check;
            alter table servicedesk.tickets add constraint tickets_rating_check check (rating is null or rating in ('up','down'));
            alter table servicedesk.tickets add column if not exists rating_comment text not null default '';
            alter table servicedesk.tickets add column if not exists rated_at timestamptz"),
        new(10, "canned replies and working hours", @"
            create table if not exists servicedesk.canned (
              id serial primary key, title text not null, body text not null, active boolean not null default true,
              created_by text not null default '', created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            create unique index if not exists canned_title on servicedesk.canned (lower(title));
            -- the moment a target is reached when only working time counts: from t0, spend secs seconds inside the working window (minutes since midnight s..e on the ISO weekdays in d, in the zone tz)
            create or replace function servicedesk.work_due(t0 timestamptz, secs double precision, on_ boolean, s int, e int, d int[], tz text) returns timestamptz language plpgsql stable as $f$
            declare cur timestamp; rem double precision := secs; day date; opens timestamp; closes timestamp; avail double precision; n int := 0;
            begin
              if on_ is not true or e <= s or coalesce(array_length(d, 1), 0) = 0 then return t0 + make_interval(secs => secs); end if;
              cur := t0 at time zone tz;
              loop
                n := n + 1;
                exit when n > 4000;
                day := cur::date;
                if extract(isodow from day)::int = any(d) then
                  opens := day + make_interval(mins => s); closes := day + make_interval(mins => e);
                  if cur < opens then cur := opens; end if;
                  if cur < closes then
                    avail := extract(epoch from closes - cur);
                    if rem <= avail then return (cur + make_interval(secs => rem)) at time zone tz; end if;
                    rem := rem - avail;
                  end if;
                end if;
                cur := (day + 1)::timestamp;
              end loop;
              return cur at time zone tz;
            end $f$"),
    };
    public Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct) =>
        SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations, ct);
}
