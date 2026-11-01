# Get-M4dMigrationComponent

Lists supported migration source connectors, target connectors, and graph actions with concise help derived from the Markdown reference pages. The help catalog is embedded in the module, so it is available both from an installed module and the standalone CLI.

```powershell
Get-M4dMigrationComponent
Get-M4dMigrationComponent -Category Source
Get-M4dMigrationComponent -Category Action -Name transform -Detailed
```

Each result has `Category`, `Name`, `Help`, and `Documentation` properties. Without `-Detailed`, `Help` contains a concise description appropriate for browsing the catalog. With `-Detailed`, it contains the full Markdown connector guide or the relevant action guidance from the migration configuration reference. Use `-Name` with `-Category` to select one component; if the same name is used in more than one category, both are returned unless a category is specified.

The matching CLI action is:

```sh
m4d get-component --category Source --name file --detailed
```
