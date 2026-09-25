# Deploying Orbit (Ubuntu Linux)

## 1. Database

```bash
sudo -u postgres psql -f create-db-role.sql     # edit the password first
```

## 2. Application files

```bash
dotnet publish Orbit.Web -c Release -o /opt/orbit/app      # from the solution root; the web app is the Orbit.Web project
sudo useradd --system --no-create-home --shell /usr/sbin/nologin orbit
sudo chown -R orbit:orbit /opt/orbit
sudo mkdir -p /var/lib/orbit/keys && sudo chown orbit:orbit /var/lib/orbit/keys && sudo chmod 700 /var/lib/orbit/keys
```

> **Upgrading a server that has `/var/lib/orbit/attachments`:** attachments are now stored in the database. Files already in that
> directory are not imported - their rows stay listed but download as missing until they are deleted and uploaded again. Once
> that is done, remove the directory and the `Attachments__Path` line from `/etc/orbit/orbit.env`.

> **Upgrading a server installed before the project was renamed `Orbit` -> `Orbit.Web`:** the entry point is now
> `Orbit.Web.dll`, not `Orbit.dll`. Do both of these, or the service will quietly keep running the **old** build:
> 1. Empty `/opt/orbit/app` before copying the new publish output in. Publishing over the top leaves the old
>    `Orbit.dll` sitting there, and the old unit file would go on starting it.
> 2. Change `ExecStart` in `/etc/systemd/system/orbit.service` to `/opt/orbit/app/Orbit.Web.dll` (as in the
>    `orbit.service` here), then `sudo systemctl daemon-reload && sudo systemctl restart orbit`.
>
> Nothing else moves: the database, the env file and the Data Protection key ring are untouched, so nobody is signed out.

## 3. Configuration

```bash
sudo mkdir -p /etc/orbit
sudo cp orbit.env.example /etc/orbit/orbit.env
sudo chown root:root /etc/orbit/orbit.env && sudo chmod 600 /etc/orbit/orbit.env
sudo nano /etc/orbit/orbit.env                  # connection string, base URL, seed admin
```

Nested configuration keys use `__` (double underscore) in environment variables, e.g.
`ConnectionStrings__DefaultConnection`. Anything set in the env file overrides `appsettings.json`.

## 4. Service

```bash
sudo cp orbit.service /etc/systemd/system/orbit.service
sudo systemctl daemon-reload
sudo systemctl enable --now orbit
journalctl -u orbit -f
```

Put nginx or Caddy in front for TLS and forward to `http://127.0.0.1:5000`.
Set `App__BaseUrl` to the public HTTPS address so notification links are correct.

Orbit honours `X-Forwarded-For` / `X-Forwarded-Proto` from a proxy on the same host (loopback only), so the request
scheme and client addresses it sees are the real ones. **`X-Forwarded-For` is a security setting, not a nicety:** the
sign-in throttle (section 8) counts failed sign-ins per client address. Without the header every user looks like
`127.0.0.1`; Orbit then switches the throttle off and logs a warning, rather than let twenty bad guesses from anyone
lock the whole company out. With nginx, send both headers - and allow WebSockets on the Orbit Agent hub, which holds a
long-lived connection (section 7):

```nginx
location / {
    proxy_pass         http://127.0.0.1:5000;
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
}
location /agent/hub {
    proxy_pass         http://127.0.0.1:5000;
    proxy_http_version 1.1;
    proxy_set_header   Upgrade $http_upgrade;
    proxy_set_header   Connection "upgrade";
    proxy_set_header   Host $host;
    proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header   X-Forwarded-Proto $scheme;
    proxy_read_timeout 1h;      # the agent's connection is idle between sign-ins; keep-alives flow every 15s
    proxy_buffering    off;
    client_max_body_size 8m;    # a directory listing for Import from directory, if the agent falls back to long polling
}
```

Caddy needs nothing extra. If WebSockets are blocked somewhere along the way the agent still works: SignalR falls
back to Server-Sent Events or long polling by itself.

**Admin > Users > Import from directory** waits for the agent to list the directory - up to
`Agents__DirectoryListTimeoutSeconds` (45 s by default). Keep that below the proxy's read timeout for `location /`
(nginx: 60 s by default). The listing comes back to Orbit as one message of up to a few megabytes: over WebSockets that
needs nothing, but with long polling it is a request body, hence `client_max_body_size` above.

