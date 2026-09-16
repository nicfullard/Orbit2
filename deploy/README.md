# Deploying Orbit (Ubuntu Linux)

## 1. Database

```bash
sudo -u postgres psql -f create-db-role.sql     # edit the password first
```

## 2. Application files

```bash
dotnet publish -c Release -o /opt/orbit/app
sudo useradd --system --no-create-home --shell /usr/sbin/nologin orbit
sudo chown -R orbit:orbit /opt/orbit
sudo mkdir -p /var/lib/orbit/keys && sudo chown orbit:orbit /var/lib/orbit/keys && sudo chmod 700 /var/lib/orbit/keys
```

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

## 5. First run

Migrations are applied at startup (`Database__ApplyMigrations=true`). The `Seed__Admin__*` values are
consumed only when no System Admin account exists yet. Sign in with the seeded account, change the
password under *Manage account*, then remove the three `Seed__Admin__*` lines and restart the service.

## 6. Claude / MCP

Create an API key under *Admin > API Keys* and configure the connector with:

- Endpoint: `https://orbit.example.com/mcp` (Streamable HTTP)
- Header: `Authorization: Bearer <key>`

The key acts with the role and department it was issued with - the same rules as a human user of that role.

## Backups

Back up the `orbit` database **and** the Data Protection key ring directory (`/var/lib/orbit/keys`).
Losing the key ring signs every user out and invalidates outstanding password-reset links; it does not affect stored data.
