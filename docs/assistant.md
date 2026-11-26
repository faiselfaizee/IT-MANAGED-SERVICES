# Ask DeskAsk (6.23.0)

Ask in plain English from the speech-bubble button at the top right (next to the gear). It answers from your own inventory, tickets and alerts, shows a table whose first column opens the item, and offers **Download CSV** and **Print or save as PDF**. It is a set of rules, not an outside AI service: nothing you type leaves your DeskAsk, and it only sees the customers you may see.

## What it understands
| Ask | Answer |
|---|---|
| How many servers / laptops / printers do we have? Show Windows computers. | Count and list by asset type, system or status |
| Which assets have a warranty ending in 60 days? Assets out of warranty | Warranty list |
| Which computers are low on disk space? Less than 10% free | Partitions below the limit (15% if you give none) |
| Which devices are down? | Monitored devices whose last check failed |
| Show open critical alerts | Open alerts, most serious first |
| Show overdue tickets / tickets due soon / tickets with no engineer / my tickets / open critical tickets | Ticket lists |
| Which computers have Chrome installed? | Computers whose program list has it |
| Computers not reporting for 7 days | Agents that have gone quiet |
| Which computers are not protected by antivirus? | Antivirus not reported as protected |
| What is affected if SW-CORE-01 fails? | Everything that depends on it (Relationships), nearest first |
| What is assigned to Ravi? Find 10.0.0.5 / a serial / a ticket number | Assets by person, or a search by name, tag, serial, address |

Up to 100 rows are shown per answer; ask a narrower question for the rest. If a question is not understood, it says so and offers examples.

## Access
Anyone who can read Assets can ask. Answers are limited to the customers the person may see, exactly like the Assets, Service Desk and Monitoring pages.
