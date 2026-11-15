# Vault (v3.2.0)

The Vault keeps the passwords, keys and notes of your customers and your own company. It follows the Bitwarden model: everything is encrypted in the browser before it is saved, and the server stores only ciphertext. On top of that it adds what an MSP needs and Bitwarden does not have: vaults per customer, access requests with a reason and a time limit, links to assets, and passwords that DeskAsk changes on a schedule through the agent.

## How the encryption works

| Part | How |
|---|---|
| Master password | Each person picks one when they first open the Vault. It is not their sign-in password and is never sent to the server. PBKDF2-SHA256 with 600 000 rounds turns it into the key that opens their private key. |
| Personal key pair | RSA-OAEP 3072, made in the browser. The public key is stored as it is. The private key is stored twice: encrypted with the master password, and encrypted with the recovery code. |
| Recovery code | 32 characters (160 random bits), shown once at set-up. It is the only way back after a forgotten master password. A new one can be made under **My key**. |
| Vault keys | Each vault has two AES-256-GCM keys. The **item key** encrypts contents and folder names. The **directory key** encrypts item names, so people who may only ask for access can see what exists. Each member gets the keys wrapped with their own public key. |
| Approved requests | The approver's browser encrypts the item again for the requester's public key (a fresh AES key, wrapped with RSA). The copy is deleted when the time is up. |
| Send links | Encrypted with a random key that exists only in the link after the `#`. Browsers do not send that part to any server. An optional passcode is mixed into the key. |

The vault locks itself after 15 idle minutes (Settings), when the tab closes, or with **Lock**. Locking only forgets the keys in that browser.

**What the server can see:** who is a member of which vault, when items were created and changed, the kind of each item (login, server account and so on), which asset an item is linked to, and the log of who opened or copied what. It cannot see item names, user names, passwords, notes or folder names.

**The one exception is managed accounts** (below). When DeskAsk changes a password for you, the server must know it. Those passwords are sealed with the server's data protection keys. Every time someone reads one, it is written to the audit log.

## Vaults and roles

| Kind | For | Who creates it |
|---|---|---|
| Customer | One per customer: servers, network devices, cloud admin accounts, Wi-Fi | Administrators (or engineers too, in Settings) |
| Internal | Your own company's accounts | The same |
| Personal | Each person's own vault. Nobody else can be added to it | Made automatically at set-up |

Members are people or **security groups** from Access. Groups nested inside groups count as well. A person who is in a vault directly and through a group gets the higher of the two roles.

| Role | Sees names | Opens items | Changes items, answers requests | Changes members |
|---|---|---|---|---|
| May ask for access | ✓ | after an approved request | | |
| Can view | ✓ | ✓ (not items marked "viewers must ask") | | |
| Can edit | ✓ | ✓ | ✓ | |
| Manages | ✓ | ✓ | ✓ | ✓ |

**Giving out keys.** The server cannot hand anyone a vault key, because it does not have one. When someone is added (or sets up their vault later), they wait until a manager of that vault next opens the Vault. The manager's browser then gives them the key without the manager doing anything. The Members window shows who has the key and who is still waiting.

**Removing people.** A person who is removed loses their wrapped keys at once. If they ever held the item key, they could have kept a copy, so the vault is marked **needs new keys**. A manager clicks **Change the vault's keys**. Their browser then re-encrypts every item, earlier version and folder with new keys and gives the new keys to everyone who remains.

## Items

The kinds of item are: login, server account, network device, database, Wi-Fi, SSH key, API key, payment card, identity and secure note. Each kind has its own fields, and any item can have extra fields, shown or hidden.

- **Show and Copy.** Each action is written to the item's log under **Who used it**. The clipboard is cleared after 30 seconds if it still holds the secret.
- **One-time codes.** Store a TOTP secret or an `otpauth://` address in a login, and the current six-digit code is shown with its countdown.
- **Earlier versions.** Each change keeps the previous version, up to 20.
- **Trash.** Deleted items stay in the trash for 30 days.
- **Linked to an asset.** An item can be linked to an asset of the same customer. The asset's page then shows **Passwords in the Vault (n)**.
- **Folders** can be made in each vault.
- **Move to another vault.** The browser encrypts the item again with the other vault's key.
- **Viewers must ask.** Mark an item this way, and people with *Can view* must ask before opening it. Editors and managers open it directly.

