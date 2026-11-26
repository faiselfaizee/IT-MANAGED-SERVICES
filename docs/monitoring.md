# Monitoring (v3.0.0, extended in v3.1.0)

Monitoring watches the devices that are already in Assets. It does not need a separate agent or a separate list of devices.

## What it watches

| Source | What it gives Monitoring | Needs |
|---|---|---|
| Agent 3.0 or later | Processor use, memory use, free space on every fixed disk, and time since start, once a minute. Also whether the agent is reporting. | The agent on the computer or server, updated to 3.0 |
| Site controller | Whether the device answers (ping), and since when it has not | A site controller watching the device (asset page, "Read by a controller") |
| Service checks | TCP, HTTP, DNS and certificate checks, with failures in a row and days until a certificate expires | Checks added on the asset's Checks tab |
| Hardware readers | Power supplies, drives, fans, memory and hypervisor hosts that report a fault; datastore free space | A server read through iLO, iDRAC, XClarity, VMware, Nutanix, Proxmox or XCP-ng |

A device is **watched** when it has an agent, a site controller pings it, or it has service checks. Everything else counts as **not watched**.

Performance data is kept as minute readings for two days and as hourly averages and peaks for 90 days. The agent sends a reading every 60 seconds. To change the interval, set `Monitoring:MetricsSeconds` (15 to 3600) on the server; agents follow the server's answer.

## Screens: three levels, as in Assets

1. **Customer tiles** (Monitoring › Dashboard). Each customer has one tile with a ring of its device states (OK, warning, critical, muted, not watched). The tile also counts critical and warning alerts, devices down, disks nearly full, failing checks and expiring certificates. Customers with critical alerts come first. A customer administrator lands straight on their own customer.
2. **A customer's rings.** Click a tile to see device status, open alerts, open alerts by kind, disk space, service checks and certificates.
3. **The device list.** Click any part of a ring to open the list filtered to it. Each device row opens a **device page** with:
   - live figures
   - charts for 6 hours to 90 days (hover for the values, or show the same numbers as a table)
   - disks, service checks and hardware faults
   - the device's alerts
   - a maintenance mute

The pages refresh every minute. Administrators also have **Check now**, which evaluates every rule at once.

## Alert rules

Every minute each device is checked against the rules that are switched on. The starting rules are:

| Rule | Severity | Devices | Then | On at the start |
|---|---|---|---|---|
| Server or network device down for 5 minutes | Critical | Servers, network | Mail and ticket (critical) | Yes |
| Computer not reporting for a day | Warning | Computers | Mail | No (computers are switched off at night) |
| Processor above 90% for 15 minutes | Warning | Servers | Mail | Yes |
| Memory above 90% for 15 minutes | Warning | Servers | Mail | Yes |
| Disk space below 10% | Warning | All | Mail | Yes |
| Disk space below 5% | Critical | All | Mail and ticket (high) | Yes |
| Service check failing 2 times in a row | Critical | All | Mail and ticket (high) | Yes |
| Certificate expires within 14 days | Warning | All | Mail | Yes |
| Hardware fault | Critical | All | Mail and ticket (high) | Yes |

How rules behave:
- **Most serious wins.** When several rules hit the same thing, for example a disk below 10% and below 5%, only the most serious one opens an alert. If a warning grows into a critical, the alert is raised to critical and mailed again.
- **"Down"** means one of two things. Either the site controller has had no answer for the set time, or the agent has been silent for that time and no site controller reports the device as up.
- **"Above X% for N minutes"** means even the lowest reading in that window was above X. At least two readings covering most of the window are needed, so a single spike never opens an alert.
- **Certificates:** an expired certificate is always critical. A hardware part in the "Warning" state opens a warning, whatever the rule's severity.
- **Changing rules:** administrators change the rules under Monitoring › Alert rules. A rule can be limited to one customer and to kinds of device (computers, servers, network devices, printers, other).

## Alerts

- **Clearing:** an alert clears by itself once the problem has gone for **two checks in a row** (about two minutes), so one good reading does not close it. It closes at once when the device is muted, retired or removed, or when its rule is switched off or deleted.
- **Mail:** opening an alert, or raising it to critical, mails the recipients. Clearing mails them too, if that is switched on.
  - The wording is in Mail › Templates ("Monitoring alert" and "Monitoring alert cleared").
  - Links in the mail use the public address set in Mail settings.
- **Tickets:** a rule with "Open a Service Desk ticket" opens a ticket with that rule's priority and the customer's service levels. The ticket is linked to the asset, and its source is "monitoring". When the alert clears or is raised to critical, the ticket gets a note. Any open alert without a ticket can also get one with **Open ticket**. Tickets need the Service Desk module in the licence.
- **Acknowledge:** shows the team who is on it. It does not close the alert.