## 5. First run

Migrations are applied at startup (`Database__ApplyMigrations=true`). The `Seed__Admin__*` values are
consumed only when nobody holds the built-in **System Administrator** role yet. Sign in with the seeded account, change the
password under *Manage account*, then remove the three `Seed__Admin__*` lines and restart the service.

> **Upgrading a server installed before roles became data (spec §6.5):** the `AddDynamicRoles` migration runs at
> startup and converts the fixed roles in place - `SystemAdmin` becomes the built-in *System Administrator*,
> `DepartmentAdmin` becomes *Department Admin*, *Member* stays - giving the two migrated roles the grants that match
> their old rights and pointing every API key at its role. Nobody's access changes and no key has to be reissued.
> Afterwards roles are edited under *Admin > Roles*.

## 6. Claude / MCP

Create an API key under *Admin > API Keys* and configure the connector with:

- Endpoint: `https://orbit.example.com/mcp` (Streamable HTTP)
- Header: `Authorization: Bearer <key>`

The key acts with the role and department it was issued with - the same rules as a human user of that role: each of the
role's permissions applies at its scope (*Admin > Roles*). A key whose role has any permission scoped to a department must
be issued with a department; a key can be given no department only if its role is scoped company-wide.

## 7. Directory sign-in (LDAP / Active Directory) and the Orbit Agent

Orbit is on the internet; the directory is behind the corporate firewall. Rather than open a port to it, a small
**Orbit Agent** runs inside the network and connects *out* to Orbit over HTTPS. Orbit sends sign-in checks down that
connection. The firewall needs no inbound rule; the agent's machine needs outbound HTTPS to Orbit and LDAP(S) to the
directory server.

The agent has **no settings of its own** apart from where Orbit is and the credential Orbit issued it. Directory
servers, the service account, search base and filter are all set in Orbit under *Admin > Directory* and travel with
each request - change them in Orbit and the next sign-in uses them.

### Publish the agent

It is a separate project (`Orbit.Agent`) and is *not* part of the web app's publish output. Self-contained, so
the target machine needs no .NET installed. From the solution root:

```bash
# Windows (typical next to a domain controller)
dotnet publish Orbit.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/agent-win
# Linux
dotnet publish Orbit.Agent -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/agent-linux
```

Copy the output folder to the machine, e.g. `C:\OrbitAgent` or `/opt/orbit-agent`.

### Register it (GitHub-runner style)

1. In Orbit: *Admin > Agents > New agent*, give it a name. Orbit shows a one-time command.
2. On the agent machine, in the agent's folder, run it:

   ```
   Orbit.Agent configure --url https://orbit.example.com --token orbitreg_...
   ```

   The token works once and expires after an hour (*New token* on the Agents page issues another). `configure` swaps
   it for the agent's own credential and writes `agent.json` beside the executable - encrypted with DPAPI (machine
   scope) on Windows, mode `600` on Linux. Orbit stores only a hash of that credential.
3. Start it with `Orbit.Agent run`, or as a service so it survives reboots:

   ```powershell
   # Windows (elevated). Run the service as an account that can read C:\OrbitAgent.
   sc.exe create OrbitAgent binPath= "C:\OrbitAgent\Orbit.Agent.exe run" start= auto DisplayName= "Orbit Agent"
   sc.exe failure OrbitAgent reset= 86400 actions= restart/10000
   sc.exe start OrbitAgent
   ```

   ```bash
   # Linux
   sudo useradd --system --no-create-home --shell /usr/sbin/nologin orbit-agent
   sudo chown -R orbit-agent:orbit-agent /opt/orbit-agent
   sudo -u orbit-agent /opt/orbit-agent/Orbit.Agent configure --url https://orbit.example.com --token orbitreg_...
   sudo cp orbit-agent.service /etc/systemd/system/ && sudo systemctl daemon-reload
   sudo systemctl enable --now orbit-agent && journalctl -u orbit-agent -f
   ```

   On Windows, run `configure` *before* creating the service (or as any administrator): the machine-scope encryption
   lets the service account read what another account wrote. On Linux run it **as the service user**, as above, since
   only the file's owner can read it.
