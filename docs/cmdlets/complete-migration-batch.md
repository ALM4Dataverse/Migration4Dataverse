# Complete-M4dMigrationBatch

Completes a leased batch through the lower-level store interface. In normal usage the scheduler performs batch completion itself.

## Parameters

| Parameter | Required | Default | Description |
|---|---:|---|---|
| `-Store` | Yes | — | Store object returned by `New-M4dMigrationJobStore`. |
| `-BatchId` | Yes | — | ID of the batch being completed. |
| `-WorkerId` | Yes | — | Worker that owns the batch lease. |
| `-OutcomesJson` | Yes | — | JSON string containing the batch outcomes. |
| `-HasErrors` | No | False | Marks the completion as containing row errors. |
| `-ChildTable` | No | Omitted | Child job/table name for one dependency output. Must be supplied together with `ChildPayloadJson`. |
| `-ChildPayloadJson` | No | Omitted | JSON payload for the child work; must be supplied together with `ChildTable`. |
| `-LastError` | No | Omitted | Optional final error text stored with the batch. |

## Example

```powershell
Complete-M4dMigrationBatch -Store $store -BatchId $batchId -WorkerId $workerId `
    -OutcomesJson '{"Succeeded":10,"Failed":0}' `
    -ChildTable 'ContactImport' -ChildPayloadJson '{"Records":[]}'
```

This creates at most one dependency event in this function call. Supplying only one of `ChildTable` and `ChildPayloadJson` throws an error.
