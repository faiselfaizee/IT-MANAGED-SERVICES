# Backup

Backup does two things: it watches the backup software your customers already run (it only reads their results), and since 5.2 the DeskAsk agent makes file backups itself (see File backups below).

## Where results come from

| Software | Route | Set-up |
|---|---|---|
| Veeam Backup & Replication, Veeam Agent for Windows | Agent | DeskAsk agent 5.1 or later on the Veeam server or the computer. It reads the `Veeam Backup` log (events 190, 194, 390, 490, 590, 790) and the `Veeam Agent` log (event 190). Veeam can also forward syslog or send report mails. |
| Windows Server Backup | Agent | `Get-WBJob -Previous 20` on the server. |
| Azure Recovery Services agent (MARS) | Agent | `Get-OBJob -Previous 20` on the protected server. |
| Azure Backup Server (MABS) / System Center DPM | Agent | `Get-DPMJob` for the last three days, one job per protection group and data source. |
| Synology Hyper Backup, Active Backup for Business | Syslog or mail | Log Center (or Active Backup › Settings › Log forwarding) to the site controller, UDP 514. The NAS must be an asset of that site. |
| QNAP Hybrid Backup Sync | Syslog or mail | Notification Center › system logs to the site controller, UDP 514. |
| Acronis, Macrium, Commvault and others | Mail or report address | Mail notifications from a listed sender, or a post-job script calling the report address. |
| Microsoft Azure Backup (vault alerts) | Mail | Azure Monitor action group email to the Service Desk mailbox; list `azure-noreply@microsoft.com` with the customer. |

The agent reads results a couple of minutes after it starts and then every 15 minutes, and only sends when it finds backup software. Results of the last three days are read each time; a run is stored once (by its key).

### Report mails

Under **Backup › Report mails**, list the senders whose mail is a backup report: an address (`backup@nas.customer.com`) or a domain (`@customer.com`), with the customer and, if the mail does not say, the software. Mail from these senders is read by Backup and never becomes a ticket; all other mail goes to the Service Desk as before. The result comes from the subject:

- Veeam: `[Success] Job name (3 machines)`, `[Warning] ...`, `[Failed] ...`; `... on COMPUTER` names the device.
- Others: words such as *completed successfully*, *succeeded*, *with warnings*, *partially completed*, *failed*, *error*. A name in quotes is the job (Acronis: `plan 'Daily' on machine 'SRV01'`), otherwise the subject without the result words and dates.

A listed sender's mail that reports no result (a weekly summary) is kept out of the Service Desk but stores nothing.

### Report address

`POST /api/public/backup/report` with `Authorization: Bearer <key>` and JSON:

| Field | |
|---|---|
| `job` | needed |
| `result` | needed: `success`, `warning` or `failed` (also `ok`, `error`, `0`/`1`/`2`) |
| `device` | a device name or asset tag; matched to the customer's assets |
| `tool`, `started`, `ended`, `message`, `bytes` | optional |
| `key` | optional; the same key twice stores the run once |

Keys are made under **Backup › Report address**, one per customer, shown once (only a hash is kept), and can be revoked.

## Jobs and status

A job is one customer, device, software and job name. It appears with its first result and is expected **every 24 hours**; change that on the job (168 for weekly, 0 for no schedule). Status:

| Status | Meaning |
|---|---|
| Success, Warning, Failed | the newest run's result |
| Missed | nothing arrived for the interval plus the grace hours (6 by default) |
| Waiting | no result yet, still within the interval |
| Not watched | switched off on the job: no status, no alerts |

## Alerts

Under **Backup › Alerts**: recipients (everyone, plus a customer's own list), how many failures in a row alert (1 by default), whether warnings alert, the grace hours, a Service Desk ticket per alert (on, priority high) and a mail when the job is back to normal. An alert opens once (mail `backup_alert`, ticket), follows the job if it changes from failed to missed, and clears when a run succeeds (ticket note, mail `backup_resolved`). Both templates can be edited under Administration › Mail.

## Access

Staff and viewers see every customer, engineers change and delete jobs, customer administrators see their own customer; settings, senders and keys are for administrators. Backup is in the Users › Permissions grants list.

## File backups (agent 5.2 and later)

Under **Backup › File backups** an administrator makes a plan for a device with the agent: the folders, what to leave out, when (daily from an hour of the device's clock, every 1 to 12 hours, weekly, or only when asked), how long backups are kept (the last N, plus every backup of the last D days) and where they go:

| Where | What to give |
|---|---|
| Folder or USB disk | a folder on the device (`D:\Backups`, `E:\`) |
| SMB share | `\\server\share\folder`, and an account (domain\user) or nothing for the computer's own account. The agent signs in to the share without a drive letter. |
| S3-compatible storage | endpoint (empty for Amazon S3), region, bucket, optional folder, access key and secret key; "bucket in the path" for MinIO. Works with Wasabi, Backblaze B2, MinIO, Synology C2, Cloudflare R2. |
| Azure Blob | a container SAS address allowing read, add, create, write, delete and list, and an optional folder. |

How a backup is made:

- Files are cut into pieces of about 1 MB where the content says, so a change in a large file stores only the changed pieces; a file whose size and change time are the same as last time is not read at all.
- Every piece and every list of files (a snapshot) is compressed and encrypted with AES-256-GCM. Piece names are keyed hashes, so the storage learns nothing about the content.
- On Windows, open files are read from a shadow copy of their drive (can be switched off); a file that still cannot be read is skipped and the run ends with a warning naming it.
- Each plan has its own folder in the storage (`deskask-plan-<id>`) and its own password, made by DeskAsk and sealed on the server. An administrator can show it on the plan (recorded in the audit log), so backups can be opened without this server.
- After each run, backups outside the retention are removed, then pieces no remaining backup needs.
- Results appear as the job *DeskAsk file backup* with the usual statuses and alerts.

**Back up now** (engineers and administrators) asks the agent at once. **Restore** on a backup puts chosen files or folders (or everything) into a folder on the device, keeping the original folders under it (`C:\DeskAsk-Restore\C\Users\...`), or back where they were when that is ticked. The agent reports the result under Restores.

The agent keeps its plans in `backup-plans.json` beside `agent.json` (same protection), so scheduled backups also run while the server cannot be reached.
