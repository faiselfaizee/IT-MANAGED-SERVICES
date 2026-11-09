# Creating and looking after customers

Run these from the server folder (the one with `DeskAsk.Platform.Host`). On Windows use `DeskAsk.Platform.Host.exe tenant ...`; on Linux `./DeskAsk.Platform.Host tenant ...` (or `dotnet DeskAsk.Platform.Host.dll tenant ...`).

## Settings it needs
| Setting | Meaning |
|---|---|
| `Platform__CatalogConnection` | Connection to the platform catalog database (the list of customers). Required. |
| `Platform__ProvisionConnection` | A login that may create databases. Default: the catalog login. |
| `Platform__TenantConnectionTemplate` | Optional. Connection string for new customers with `{db}` where the database name goes. Default: the catalog connection with the database name replaced. |

## Commands
```
tenant init                  # makes the catalog tables (create and list do this for you)
tenant create --id acme --host acme.deskask.in --admin-email admin@acme.com --name "Acme Ltd"
tenant list
tenant disable --id acme     # the address stops answering; data is kept
tenant enable  --id acme
tenant migrate               # brings every active customer up to this version (run after each update)
```
`create` makes the database, the catalog entry, all module tables, and the first administrator. The password is generated and shown once, or taken from an environment variable (`--admin-password-env NAME`). If a create stops half way, run the same command again: it carries on. Nothing is switched on until every step has worked.

## Updates and migrations
Each module keeps its database changes as numbered steps (`meta.migrations` in every customer database). A step is applied once, inside a transaction, under one lock for the database, so two servers starting together are safe. A step that was edited after it was applied is refused: add a new version instead. The web server also applies pending steps for a customer on that customer's first request, so `tenant migrate` is a way to do it ahead of time and to see failures before customers do.
