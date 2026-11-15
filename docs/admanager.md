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
