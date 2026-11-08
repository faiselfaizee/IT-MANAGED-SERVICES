# ADR 0005: Agent

Status: accepted (8 Nov 2026)

One Go agent per customer site. It only calls out over TLS (heartbeat first, plugins for assets, monitoring, AD and backup later). Nothing connects in.
