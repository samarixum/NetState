# NetState

NetState is a lightweight domain and endpoint monitoring system built with .NET 10. The solution contains:

- A minimal ASP.NET Core server that stores monitored domains in SQLite, exposes a JSON API, and runs background checks every 5 minutes.
- A desktop client built with Avalonia UI for managing monitored domains, triggering checks, and inspecting captured responses.
- A shared library containing models and diagnostics utilities used by both applications.

## What It Monitors

Each monitored domain can validate one of the following expectations:

- `HttpStatus`: verifies the returned HTTP status code matches the expected value, such as `200`.
- `Redirect`: verifies the endpoint returns a redirect and optionally checks the redirect target.
- `HtmlHash`: normalizes the returned HTML and compares its SHA-256 hash to an expected hash.

In addition to the main expectation, NetState can validate expected response headers. Every check also captures the last response body and response headers so they can be inspected from the desktop client.

## Solution Layout

```text
NetState/
|- NetState.Server/   ASP.NET Core monitoring API + background worker + SQLite
|- NetState.Client/   Avalonia desktop UI for operators
|- NetState.Shared/   Shared models and diagnostics helpers
|- NetState.slnx      Solution entry point
```

## Key Features

- Create, update, delete, and manually check monitored domains.
- Automatic monitoring loop that re-checks all configured domains every 5 minutes.
- Response header assertions per monitored domain.
- HTML snapshot capture for later inspection.
- Desktop UI status dashboard with refresh, edit, inspect, and manual check actions.
- Host discovery workflow in the client using `crt.sh` to expand a base domain into candidate subdomains.
- Basic alerting via a MailChannels-compatible HTTP endpoint when a domain transitions to `Down`, and again when it recovers.
- Session-based diagnostics logs for both client and server.

## Requirements

- .NET 10 SDK
- Windows is the current primary development environment, but the server and shared projects are standard .NET projects and the Avalonia client is cross-platform in principle.

## Getting Started

### 1. Restore and build

From the repository root:

```powershell
dotnet build NetState.slnx -c Debug
```

### 2. Start the server

```powershell
dotnet run --project .\NetState.Server
```

By default, the development profile serves the API on:

- `http://localhost:5138`
- `https://localhost:7088`

The desktop client is currently configured to use `http://localhost:5138/`.

### 3. Start the desktop client

In a second terminal:

```powershell
dotnet run --project .\NetState.Client
```

### 4. Add domains to monitor

From the client you can:

- Add a single domain manually.
- Edit an existing monitored domain.
- Add expected headers for validation.
- Use the Resolve Hosts tab to discover candidate subdomains for a base domain.
- Inspect the latest response headers and body captured by a check.

## Server Behavior

### Persistence

- The server uses Entity Framework Core with SQLite.
- The database is created automatically at startup via `EnsureCreated()`.
- If no connection string is configured, the server falls back to `Data Source=netstate.db`.

### Monitoring loop

- A hosted background service loads all monitored domains every 5 minutes.
- Each domain is checked with redirects disabled so redirect expectations can be validated explicitly.
- A status transition from any previous state to `Down` triggers an alert.
- A transition from `Down` back to `Healthy` triggers a recovery alert.

### Alerts

Alert delivery is currently hardcoded to post to:

```text
https://mail.yggdrasil.au/send
```

The recipient and sender addresses are also hardcoded in the server implementation. If you want to deploy this outside the current environment, move those values into configuration before production use.

## Desktop Client Behavior

- Loads domains from the server on startup.
- Auto-refreshes the dashboard every 30 seconds.
- Shows connection status based on the latest API load result.
- Supports manual checks for individual domains.
- Supports inspecting the last captured response body and headers.

## API Overview

The server exposes a small REST API for domain management.

### Endpoints

- `GET /api/domains` - list all monitored domains.
- `POST /api/domains` - create a monitored domain.
- `PUT /api/domains/{id}` - update a monitored domain.
- `DELETE /api/domains/{id}` - delete a monitored domain.
- `POST /api/domains/{id}/check` - run a check immediately and persist the result.

### Example payload

```json
{
	"name": "Main Website",
	"url": "https://example.com",
	"expectation": 2,
	"expectedValue": "200",
	"expectedHeaders": {
		"server": "cloudflare"
	}
}
```

Expectation enum values:

- `0` = `Redirect`
- `1` = `HtmlHash`
- `2` = `HttpStatus`

Status enum values:

- `0` = `Unknown`
- `1` = `Healthy`
- `2` = `Degraded`
- `3` = `Down`

## Logging

Both the client and server initialize the shared diagnostics system on startup.

- Logs are written under each app's output directory in `logs/<AppName>/<session>/`.
- `debug.log` contains informational and general log messages.
- `exception.log` contains captured exceptions.
- `trace.log` is written in Debug builds.

Old session logs older than 24 hours are cleaned up automatically.

## Development Notes

- The solution currently uses minimal APIs instead of controllers.
- The server uses `EnsureCreated()` rather than migrations.
- The client base API URL is hardcoded in `MainWindowViewModel`.
- The server-side mail alert endpoint and email addresses are hardcoded.

Those defaults are acceptable for local development, but they should be moved into configuration if the project is intended for broader deployment.

## Test Command

If tests are added or already present in the solution, run:

```powershell
dotnet test -c Debug --logger "trx;LogFileName=test_results.trx"
```

## Current State

This repository already contains the core monitoring flow end to end:

1. Configure domains in the Avalonia client.
2. Persist them through the server API into SQLite.
3. Run checks manually or via the background worker.
4. Capture status, headers, body, and error details.
5. Inspect recent responses from the desktop UI.

If you want to extend the project, the next logical improvements are configuration-driven alerting, migration support, authentication, and richer monitoring policies.

## Notice

This project was generated entirely with AI.
