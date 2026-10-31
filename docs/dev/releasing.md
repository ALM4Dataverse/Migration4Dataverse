# Releasing Migration4Dataverse

Migration4Dataverse is published to the PowerShell Gallery and GitHub Releases. Squash-merged pull request titles on `main` drive semantic-release after the initial `v0.1.0` release has been bootstrapped manually.

## Release model

- Conventional Commit pull request titles determine the next semantic version.
- `fix:` and `perf:` create patch releases.
- `feat:` creates a minor release.
- `!` in the title or a `BREAKING CHANGE` footer creates a major release.
- `docs:`, `test:`, `build:`, `ci:`, `chore:`, `refactor:`, and `revert:` do not create a release by themselves.
- semantic-release tags the repository, publishes to the PowerShell Gallery, creates a GitHub Release, uploads the nupkg and standalone CLI executables, and appends the version-specific installation command.
- There is no release pull request. Merging a release-worthy PR to `main` publishes immediately.

The source manifest keeps a development version. `build/Stage-MigrationModule.ps1` injects the semantic-release version into the staged manifest. Do not hand-edit `ModuleVersion` for releases.

## One-time bootstrap

Create the `v0.1.0` baseline before enabling `.github/workflows/release.yml` on `main`. This prevents semantic-release from starting the package at `1.0.0`.

1. Merge the module rename and packaging changes without the release workflow.
2. Set a PowerShell Gallery API key in the current shell:

   ```powershell
   $env:PSGALLERY_API_KEY = '<api-key>'
   ```

3. Build, validate, and publish `0.1.0`:

   ```powershell
   pwsh ./build/Test-MigrationModulePackage.ps1 -Version 0.1.0
   pwsh ./build/Publish-MigrationModule.ps1 -Version 0.1.0
   ```

4. Create and push the baseline tag, then create the GitHub Release:

   ```powershell
   git tag v0.1.0
   git push origin v0.1.0
   gh release create v0.1.0 artifacts/packages/Migration4Dataverse.0.1.0.nupkg `
       --title 'v0.1.0' `
       --notes 'Initial PowerShell Gallery release.'
   pwsh ./build/Update-GitHubReleaseNotes.ps1 -Version 0.1.0 -Tag v0.1.0
   ```

5. Merge the release-automation PR with a non-release title such as `ci: add semantic-release workflow`. Because `v0.1.0` is already tagged and the merge commit is not release-worthy, no new version is published.

## Repository configuration

Configure these settings before the first automated release:

- Add the `PSGALLERY_API_KEY` Actions secret at repository or organization level.
- Keep `GITHUB_TOKEN` permissions enabled for repository contents, issues, and pull requests as declared in `.github/workflows/release.yml`.
- Require the `Validate PR title` check in the `main` branch protection rule or ruleset.
- Require squash merges and ensure the PR title becomes the squash commit subject.

## Automated release flow

`.github/workflows/release.yml` runs on every push to `main` and can also be dispatched manually. It:

1. Restores and builds the .NET solution with the locked dependency graph.
2. Installs `Microsoft.PowerShell.PSResourceGet` and `Rnwood.Dataverse.Data.PowerShell`.
3. Installs the pinned Dataverse PowerShell module used in the CLI bundle.
4. Skips semantic-release until the `v0.1.0` baseline tag exists.
5. Runs semantic-release from the root `package.json`.

`.releaserc.json` uses this plugin order:

1. Analyze the commit and calculate the next version.
2. Generate release notes.
3. Stage, test, and publish the self-contained CLI for all five supported runtime identifiers.
4. Stage, test, pack, and publish the module to the PowerShell Gallery.
5. Create the GitHub Release and upload the nupkg and CLI executables.
6. Append the exact installation command to the GitHub Release.

If Gallery publishing fails, the GitHub Release is not created. If GitHub release creation fails after publishing, do not republish the same version; retry only the GitHub release step.

## Package layout

The staged module contains:

- `Migration4Dataverse.psd1`
- `Migration4Dataverse.psm1`
- `LICENSE`
- `lib/net10.0/` with the complete Release engine output, including `runtimes/` for Linux, macOS, and Windows

The manifest requires `Rnwood.Dataverse.Data.PowerShell` 3.3.0 or later. `Install-Module Migration4Dataverse` installs that dependency automatically.

`build/Test-MigrationModulePackage.ps1` creates the nupkg under `artifacts/packages`, verifies the expected module and native-library entries, rejects packages above 240 MB, and imports the staged module. The full package is currently about 460 MiB installed and roughly 169 MB compressed.

`build/Publish-MigrationCli.ps1` stages the versioned Migration4Dataverse module alongside exactly `Rnwood.Dataverse.Data.PowerShell` 3.3.0, then publishes self-contained single-file executables with .NET 10.0.13 and PowerShell SDK 7.6.7 for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, and `osx-arm64`. It runs `build/Test-MigrationCliPackage.ps1` against Linux x64, which checks the full command surface, parser errors, NDJSON output, and a local file-to-file migration. CLI binaries are written under `artifacts/cli/<version>/`.

## Local validation

Run the normal test suite and package smoke test before merging packaging changes:

```powershell
dotnet restore Migration4Dataverse.slnx --locked-mode
dotnet build Migration4Dataverse.slnx --configuration Release --no-restore
dotnet test Migration4Dataverse.slnx --configuration Release --no-build
pwsh ./build/Test-MigrationModulePackage.ps1 -Version 0.0.0 -SkipBuild
pwsh ./build/Publish-MigrationCli.ps1 -Version 0.0.0 -RuntimeIdentifiers linux-x64 -SkipBuild
```

Publishing all five release assets requires network access to restore the RID-specific .NET runtime packs. Building a CLI requires PowerShell 7.6+ to stage the module and .NET 10 SDK; the produced executable does not require either on the target machine.

To validate a clean Gallery installation after publishing:

```powershell
Install-Module Migration4Dataverse -RequiredVersion 0.1.0 -Scope CurrentUser -Force
Import-Module Migration4Dataverse
Get-Command Invoke-M4dMigration
```

PowerShell Gallery versions cannot be reused. If an upload is incorrect, unlist the version and publish a new patch release rather than attempting to overwrite it.
