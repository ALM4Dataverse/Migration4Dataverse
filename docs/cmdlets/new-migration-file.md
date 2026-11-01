# New-M4dMigrationFile

Creates a new `.m4d` file containing the complete `$Migration` structure, commented examples, `RunOptions`, and `PreRun` / `PostRun` scriptblocks.

```powershell
New-M4dMigrationFile -Path './migration-job.m4d'
```

The CLI equivalent is:

```sh
m4d new-file --path ./migration-job.m4d
```

The command fails rather than overwriting an existing file. Specify `-Force` (or `--force`) to replace it. `-WhatIf` (or `--what-if`) previews the operation without creating a file. The parent directory must already exist, and the path must use the `.m4d` extension.

The generated file starts with an empty `Steps` array and is not runnable until you add migration nodes. See [`Invoke-M4dMigration`](./invoke-migration.md) for the file contract and [migration step configuration](../configuration.md) for supported nodes and options.
