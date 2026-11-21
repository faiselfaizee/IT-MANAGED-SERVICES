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

## Knowledge base and satisfaction rating (6.7.0)

- **Knowledge base** (Service Desk › Knowledge base): articles with a title, an answer, tags and a reader group: **everyone**, **one company**, or **staff only**. Only our staff (admin, engineer) write, edit, publish and delete them; customers do not. A draft is seen by staff only. People search title, tags and text; the list puts title matches first, then the most helpful.
- **Was this helpful?** One yes/no per person (changeable); counts show to staff. A "no" points to Open a ticket.
- **Suggestions while typing:** on New ticket, a customer typing a subject of 4 or more characters sees up to 4 matching published articles beside the form.
- **Rating:** when a ticket is resolved or closed, the customer who raised it (or a colleague who may change the ticket) can give a thumbs up or down and a short comment, and change their mind. Staff and other companies cannot. Staff see the rating on the ticket. Audited as `servicedesk.ticket_rated`; articles as `servicedesk.article_created/updated/deleted`.
- API: `GET/POST /api/servicedesk/kb`, `GET/PUT/DELETE /api/servicedesk/kb/{id}`, `POST /api/servicedesk/kb/{id}/vote`, `POST /api/servicedesk/tickets/{id}/rating`.
- Not yet: rating in the monthly report and dashboard; articles from known errors; mail link to rate. Next release in this wave: better My tickets and picking the affected device.

## My tickets and the affected device (6.8.0)

- **My tickets** (customers): the ticket list opens as My tickets with groups **Open**, **Waiting for you** (the ticket is on hold for their reply; shown with a count and a badge), **Finished** and **All**, a search box, the due date in plain words ("We aim to fix it in 3 hours", "Overdue by 2 days", "Waiting for your reply"), and a **Reopen** button on resolved or closed tickets. Staff keep the full ticket list.
- **Which device is it about?** On New ticket a customer picks one of their own devices from Asset management without having access to Assets: the devices assigned to them (by name or email), and for a customer administrator all of the company's devices. Retired and lost devices are left out. The ticket carries the device, so staff see its tag and details; the server refuses any other device. If Asset management is not set up the list is empty and the field is not shown. API: `GET /api/servicedesk/my-devices`; `assetId` on `POST /api/servicedesk/tickets`.
- Not yet: copy a colleague on a ticket (needs a mail and permission design), rating in reports.
