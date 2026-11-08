create schema if not exists platform;
create table if not exists platform.tenants (
  id text primary key, name text not null, host text not null unique,
  connection_string text not null, active boolean not null default true, created_at timestamptz not null default now());
insert into platform.tenants (id, name, host, connection_string)
values ('dev', 'Development', 'localhost', 'Host=localhost;Database=deskask_dev;Username=postgres;Password=dev')
on conflict do nothing;

-- SaaS licences: one signed token per customer, issued with tools/DeskAsk.LicenseTool
create table if not exists platform.licenses (
  tenant_id text primary key references platform.tenants(id), token text not null, updated_at timestamptz not null default now());
