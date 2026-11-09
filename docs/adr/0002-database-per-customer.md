# ADR 0002: Database per customer

Status: accepted (8 Nov 2026)

Every customer has its own PostgreSQL database. Inside it each module owns one schema. A small platform catalog database lists customers (platform.tenants, made by `tenant init`, see docs/provisioning.md) and is read by the tenancy middleware by request host name. Unknown host: 404.
