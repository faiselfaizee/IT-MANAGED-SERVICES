# Monitoring (v3.0.0)

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

## Not in this version

- **Other ways to notify:** text messages, Teams, Slack and phone calls are not available. Mail and tickets are.
- **macOS performance data:** the agent does not send it yet. Linux and Windows are supported.
- **More metrics:** per-process figures, network traffic and SNMP interface load are not watched yet. Network devices are watched by ping, ports read at inventory time and hardware state.
- **Escalation:** there is no "mail again if not acknowledged within X minutes".
