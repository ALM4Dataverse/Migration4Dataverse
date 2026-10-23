# Custom PowerShell target

The `custom` target calls a PowerShell callback once per mapped batch. The caller supplies any non-null runtime connection object; the callback is responsible for target-side replay safety.

## Configuration and callback example

```powershell
Target = @{
    Type = 'custom'
    ConnectionId = 'service'
    Operation = 'Write'
    Custom = @{
        Identity = 'customers-writer-v1'
        MaxConcurrency = 1
        Modules = @('My.Target.Module')
        Callback = {
            param($Connection, $Records, $Context)
            # Write this batch idempotently.
            return $true
        }
    }
}
```

The callback receives:

1. The connection object from `-Connections`.
2. The mapped records for the batch. Each record has an `Id` value derived from `Source.Id`.
3. A context object with `StepId`, `TableName`, `RunId`, `BatchId`, `Sequence`, and `TargetPath`.

It must emit exactly one Boolean `$true` after success. Exceptions, no output, extra output, `$false`, or any non-Boolean value fail the batch. Send diagnostics to non-output streams.

## Options

| Property | Required | Default | Description |
|---|---:|---:|---|
| `Identity` | Yes | — | Stable implementation identity included in resume compatibility. Change it when callback behavior changes. |
| `Callback` | Yes | — | Scriptblock writing one batch. |
| `MaxConcurrency` | No | `1` | Positive callback concurrency for the target connection. Steps sharing that target connection must use the same value. Choose a value no greater than the cmdlet's `MaxDegreeOfParallelism`. |
| `Modules` | No | `[]` | Module names or paths imported into the embedded runspace pool. |
| `PageSize` | No | `100` | Source-only option; has no effect on target callbacks. |

The callback may execute again if a remote write succeeds but its SQLite completion is not committed. Use `BatchId` or another stable idempotency key so that a retry is safe. Target concurrency is shared among steps that use the same target connector type and connection ID.
