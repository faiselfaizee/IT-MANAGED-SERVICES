# Assets module (`assets`)

First product module on the platform. Licence module key `assets`, limit `max_assets` (active assets, i.e. not retired or lost). Everyone signed in can look; administrators change. Needs the module in the customer's licence (402 `module_not_licensed` otherwise).

## What it holds
- **Asset types** (Laptop, Monitor, Phone ...), each with its own extra fields: `text`, `number`, `date`, `bool`, `choice` (with options). Up to 30 per type.
- **Assets**: tag (`AST-000001`, or your own), name, type, status (`stock`, `in_use`, `repair`, `retired`, `lost`), manufacturer, model, serial (unique), location, assigned to, purchase date and cost, warranty end, notes, the type's extra fields, source (`manual`, `import`; `agent` later).
- **History** per asset: who changed what, from and to. Also written to the platform audit log.

## API
| | |
|---|---|
| `GET /api/assets/types`, `POST`, `PUT /{id}`, `DELETE /{id}` | types (delete refused while assets use it) |
| `GET /api/assets?search=&status=&typeId=&assignedTo=&take=&skip=` | list, newest first, `{total, items}` |
| `GET /api/assets/{id}` | asset and history |
| `POST /api/assets`, `PUT /api/assets/{id}`, `DELETE /api/assets/{id}` | create, change (only the fields you send), delete |
| `GET /api/assets/summary` | counts by status and type, warranty ending in 30 / 90 days, expired |
| `GET /api/assets/export.csv` | all assets (cells that start with `= + - @` get a leading `'` so a spreadsheet cannot run them) |
| `POST /api/assets/import[?createTypes=true]` | CSV body, header line required with a `name` column; columns: tag, name, type, status, manufacturer, model, serial, location, assigned_to, purchase_date, purchase_cost, warranty_end, notes. Answers `{created, skipped, errors:[{line,error}]}` |

## Not in this first version (next)
Agent discovery (hardware and software inventory from the Go agent), QR labels, check-out/check-in with signatures, maintenance and warranty reminders, attachments, custom roles (technician vs administrator), screens in the web app. These come from the old DeskAsk Assets app; the field list above is a starting point and should be compared with it.
