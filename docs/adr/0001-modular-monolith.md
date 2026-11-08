# ADR 0001: Modular monolith

Status: accepted (8 Nov 2026)

One deployable .NET 10 application made of a platform core (tenancy, identity, licensing, audit, settings, notifications, jobs, files, connect, reports) and product modules. Modules talk through interfaces and events, never through each other's tables. Split a module into its own service only when measurements show a need.
