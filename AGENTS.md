# Repository guidance

## Keep documentation in sync

- Preserve the README's explicit separation between the fixed PowerShell example and the general C# engine. Update its links and relevant sections when either surface changes.
- Keep all target examples aligned with implemented engine capabilities. Each target folder must contain a self-contained `example.ps1` and `Setup-Example.ps1`; do not add shared example helpers, config files, wrapper scripts, or script parameters. Keep connector types/options inline with their connection definitions where possible. Update every applicable example, the PowerShell cmdlets, and tests when engine behavior changes.
- Keep the binary `Invoke-Migration` cmdlet generic: do not add example job/table names to engine orchestration. Update its public contract documentation and integration tests when job callbacks, named connections, multi-parent dependencies, run options, or module loading change.
- When implementation changes affect architecture, public scheduler contracts, dependency release, SQLite schema or transactions, leases, retries, checkpoints, cancellation, or recovery, update the relevant prose and Mermaid diagrams in `docs/dev/architecture.md` in the same change.
- Keep `docs/reference.md` aligned with the supported job shape, connectors/options, custom callback contracts, and transformation semantics; update it whenever one of those public behaviors changes.
- When the documentation structure changes, keep `docs/README.md` and `docs/dev/README.md` aligned with the available guides.
- Keep the README concise and user-oriented; put detailed internal behavior and diagrams in `docs/dev/architecture.md`.
