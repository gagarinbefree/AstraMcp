# Astra MCP for Windows x86

Local MCP adapter for Astra R-Nesting.

## Included

* Six MCP tools for validation, submission, job polling, results and cancellation.
* Native Astra XML exchange with a persistent SQLite queue and idempotent requests.
* Background execution on a separate Windows desktop, enabled by default.
* Visible execution for diagnostics through `Astra:RunInBackground=false`.
* Owned-process cancellation, execution deadlines and native stdout/stderr logs.
* Sequential Tests, Build and Release jobs with cancellation of superseded runs.

## Installation

Extract the versioned `AstraMcp-win-x86-*.zip` into a writable directory. It includes the
.NET runtime; Node.js and a separate .NET runtime installation are not required.
The SHA-256 checksum is attached alongside the archive.
The archive also includes a configuration guide and the profile setup script.

Install Astra separately and configure its command-line import, calculation and
XML export profile as described in the repository README. The shipped
`config.json` uses `ProfileReady=false` until that configuration is complete.
Pass the absolute configuration path with `--config` in your MCP client.

Background execution still requires a suitable Windows user session and the
installed Astra application. It does not provide a headless Linux engine.
Before each calculation the adapter stops existing matching Astra processes;
unsaved work in those processes can be lost. Use a dedicated test profile.