4. The agent shows as **Online** under *Admin > Agents* within a few seconds. Logs go to the console, or as a service
   to the Windows Event Log (Application log, source *Orbit.Agent*, warnings and errors) or the systemd journal.

Behind a corporate proxy the agent uses the machine's proxy settings (`HTTPS_PROXY`, or the Windows system proxy)
automatically. Run a second agent on another machine for redundancy: Orbit tries each connected agent in turn.

To retire an agent: `Orbit.Agent remove` on its machine (de-registers and deletes `agent.json`), or *Revoke* it in
Orbit, which disconnects it immediately and invalidates its credential. A lost or copied `agent.json` should be
treated like a leaked password - whoever holds it can pose as the agent and would be sent users' directory passwords
to check - so revoke and re-register.

### Turn directory sign-in on

1. *Admin > Directory*: server (the name on its certificate, as reachable **from the agent**), port `636` with SSL, a
   **read-only, least-privilege** service account, search base and user filter. Use *Test connection* - it runs
   through the agent against the values in the form, saved or not - then tick *Enable* and save.
2. *Admin > Users*: set **Sign-in method** to *Directory (LDAP)* per user. Accounts are still created in Orbit first;
   a directory account alone grants nothing. The user's Orbit email must match exactly one directory entry through
   the filter (default `(mail={0})`).
3. Keep at least one System Admin on a **local password**. Orbit enforces this: it is the way in when the directory
   or the agent is down.

The bind password is encrypted with the Data Protection key ring below. If the key ring is lost it can't be decrypted
and must be re-entered under *Admin > Directory*; nothing else is affected.

## 8. Sign-in hardening - set this against your AD policy

Orbit is on the internet and, for directory users, is a door onto Active Directory. Three protections ship switched on;
the first one needs you to look at your AD policy once.

**Lockout (`Security__Lockout__*`, default 3 attempts / 30 minutes).** Every wrong password typed into Orbit for a
directory user is a real failed bind in AD, and counts towards AD's *own* lockout. If Orbit tolerated as many
attempts as AD does, anyone who knew a colleague's email could lock that person's **Windows account** from the
internet, over and over. A locked Orbit account is refused without AD being contacted, so AD sees at most
`MaxFailedAttempts` bad binds per `LockoutMinutes`. Look up *Account lockout threshold* and *Reset account lockout
counter after* in your domain's password policy (`net accounts /domain`), then keep:

- `MaxFailedAttempts` **below** the threshold, with room for the person's own mistakes elsewhere (threshold 5 -> 3);
- `LockoutMinutes` **at least** the reset interval.

The same lockout applies to local accounts. Because it is deliberately long, a System Admin can end one early:
*Admin > Users* shows a **Locked out** badge and the user's page has an **Unlock** button. For a directory user that
clears Orbit's lock only - if AD has locked the account as well, unlock it in AD.

**Failed-sign-in throttle (`Security__LoginThrottle__*`, default 20 failures / 15 minutes per address).** Lockout is
per account, so it never notices one common password being tried against many accounts. This counts failures by client
address instead - failures only, so an office sharing one public address isn't penalised for signing in. Once over
the limit that address gets "Too many failed sign-in attempts" (HTTP 429) and nothing is sent to AD. It is a speed
bump, not a wall; it depends on `X-Forwarded-For` (section 4); IPv6 is counted per /64.

**Secure cookies.** Outside development every cookie is marked `Secure`, whatever scheme Orbit believes the request
had. Orbit must therefore be reached over HTTPS - which the Orbit Agent requires anyway.

What is deliberately *not* enforced: a second factor. A directory sign-in is password-only - it does not pass through
any MFA or conditional access you have on AD/Microsoft 365. Users can turn on Orbit's own authenticator-app 2FA under
*Manage account*; making it mandatory is a possible follow-up (spec §13, item 29).

## Backups

Back up the `orbit` database and the Data Protection key ring directory (`/var/lib/orbit/keys`). The files people attach to
tasks and projects (spec §6.18) are stored in the database, so a database backup carries them. Losing the key ring signs every user out and invalidates outstanding password-reset links; it does not affect stored data.