## Access requests

A person who may not open an item sees **Ask for access**, with a reason, an optional ticket number and a time (15 minutes to the maximum set in Settings). The vault's editors and managers get an email (template `vault_request`) and see the request under **Requests**. One of them approves, and may choose a shorter time and add a note, or denies. The requester gets an email (`vault_decision`), and the item appears under **Requests › Open now** until the time ends or they click **I am done**. Requests that nobody answers end after a day. Nobody can approve their own request.

## Managed accounts (password rotation)

On a server account (or a login, database or network item) that is linked to an asset with a DeskAsk agent, choose **Let DeskAsk change this password**.

| Account | Agent | How it is changed |
|---|---|---|
| Windows local account | Agent on that Windows computer | `NetUserSetInfo` (level 1003) |
| Active Directory account | Agent on a **domain controller** | The same call on the domain controller, which changes the domain account |
| Linux account | Agent on that Linux computer | `chpasswd`, with the password on standard input |

Options: change every *n* days, change it after each use (an hour after someone reads it, or when an approved request ends), and change it now.

**How a change is made safely:**
1. DeskAsk makes a new 24-character password (length in Settings) and keeps it as *pending*.
2. The agent is told there is a change and fetches the password once. The server wipes its copy as it hands it over.
3. The agent sets the password and reports the result.
4. Only on success does the pending password become the current one. The old one goes into the account's history.
5. If the agent cannot do it, the old password stays and the vault's managers get an email (`vault_rotation_failed`). DeskAsk tries again an hour later.
6. If the agent took the password but never answered, the account shows **Unsure**. **Show password** then gives both the current and the possible password, and DeskAsk tries again in 5 minutes. Setting a password does not need the old one, so the next success settles it.

**Stop managing** hands the password back once. The browser puts it into the encrypted item, and the server forgets it.

**Managed accounts** lists every account with its status, the last change and the next one.

## Send links

**Send links** makes a link for someone outside DeskAsk: a customer's new starter, a contractor or a supplier. You choose how long it works (1 hour to 30 days) and how many times it opens (once to 10 times), and you can add a passcode to tell them separately. A wrong passcode does not use up a view. Ten wrong tries end the link. The page needs no sign-in. Opening a link is written to the audit log.

## Password health

Worked out in the browser, over every item you can open:
- weak passwords
- passwords used more than once
- passwords not changed for more than a year
- with **Check against known breaches**, passwords found in Have I Been Pwned

The breach check sends only the first five characters of each password's SHA-1 hash (k-anonymity), through the server. It can be switched off in Settings.

## Import and export

**Tools › Import** reads a Bitwarden export (JSON or CSV, not the encrypted kind), or a CSV export of Chrome, Edge, KeePass or LastPass. The file is read and encrypted in the browser. Folders are created as needed.

**Export** saves everything you can open, either encrypted with a password you choose, or as plain CSV (with a warning). Each exported item is noted in its log.

## Forgotten master password

1. On the lock screen, choose **Forgot the master password?**, type the recovery code and choose a new master password.
2. If the recovery code is lost too, **I lost the recovery code too** starts again with new keys. The personal vault is emptied, because nobody can open it any more. Shared vaults come back once a manager of each vault opens the Vault.

An administrator cannot reset anyone's master password. That is what zero knowledge means.

## Settings (administrators)

| Setting | Default |
|---|---|
| Who may create vaults | Administrators |
| Lock after idle minutes | 15 |
| Longest approved request | 480 minutes |
| Length of passwords DeskAsk sets | 24 |
| Change managed passwords every | 30 days (for new managed accounts) |
| Breach check | On |

## Upgrading

Install v3.2.0 like any update. The Vault needs the **vault** module in the licence. For password changes, update the agents to 3.2.0, because older agents ignore the request and the change fails after a day with "The agent did not come online within a day."
