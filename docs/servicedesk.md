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

## v4.4.0: ITIL
- **Numbers:** incidents INC-0001, service requests SRV-0001, problems PRB-0001, changes CHG-0001 (four digits at least, more when needed). Tickets from before keep their old TKT number as an alias: replies to old mails and searches still find them. A ticket whose type changes gets a number in the other series and keeps the old one as an alias.
- **Service catalogue (request fulfilment):** administrators add items (Service Desk › Catalogue items) with a category, description, form questions (short/long text, number, date, choice, yes/no, email), who it goes to, how fast it is done, and approvers. People pick an item under *Service catalogue*; the request becomes an SRV ticket with the answers. With approvers it waits on hold (the clock stops) and the approvers are mailed (template *Request waiting for approval*); one approver decides under *Approvals*: approved opens it and the clock runs on, turned down closes it with the reason sent to the requester. Nobody but an administrator approves their own request.
- **Problem management:** impact and urgency set a problem's priority; *Known errors* lists problems with a workaround, searchable by symptoms; a ticket shows the known errors that may match (same category or words of its subject) and links to the problem in one click; a resolved problem (or a known error) resolves its linked open incidents, each getting the resolution or workaround as a reply.
- **Change management:** a change advisory board (Service Desk › Change board) decides who may approve normal changes and how many approvals they need; the board is mailed when a change is submitted (template *Change waiting for approval*). The *Change calendar* shows four weeks of planned changes and marks clashes (same customer or a shared asset, overlapping windows). *Standard changes* are pre-approved templates with a tested plan: raised from there they are approved (or scheduled when a window is given) at once. A change records its category, test plan and the assets (configuration items) it touches.
- **Layout:** the Service Desk has a sidebar grouped by ITIL practice: Incidents, Service requests, Problems, Changes, Insight, Admin.
