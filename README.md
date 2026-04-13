# Synthetic Data Generator

A .NET console application that connects to a Microsoft SQL Server database, reads its schema (tables, columns, primary keys, and foreign keys), and automatically generates realistic synthetic data using the [Bogus](https://github.com/bopoda/Bogus) library. Tables are inserted in the correct order based on foreign-key dependencies so referential integrity is preserved.

## Features

- **Schema-driven generation** — reads table and column metadata directly from SQL Server system views, so no manual mapping is needed.
- **Automatic dependency ordering** — uses topological sort (Kahn's algorithm) to determine safe insertion order. Self-referencing foreign keys are detected and handled separately.
- **Smart value generation** — column names are matched against heuristic rules (e.g. `email` → realistic email, `first_name` → realistic first name). When no name rule matches, values are generated based on the SQL data type.
- **YAML plan workflow** — generate a YAML plan file describing every table and column, review or edit it, then execute it. This gives full control over what data gets inserted.
- **Table filtering** — optionally restrict generation to a specific schema or an explicit include/exclude list of tables.
- **Locale support** — generated data can target different locales (defaults to `en`).
- **Seeded generation** — supply a seed for reproducible output.

## Prerequisites

- [.NET 8 SDK or higher](https://dotnet.microsoft.com/en-us/download)
- A SQL Server instance you can connect to (the tool needs permission to read schema metadata and insert rows)

## Project Structure

```
synthetic_data_generator/
├── SyntheticDataGenerator.sln
├── src/
│   └── SyntheticDataGenerator/          # Main console application
│       ├── Program.cs                   # Entry point and CLI argument parsing
│       ├── Models/
│       │   ├── TableMetadata.cs         # ColumnInfo, ForeignKeyInfo, TableInfo
│       │   ├── GenerationPlan.cs        # YAML plan DTOs
│       │   └── TableScope.cs           # Scope configuration (tables + columns)
│       └── Services/
│           ├── SchemaReader.cs          # Reads DB metadata from sys views
│           ├── DependencyGraph.cs       # FK graph + topological sort
│           ├── PlanGenerator.cs         # Builds/writes/reads YAML plans
│           ├── ColumnValueGenerator.cs  # Executes Bogus generators
│           └── DataInserter.cs          # INSERT statement generation/execution
└── tests/
    └── SyntheticDataGenerator.Tests/    # xUnit integration tests
        ├── DatabaseFixture.cs           # Creates/drops a LocalDB test database
        └── IntegrationTests.cs          # Integration tests
```

## Configuration

Create an `appsettings.yaml` file in `src/SyntheticDataGenerator/` (this file is gitignored):

```yaml
ConnectionString: Server=YOUR_SERVER;Trusted_Connection=True;TrustServerCertificate=True;
DatabaseName: YOUR_DATABASE
Schema: dbo
TablesToInclude:
  - dbo.Users
  - dbo.Orders
RowsPerTable: 100
Seed: 12345
Locale: en
```

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `ConnectionString` | Yes | — | SQL Server connection string |
| `DatabaseName` | No | — | Database name; overrides `Initial Catalog` / `Database` in the connection string |
| `Schema` | No | all schemas | Restrict to a single schema name |
| `TablesToInclude` | No | all tables | Tables (and optionally columns) in scope — see below |
| `RowsPerTable` | No | `100` | Number of rows to insert per table (bootstrap mode) |
| `Seed` | No | random | Integer seed for reproducible data |
| `Locale` | No | `en` | Bogus locale for generated data |

### TablesToInclude

`TablesToInclude` controls which tables are in scope and, optionally, which columns within each table. It supports two formats that can be mixed:

**Simple form** — table names only, all columns are in scope:

```yaml
TablesToInclude:
  - dbo.Users
  - dbo.Orders
```

**Structured form** — with per-table column lists:

```yaml
TablesToInclude:
  - Table: dbo.Users
    Columns:
      - FirstName
      - LastName
      - Email
  - Table: dbo.Orders
```

When `Columns` is omitted (or empty), all columns on that table are in scope. When `Columns` is provided, only those columns are targeted. This is used by both `bootstrap` and `update` modes:

- **Bootstrap mode**: columns not listed get `generator: skip` in the plan. All columns still appear in the schema.
- **Update mode**: only the listed columns are regenerated. Primary key columns are always included automatically (with `generator: skip`) so the tool can identify which rows to update.

## Usage

### Build

```bash
dotnet build
```

The tool requires a subcommand (`bootstrap` or `update`). Running without one prints usage and exits:

```
Usage:
  dotnet run -- bootstrap                          Insert synthetic data directly
  dotnet run -- bootstrap --generate-plan [path]   Generate a plan file without inserting
  dotnet run -- update                             Update existing data directly
  dotnet run -- update --generate-plan [path]      Generate an update plan file
  dotnet run -- --execute-plan <path>              Execute a previously generated plan
```

Both `bootstrap` and `update` read their scope (which tables and columns) from `appsettings.yaml` via `TablesToInclude`.

### Bootstrap — Direct Mode

Reads the database schema and inserts synthetic rows immediately. A `plan.yaml` file is also saved in the current directory so you can inspect or re-run what was generated:

```bash
dotnet run --project src/SyntheticDataGenerator -- bootstrap
```

### Bootstrap — Generate Plan

Creates a YAML plan file (with `mode: bootstrap`) that you can review and edit before inserting any data:

```bash
dotnet run --project src/SyntheticDataGenerator -- bootstrap --generate-plan plan.yaml
```

If the output path is omitted it defaults to `plan.yaml`.

### Update — Direct Mode

Updates existing rows in place with new synthetic data. The tables and columns to update are configured in `appsettings.yaml` via `TablesToInclude` with per-table `Columns` lists. Every row in each listed table is updated:

```bash
dotnet run --project src/SyntheticDataGenerator -- update
```

Under the hood the tool:
1. Reads the database schema and validates that all listed tables/columns exist and each table has a primary key.
2. For each table, creates a SQL Server temp table (`#TableName`) with an `Id` identity column, `OriginalId_<pk>` columns matching the original primary key types, and the listed data columns.
3. Loads every primary key from the original table, generates synthetic values for the listed columns, and inserts them into the temp table.
4. Runs a single `UPDATE ... FROM ... INNER JOIN` to apply the temp table values back to the original table.

A `plan.yaml` file is also saved after completion.

### Update — Generate Plan

Creates a YAML plan file (with `mode: update`) that you can review and edit before executing:

```bash
dotnet run --project src/SyntheticDataGenerator -- update --generate-plan plan.yaml
```

The generated plan includes only the PK columns (with `generator: skip`) and the columns listed in `TablesToInclude` (with generators assigned by name heuristics / SQL type). Edit generators or add `valuesFile` entries, then run `--execute-plan`.

### Execute Plan

Executes a previously generated (and optionally edited) plan file. The plan's `mode` field determines the operation:

- `mode: bootstrap` — inserts new rows into each table.
- `mode: update` — updates existing rows using the temp-table strategy described above. The plan must include PK columns (generator `skip`) so the tool can identify which rows to update.

```bash
dotnet run --project src/SyntheticDataGenerator -- --execute-plan plan.yaml
```

## Plan File Reference

When you run `--generate-plan` (with either `bootstrap` or `update`), the tool produces a YAML file (`plan.yaml` by default) that fully describes what data will be generated or updated. You can review and edit this file before running `--execute-plan`.

### Top-level properties

| Key | Type | Description |
|-----|------|-------------|
| `mode` | `string` | Operation mode: `bootstrap` (insert new data) or `update` (update existing data). Defaults to `bootstrap`. Used by `--execute-plan` to determine behavior. |
| `seed` | `int?` | Random seed for reproducible output. Remove or set to `null` for random data each run. |
| `locale` | `string` | Bogus locale code (e.g. `en`, `fr`, `de`, `ja`). Affects names, addresses, etc. |
| `tables` | `list` | Ordered list of table definitions to generate data for. |

### Table properties

| Key | Type | Description |
|-----|------|-------------|
| `schema` | `string` | SQL schema name (e.g. `dbo`). |
| `table` | `string` | Table name. |
| `order` | `int` | Insertion order. Lower values are inserted first. Tables referenced by foreign keys must have a lower order than the tables that reference them. |
| `rowCount` | `int` | Number of rows to generate for this table. |
| `columns` | `list` | Column definitions (see below). |

### Column properties

| Key | Type | Description |
|-----|------|-------------|
| `name` | `string` | Column name (must match the database). |
| `sqlType` | `string` | SQL Server data type (e.g. `int`, `nvarchar`, `datetime2`). |
| `maxLength` | `int` | Maximum storage length in bytes (e.g. `200` for `nvarchar(100)` since nvarchar uses 2 bytes per char). |
| `precision` | `byte` | Numeric precision for `decimal`/`numeric` types. |
| `scale` | `byte` | Numeric scale for `decimal`/`numeric` types. |
| `isNullable` | `bool` | Whether the column allows NULL values. |
| `isIdentity` | `bool` | Whether the column is an identity (auto-increment) column. |
| `isPrimaryKey` | `bool` | Whether the column is part of the primary key. |
| `isComputed` | `bool` | Whether the column is computed. |
| `isRowVersion` | `bool` | Whether the column is a `rowversion`/`timestamp` column. |
| `generator` | `string` | The generator to use (see table below). |
| `generatorArgs` | `map` | Key-value arguments passed to the generator. |
| `valuesFile` | `string` | Path to a text file (one value per line) to randomly pick values from instead of using a generator. Can be absolute or relative to the plan file. |

### Generators

These are the values you can assign to the `generator` field on any column.

| Generator | Description | `generatorArgs` |
|-----------|-------------|-----------------|
| `skip` | Do not generate a value (used for identity, computed, and rowversion columns). | — |
| `foreignKey` | Pick a value from the referenced table's primary key. Automatically set for FK columns. | `referencedSchema`, `referencedTable`, `referencedColumn`, `isSelfReferencing`, `compositeFkGroup` |
| `null` | Always inserts NULL. | — |
| **Name** | | |
| `Name.FirstName` | Realistic first name. | — |
| `Name.LastName` | Realistic last name. | — |
| `Name.FullName` | Realistic full name. | — |
| `Name.JobTitle` | Job title string. | — |
| **Internet** | | |
| `Internet.Email` | Email address. | — |
| `Internet.UserName` | Username. | — |
| `Internet.Password` | Password string. | — |
| `Internet.Url` | URL. | — |
| `Internet.Avatar` | Avatar image URL. | — |
| **Phone** | | |
| `Phone.PhoneNumber` | Phone number. | `format` (default `###-###-####`) |
| **Address** | | |
| `Address.StreetAddress` | Street address. | — |
| `Address.City` | City name. | — |
| `Address.StateAbbr` | US state abbreviation. | — |
| `Address.ZipCode` | Zip/postal code. | — |
| `Address.Country` | Country name. | — |
| **Text** | | |
| `Lorem.Word` | Single random word. Supports `wrapXml: true` to wrap the value in `<data>...</data>`. | `wrapXml` (bool) |
| `Lorem.Sentence` | Random sentence. | — |
| **Finance** | | |
| `Finance.Amount` | Decimal dollar amount. | `min` (default `1`), `max` (default `10000`) |
| `Company.CompanyName` | Company name. | — |
| **Random** | | |
| `Random.Int` | Random integer. | `min` (default `1`), `max` (default `1073741823`) |
| `Random.Long` | Random long integer. | `min` (default `1`), `max` (default `4611686018427387903`) |
| `Random.Short` | Random short integer. | `min` (default `1`), `max` (default `32767`) |
| `Random.Byte` | Random byte (0–255). | — |
| `Random.Bool` | Random boolean. | — |
| `Random.Decimal` | Random decimal. | `min` (default `0`), `max` (default `99999`) |
| `Random.Double` | Random double. | `min` (default `0`), `max` (default `99999`) |
| `Random.Float` | Random float. | `min` (default `0`), `max` (default `99999`) |
| `Random.AlphaNumeric` | Random alphanumeric string. | `length` (default `8`) |
| `Random.Bytes` | Random byte array. | `count` (default `16`) |
| **Date/Time** | | |
| `Date.Past` | Random past datetime. | `yearsToGoBack` (default `5`) |
| `Date.PastDateOnly` | Random past date (no time component). | `yearsToGoBack` (default `5`) |
| `Date.Timespan` | Random time-of-day value. | — |
| `Date.PastOffset` | Random past datetimeoffset. | `yearsToGoBack` (default `5`) |
| **Other** | | |
| `Guid` | New random GUID. | — |
| `PickRandom` | Picks randomly from a provided list of values. | `values` (string array, e.g. `["A", "B", "C"]`) |

### Example: customizing a column

Change the generator for a column by editing `generator` and `generatorArgs`:

```yaml
- name: Status
  sqlType: nvarchar
  maxLength: 100
  # ...
  generator: PickRandom
  generatorArgs:
    values: ["Active", "Inactive", "Suspended"]
  valuesFile:
```

Or load values from a file:

```yaml
- name: City
  sqlType: nvarchar
  maxLength: 200
  # ...
  generator: Lorem.Word          # ignored when valuesFile is set
  generatorArgs: {}
  valuesFile: data/cities.txt    # one city name per line
```

### Common edits

- **Change row count** — set `rowCount` on any table to control how many rows are generated.
- **Skip a table** — remove the table entry from the `tables` list entirely.
- **Skip a column** — set `generator: skip` (useful for columns you want the database default to fill).
- **Force NULL** — set `generator: "null"`.
- **Use a fixed set of values** — use `PickRandom` with a `values` array, or point `valuesFile` to a text file.
- **Change insertion order** — adjust `order` values, ensuring parent tables have lower order numbers than child tables.
- **Rerun with same data** — keep `seed` the same and re-execute the plan.

## Running Tests

The test suite uses xUnit and requires **SQL Server LocalDB** (typically included with Visual Studio or SQL Server Express).

```bash
dotnet test
```

A temporary database is created and dropped automatically for each test run.
