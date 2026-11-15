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

Planned: Active Directory (LDAP) import and sign-in, then Google Workspace.