## Recipients

Monitoring › Settings (administrators):
- **Default list:** one list of addresses for every customer.
- **Customer lists:** a customer can have its own list, which replaces the default for that customer's alerts.
- **Mail when an alert clears:** on or off.

Mail must be set up under Mail.

## Maintenance mute

On a device page, choose **Mute** for 1 hour up to a week, or until someone unmutes it, and give a reason if you like.
- While muted, no alerts open for the device, and its open alerts close with the reason "Device muted".
- The mute ends by itself.
- Muting and unmuting are written to the audit log.

## Who may do what

| Role | Sees | Acknowledges, mutes, opens tickets | Rules, settings, Check now |
|---|---|---|---|
| Administrator | Every customer | Yes | Yes |
| Engineer | Every customer | Yes | No |
| Viewer | Every customer | No | No |
| Customer administrator | Their own customer | No | No |
| Customer user | Nothing | No | No |

Under Access, "Monitoring" grants (read or modify, for all customers or one) work as they do for Assets and Service Desk.

## Added in v3.1.0

### Port traffic (network devices)
- **What is read:** for devices read in SNMP or Network device mode, the site controller reads every port's counters every 5 minutes: state, speed, traffic in and out (as a share of the port speed and in bits per second), and errors and discards per minute. It uses the device's SNMP login, v2c or v3.
- **Where it shows:** the device page lists the ports, and **Traffic** opens a chart (6 hours to 30 days). Readings are kept 2 days, hourly figures 30 days.
- **Watched ports:** ports with a description (such as "Uplink to FW") are watched by default. Tick or untick others on the device page. A watched port that goes down opens an alert.
- **New rules:** "Port busy above 80% for 15 minutes" (warning) and "Watched port down for 5 minutes" (critical, opens a ticket).

### Services and programs (agent)
- **Rule kinds:** "Service not running" and "Program not running". Each names what to watch, for example `Spooler, MSSQLSERVER` or `sqlservr.exe, nginx`. Agents 3.1 and later report on those names once a minute: Windows services through the service manager, Linux services through systemd, and programs from the running process list.
- **Restart:** a service rule can ask the agent to restart the service once when the alert opens. The restart runs as an agent job, shown under the asset's commands and in the audit log, and the alert notes it.

### Device messages: syslog, SNMP traps and Windows event logs
- **Syslog and traps:** the site controller receives syslog on UDP 514 and SNMP traps (v1, v2c) on UDP 162.
  - Point devices at the controller's address.
  - Messages from addresses that are not devices of that site are ignored, and the controller log says which addresses they came from.
  - A source sending more than 120 messages a minute is cut off for the rest of the minute.
  - SNMPv3 traps are not decoded yet.
- **Windows event logs:** agents send new errors and critical events from the System and Application logs, when a message rule asks for them.
- **Messages page:** Monitoring › Messages lists everything, and each device page shows its own. Messages are kept 14 days.
- **Rule kind "Device messages":** N messages of a kind (any, syslog, trap or Windows event), at a level or worse, optionally containing a text (or program or trap name), within M minutes. The starting rule "Critical message from a device" fires on any message at critical or worse.

### Microsoft Teams
- **Setting it up:**
  1. In Teams, create a webhook for a channel: Workflows › "Post to a channel when a webhook request is received". Older incoming webhooks also work.
  2. Paste the address in Monitoring › Settings.
  3. Use **Send a test message** to check it.
  A customer can have its own channel.
- **What gets posted:** alerts post a card with the device, customer, rule, time and a button that opens the device; cleared alerts post too, if that is switched on. Each rule has "Post to the Teams channel" (on by default).
- **Delivery:** posts are retried after 1, 2, 4 and 8 minutes if Teams does not answer. Webhook addresses are stored encrypted, and settings show only their host.

### Escalation
If nobody acknowledges a critical alert within the set minutes, it goes once to an escalation mail list and Teams channel. The mail template is "Monitoring alert not acknowledged".

### Monthly uptime report
- **What it shows:** Monitoring › Uptime report shows, per customer and month:
  - availability by device (the share of the month with no open "down" alert)
  - outages and total down time
  - alerts by kind
  - tickets opened from alerts
  You can download it as a PDF.
- **Monthly mail:** when switched on in settings, the report goes out as a PDF on the 1st of every month (or by the 3rd, if the server was off). The all-customers report goes to the listed addresses, and each customer with its own recipients gets theirs. **Send the reports for this month now** sends any month again.
- **Mail attachments:** mail can now carry attachments, through both SMTP and Microsoft 365.

## Not in this version

