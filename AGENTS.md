# Repository guidance

## Documentation audiences and boundaries

- Keep the root README concise and focused on the supported PowerShell workflow and runnable examples. Clearly separate user guidance from contributor material. The C# engine is an internal implementation, not a supported public API; put C# scheduler details and the standalone data-flow pipeline under `docs/dev/`, and do not present those types as `Invoke-Migration` step types.
- Keep public reference pages focused on the PowerShell contract: supported migration steps, connectors and options, custom callback contracts, and migration-graph transformation semantics.
- For Dataverse, document Migration4Dataverse-specific adapter behavior and constraints. Link to the relevant upstream `Rnwood.Dataverse.Data.PowerShell` cmdlet and concept pages on GitHub for complete command options, authentication, connections, querying, and writes; do not reproduce their full option catalogs except where needed to explain an adapter difference or safety concern.
- Use GitHub callouts (`[!NOTE]`, `[!TIP]`, `[!IMPORTANT]`, `[!WARNING]`, `[!CAUTION]`) for meaningful guidance and risks; avoid using them as decoration.
- Keep quick-start prerequisites accurate. Building the repository requires PowerShell 7.6+, the .NET 10 SDK, and Node.js/npm for the embedded dashboard assets.

## Keep documentation in sync

- Keep all target examples aligned with implemented engine capabilities. Each target folder must contain a self-contained `example.ps1` and `Setup-Example.ps1`; do not add shared example helpers, config files, wrapper scripts, or script parameters. Keep connector types/options inline with their connection definitions where possible. Update every applicable example, the PowerShell cmdlets, and tests when engine behavior changes.
- Keep the binary `Invoke-Migration` cmdlet generic: do not add example job/table names to engine orchestration. Update its PowerShell contract documentation and integration tests when job callbacks, named connections, multi-parent dependencies, run options, or module loading change.
- When implementation changes affect architecture, dependency release, SQLite schema or transactions, leases, retries, checkpoints, cancellation, or recovery, update the relevant prose and Mermaid diagrams in `docs/dev/architecture.md` in the same change.
- Update `docs/reference.md` when a supported PowerShell job shape, connector option, callback contract, or transformation behavior changes. Keep internal C# behavior in the developer guides.
- When documentation structure or audience boundaries change, keep `docs/README.md` and `docs/dev/README.md` aligned with the available guides.
- Check relative Markdown links and heading anchors when adding, moving, or renaming documentation. Keep the README's example and contributor links current.
