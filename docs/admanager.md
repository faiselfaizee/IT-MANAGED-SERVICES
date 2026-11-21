# AD Manager

Look up and manage a customer's Active Directory from DeskAsk. The platform never connects to a domain controller itself: every request travels through the customer's **site controller** (the DeskAsk agent in collector mode), which talks LDAP to the domain controllers on the customer's network. This works the same for the hosted and the on-premises edition, and no inbound firewall rule is needed at the customer.

AD Manager needs the **Assets** module (that is where controllers are enrolled) and the `adsuite` licence module.

## Set up (administrator)
1. Enrol a site controller for the customer (Assets → Collectors) and leave it running on a server that can reach the domain controllers.
2. AD Manager → Domains → *Add a domain*: company, name, base DN, domain controllers (tried in order), LDAPS on/off, service account and its password, and the controller.
3. Press **Test**. The result names the controller that answered, or says why not (refused password, unreachable, wrong base DN) with a hint.
4. *Who may*: give people or groups rights on the whole domain or on one OU. Rights: look, unlock, reset passwords, enable/disable, change details, group members, create, move, delete, approve. *Look* is always included. Administrators may do everything. Customer-role users never get rights.

Setting a password needs LDAPS (Active Directory refuses it otherwise). The service account password is stored sealed and is never shown again.

## What people can do
- **Browse**: folders (OUs), search people, groups, computers and OUs, open an object and see its details, groups and members.
- **Actions**: unlock, reset password (a password the person types is never stored or logged; a generated one is shown once), enable/disable, set account expiry, edit details, move, create people/groups/OUs, add/remove members, delete.
- **Reports**: inactive users, never signed in, password never expires, privileged users, disabled, locked and expired users, empty groups, stale and disabled computers. Each can be downloaded as CSV (exports are audited).
- **Account requests**: anyone with access to an OU can ask for a new account (employee, external with an end date, or service account with its purpose). Someone else who may approve creates it; nobody but an administrator approves their own request.

## Safety
- Only rights granted for the OU apply; a request outside it is refused.
- **Delete**, resetting or disabling an administrator account (adminCount=1) and changing members of privileged groups (Domain Admins, Enterprise Admins, …) wait for a second person with the *approve* right (Approvals).
- Built-in objects (RID below 1000), the `krbtgt` account and DeskAsk's own service account cannot be deleted or moved; the last two cannot be disabled or have their password reset.
- Every change and every export is written to the audit log. The controller stops at the first failing step of a job, and a refused sign-in is never retried, so a wrong password cannot lock the service account.
- A job waiting for the controller is sealed in the database, wiped the moment the controller takes it, and failed after two minutes if no controller picks it up.

## v4.2.0: easier to use (ManageEngine ADManager as the reference)
- **Layout:** the module has its own sidebar: Overview (rings for accounts, sign-in activity and computers), Management (Users, Groups, Computers, OUs), Create (new user, new group or OU, users from a file, change from a file), Reports, Workflow (account requests, approvals) and Settings (automation, user templates, domains and delegation). Lists open a detail pane beside them.
- **Pickers:** OUs are chosen from a tree that opens one level at a time; groups, managers and members are found by typing two letters. No distinguished names to type.
- **New user:** every field in tabs as in Active Directory Users and Computers (account, member of, general, organisation, phones, address, profile, custom attributes); UPN suffixes from the domain; the domain's password rules shown; display and sign-in names built from the name by the template's rules; generated or typed password; must change, never expires, leave disabled, expiry date.
- **User templates** (administrators): OU, groups, default fields, naming rules (`{first}`, `{last}`, `{initials}`, `{f}`, `{l}`), UPN suffix, account options, expiry after n days.
- **Bulk:** select several objects in a list to enable, disable, unlock, reset passwords, move, add to groups or delete (one at a time with a progress window and a result per object). Users from a CSV file (with a template, checked against the directory before anything is created, results with generated passwords downloadable once). Change users from a CSV file by sign-in name (`-` clears a value).
- **Offboarding** in one step: disable, replace the password, remove from every group, clear the manager, note in the description, move to an OU. A privileged account waits for approval.
- **Password flags:** password never expires, must change at next sign-in.
- **Reports:** 17, adding passwords expiring soon, expired passwords, recently created, recently changed, users without a manager, all enabled users and all computers.
- **Automation** (administrators): daily policies to disable inactive users, disable stale computers, or move disabled users, with a preview first and Run now. Built-in, privileged and DeskAsk's own accounts are never touched; at most 200 objects per run; each run is audited and its result shown.

## LDAPS (v4.1.1)
Test names the reason when LDAPS fails: no certificate on the domain controller, the certificate is for another name, issued by a CA the controller does not trust, expired, changed since you trusted it, or an old TLS version. For an untrusted or mismatched certificate it shows the certificate (names, issuer, expiry, SHA-256) and offers **Trust this certificate**: the controller then accepts exactly that certificate. Without LDAPS, a site controller on Windows sets passwords through ADSI with Kerberos/NTLM signing and sealing (give the service account as user@domain or DOMAIN\user).

## Directory step "verify" (agent 6.17.0)
The site controller checks a person's own password with a second connection bound as that person. It is used only for company employees signing in to DeskAsk with their Windows password. The answer is `valid` and `reason` (`wrong`, `disabled`, `locked`, `expired`, `must_change`, `not_allowed`). The service account's connection is not used for it.