- **Other ways to notify:** text messages, Slack and phone calls are not available. Mail, Teams and tickets are.
- **macOS performance data:** the agent does not send it yet. Linux and Windows are supported.
- **More metrics:** per-process processor and memory figures, NetFlow traffic analysis and topology maps are not available yet.

## Services of computers and servers (v3.4.0)

The agent (version 3.4 and later) lists **every service** of a Windows computer or Linux (systemd) server with its state and start type, a few minutes after it starts and then every five minutes. The list is on the device's Monitoring page, under **Services**.

- **Servers watch their automatic services.** The starting rule "Automatic service stopped" opens a warning when a service that starts automatically is stopped or failed. Update helpers and per-user services that stop by themselves (Google Update, Edge Update, Software Protection, per-user services with a hexadecimal suffix and similar) are left out; tick one to watch it anyway. On Linux only services enabled at boot count, and only a failed unit alerts.
- **Computers watch only what you choose.** Tick the services that matter on the device's Services list.
- **Choose per device.** Tick or untick any service on the list. "Back to the default" removes your choice.
- **Custom services.** Type a service name (or systemd unit) under the list and choose **Watch it**. This also works for a service that is not on the list (for example one installed later). The agent reports chosen services every minute, so these alerts open fastest. "Not installed" is reported when the name does not exist.
- **Network devices, printers and other devices without an agent** have no service list. Watch what they offer (web page, port, certificate, DNS) with **service checks** on the asset's Service checks tab; failing checks open alerts with the "Service check failing" rule.
- **Change who is told.** The rule is under Monitoring › Alert rules like any other (severity, mail, ticket, customer).

## v4.3.0: acknowledged alerts count as Ok
An acknowledged alert is being handled: the device shows Ok in the rings, the device list and the counts while the alert stays open (marked acknowledged on the Alerts page) until it clears. The dashboards also show how many alerts are acknowledged.

## 6.3: Monitoring › Reports

Monthly uptime report (the PDF mailed on the 1st), alert history, busiest devices (processor and memory average and highest, lowest free disk), disk space now, network port traffic, and service status. Filters: customer and period (24 hours to 90 days); every report exports to CSV.

## 6.5: Printer rules and tickets that resolve themselves

**Printer rules.** Two new metrics read what a printer reports about itself (see Assets, Printers):
- *Supply*: a toner, ink or other supply at or below a percentage, or a waste container nearly full. An empty supply is always critical. Readings older than 60 minutes are ignored.
- *Printer fault*: paper jam, open door, no paper, offline, service requested, output tray full, cartridge or tray missing. If none of these applies, up to three alerts the printer itself calls critical are passed on.

Four built-in rules are added: toner at 10% or less (mail), toner nearly empty at 3% or less (critical, opens a ticket), printer fault, and printer not answering for 15 minutes.

**Resolve the ticket when the alert clears.** A rule that opens tickets can also resolve them. When the alert has cleared (two clean runs), the ticket is resolved only if nobody has worked on it: it is still *open*, has no assignee, no first response and no comment from a person. Otherwise Monitoring only adds a note and leaves the ticket to its owner. If the same alert returns within 24 hours, the same ticket is reopened instead of a new one being opened; a ticket a person resolved or closed is never reopened, a new ticket is opened. A muted device never resolves a ticket. The five built-in rules that open tickets have this on; custom rules have it off until ticked.

## Starting from nothing, and what the licence decides (6.5.5)

On a new customer the Monitoring dashboard shows a short checklist with links: add the customer (Assets › Companies), save its logins (Assets › Credentials), install the agent or a site controller, then choose who gets the alert mails (Monitoring › Settings; Mail must be set up).

The menu follows the licence: Service Desk, Assets, Monitoring, AD Manager, Vault and Backup appear (menu entry, Home tile and page) only when the licence includes them, or keeps them read-only after it ended. A module that is not in the licence is not shown at all. A trial or purchase of Monitoring always includes Asset management, because Monitoring watches the devices kept there.

## Setup is under Administration (6.6.0)

Customers, credentials, agents and discovery are at Administration › Devices setup, shared with Asset management, so Monitoring can be used without the Assets licence.

## Alert routing (6.22.0)

Monitoring › Settings › Alert routing sends some alerts to different people. A route says which alerts it takes (a kind of device, a customer and a severity; leave a choice empty for any) and which addresses get them. The **first route that fits** an alert is used. An alert that fits no route goes to the customer's own list if it has one, else to the default list, as before. Routes apply to the alert mail (opening and clearing); Teams channels and escalation are unchanged. Example: printers to the local IT team, network devices to the network team, everything critical for one customer to its manager. Order matters: use "Up" to move a route higher. API: `routes` in `GET/PUT /api/monitoring/settings` (each `{name, kinds, companyId, severity, to}`).
