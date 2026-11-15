# Service Desk: incidents and service requests (v3.4.0)

Every ticket is now either an **incident** (something that was working and is not, or is degraded) or a **service request** (something a person asks for, such as a new laptop or access). Tickets that existed before the upgrade, and tickets opened by Monitoring or mail, are incidents; staff can change the type on the ticket.

## Pages
- **Tickets**: every ticket, with a Type column.
- **Incidents**: incidents only. **Major incidents**: incidents flagged as major (staff and viewers).
- **Service requests**: requests only.
- The **New ticket** form asks for the type. Customers use it too; they cannot flag a major incident.

## Impact, urgency and priority
For an incident choose **impact** (how many people or services are hit) and **urgency** (how soon it must be fixed). When no priority is chosen by hand, the priority comes from this table, and the service levels (first reply, solve time) follow the priority:

| Impact \ Urgency | High | Medium | Low |
|---|---|---|---|
| High | Critical | High | Medium |
| Medium | High | Medium | Low |
| Low | Medium | Low | Low |

Choosing a priority by hand on the new-ticket form wins over the table. On an existing ticket, staff changing impact or urgency sets the priority again once both are set (the change is written to the ticket's notes); changing the priority by hand still works.

## Major incidents
Staff tick **Major incident** on the ticket (incidents only). The ticket shows a red "Major incident" badge, is listed under Major incidents, and the time it was flagged is kept. Unticking it removes the flag. Flagging and unflagging are written to the ticket's history and sent to webhooks as ticket updates.

## API
`POST /api/servicedesk/tickets` accepts `kind` (`incident` or `request`), `impact` and `urgency` (`low`, `medium`, `high`). `GET /api/servicedesk/tickets` accepts `kind` and `major=1`. Tickets return `kind`, `impact`, `urgency`, `major` and `majorAt`. `PATCH` (staff) accepts `kind`, `impact`, `urgency` and `major`.
