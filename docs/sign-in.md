# Sign-in options

## Microsoft Entra ID (Microsoft 365 work accounts), v3.6.0
Users › **Microsoft sign-in…**

1. In the Microsoft Entra admin centre: App registrations › New registration. Choose "Accounts in this organizational directory only".
2. Add a **Web** redirect address: the one shown in the DeskAsk dialog, `https://<your DeskAsk address>/account/sso/microsoft/callback`.
3. Certificates & secrets › New client secret. Copy the **value**.
4. For importing users only: API permissions › Add › Microsoft Graph › **Application permissions** › `User.Read.All`, then "Grant admin consent".
5. In DeskAsk enter the directory (tenant) id, the application (client) id and the secret; tick "Show Sign in with Microsoft"; **Save and test**.

How it behaves:
- The sign-in page gets a "Sign in with Microsoft" button. Password sign-in keeps working.
- A person is matched to a DeskAsk user by their Microsoft object id, then by email. A disabled DeskAsk user cannot sign in.
- Optional: create an account at first sign-in for people who have none (only for the listed email domains, never as administrator; the role is chosen in the dialog).
- Microsoft's own sign-in, including its multi-factor step, stands in for the DeskAsk two-step code.
- **Import from Microsoft** lists the directory (up to 3,000 people), marks those who already have an account, and creates accounts with the chosen role. Existing users with the same email are linked, not changed. Customer roles need a company: use Edit on the user afterwards.
- The server must be able to reach `login.microsoftonline.com` and `graph.microsoft.com` over HTTPS.

Google Authenticator and other authenticator apps already work as the second step (each person: account menu › Security).

## Active Directory (LDAP), v3.7.0
Users › **Active Directory…**

- **Server:** a domain controller the DeskAsk server can reach (`dc01.acme.local`, or `dc01.acme.local:3269`). Use LDAPS (port 636). If the controller's certificate is not trusted by the DeskAsk server, install its CA there, or tick "Accept the controller's certificate" (less safe). Without LDAPS the connection uses Windows negotiated (signed and sealed) binding, which works on a Windows DeskAsk server only.
- **Base DN:** where to look, for example `DC=acme,DC=local`; an extra LDAP filter can narrow it, for example members of one group.
- **Account:** an ordinary read-only account (`svc-deskask@acme.local`). DeskAsk reads users with it and uses it to find a person before checking their password.
- **Import from Active Directory** lists people (up to 3,000; disabled accounts are marked), and creates accounts with the chosen role. They are recognised by their AD object GUID.
- **Sign-in:** people imported from AD sign in on the normal page with their email and **AD password**; DeskAsk checks it against the controller each time and never stores it. The DeskAsk two-step code still applies if they turned it on. Disabled or locked AD accounts are refused by the controller. Switching "sign in with AD password" off stops it at once.

Planned: Google Workspace.


## Google Workspace

Users › **Google sign-in…** (administrators).

1. In Google Cloud console › APIs & Services › Credentials, create an **OAuth client** of type *Web application* and add the redirect address shown in DeskAsk (`https://<your DeskAsk>/account/sso/google/callback`). Make the OAuth consent screen *Internal* so only your Workspace can use it.
2. Enter the client id and secret, and the **Workspace domains** allowed to sign in (for example `acme.com`). With domains set, only accounts managed by them come in: a personal Gmail account is refused even if its address matches a DeskAsk user. With one domain, Google's account chooser is limited to it.
3. Optionally let people of those domains get an account at their first sign-in, with a role (never administrator).
4. Tick *Show "Sign in with Google"*.

People are matched by their Google account (stored as `google:<id>`) or their email. Google's sign-in, including its 2-step verification, replaces DeskAsk's two-step code for them. An account already linked to Microsoft or Active Directory keeps that link and can also sign in with Google by its email.

### Importing from the Workspace directory

1. In the same Google Cloud project, turn on the **Admin SDK API**, create a **service account** and download a JSON key.
2. In the Google Admin console › Security › API controls › **Domain-wide delegation**, add the service account's client id with the scope `https://www.googleapis.com/auth/admin.directory.user.readonly`.
3. In DeskAsk choose the key file and enter the Workspace administrator the service account acts as, then **Save and test import**.
4. Users › **Import from Google**: choose people and a role. People who already have an account (by email) are linked instead.

The client secret and the service account key are kept encrypted; the key is never shown again (only the service account's email).
