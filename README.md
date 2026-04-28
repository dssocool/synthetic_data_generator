# Synthetic Data Generator

A .NET console application that connects to a Microsoft SQL Server database, reads its schema (tables, columns, primary keys, and foreign keys), and automatically generates realistic synthetic data using the [Bogus](https://github.com/bopoda/Bogus) library. Tables are inserted in the correct order based on foreign-key dependencies so referential integrity is preserved.

## Features

- **Schema-driven generation** — reads table and column metadata directly from SQL Server system views, so no manual mapping is needed.
- **Automatic dependency ordering** — uses topological sort (Kahn's algorithm) to determine safe insertion order. Self-referencing foreign keys are detected and handled separately.
- **Smart value generation** — column names are matched against heuristic rules (e.g. `email` → realistic email, `first_name` → realistic first name). When no name rule matches, values are generated based on the SQL data type.
- **External dependency detection** — warns when foreign keys reference tables outside the current scope (outbound) or when external tables reference scoped tables (inbound).
- **Custom dependency ordering** — define non-FK column relationships so tables are inserted in the right order even without formal foreign keys.
- **Custom value lists** — pin any column (in-scope or out-of-scope) to a fixed set of values from a flat file or inline YAML list. A group of dependent columns may have at most one such source-data provider; conflicts fail fast at validation time.
- **Table and column filtering** — optionally restrict generation to one or more schemas, an explicit list of tables, and per-table column lists.
- **Locale support** — generated data can target different locales (defaults to `en`).
- **Seeded generation** — supply a seed for reproducible output.

## Prerequisites

- [.NET 8 SDK or higher](https://dotnet.microsoft.com/en-us/download)
- A SQL Server instance you can connect to (the tool needs permission to read schema metadata and insert rows)

## Configuration

Create an `appsettings.yaml` file in `src/SyntheticDataGenerator/` (this file is gitignored). The example below shows every supported setting; comments mark which ones are optional and what they default to.

```yaml
# Required: SQL Server connection string.
ConnectionString: Server=YOUR_SERVER;Trusted_Connection=True;TrustServerCertificate=True;

# Optional: database name. Overrides Initial Catalog / Database in the connection string.
DatabaseName: YOUR_DATABASE

# Optional: restrict generation to specific schemas. Defaults to all schemas.
# Supports a single value or a list:
Schema: dbo
# Schema:
#   - dbo
#   - sales

# Optional: tables (and optionally columns) in scope. Defaults to all tables.
# Two formats are supported and can be mixed:
#   - Simple form: just the table name (all columns are in scope).
#   - Structured form: Table + Columns list (only the listed columns are targeted;
#     omit Columns or leave it empty to include all columns).
TablesToInclude:
  - dbo.Orders
  - Table: dbo.Users
    Columns:
      - FirstName
      - LastName
      - Email

# Optional: number of rows to insert per table. Defaults to 100.
RowsPerTable: 100

# Optional: integer seed for reproducible data. Defaults to random.
Seed: 12345

# Optional: Bogus locale for generated data. Defaults to "en".
Locale: en

# Optional: non-FK column relationships used for ordering.
# Each entry is a pipe-separated (`|`) list of `schema.table.column` references.
# The order of entries does NOT matter — the tool inspects each column's schema
# and picks the source via this priority cascade:
#   1. CustomValueLists-backed column (file or inline values)
#   2. External root (column outside TablesToInclude)
#   3. Primary key
#   4. Auto-generated (identity / computed / sequence default / rowversion)
#   5. Unique constraint / unique index
#   6. First declared (final tiebreaker)
# Every other column in the group becomes a dependent that copies values from
# the chosen source.
#
# A source column may live OUTSIDE TablesToInclude — either the entire table is
# excluded, or the column is excluded from a scoped table's Columns filter. In
# that case the source is treated as an "external root": values are streamed
# from the live database (with bounded memory) and copied into the dependents.
#
# A group may contain at most ONE source-data provider, where a source-data
# provider is either:
#   * an external root column without a CustomValueLists backing (data
#     streamed from the live DB), OR
#   * any column with a CustomValueLists entry (in-scope or out-of-scope).
# Two providers in the same group is a fatal error — the tool reports both
# offenders and how each is being treated. A group with zero providers is
# fine: the cascade picks one in-scope column to generate, and dependents
# copy from it. The tool also fails fast if a DB-backed external root has no
# non-null data.
CustomDependencies:
  - dbo.Lookup.Code|dbo.Orders.LookupCode
  # Order is irrelevant — dbo.Categories.Id is a primary key, so it is picked
  # as the source even though it is not declared first.
  - dbo.Products.CategoryId|dbo.Categories.Id|dbo.Inventory.CategoryId

# Optional: maximum number of values held in memory per external custom-dependency
# root column. The streamer rotates this window across the entire result set so
# even billion-row source tables stay within bounded memory. Defaults to 10000.
CustomDependencyBufferSize: 10000

# Optional: maximum number of unrelated tables to insert/update in parallel.
# Defaults to Environment.ProcessorCount; set to 1 to force fully sequential
# execution. Two tables only run concurrently when neither has a foreign-key
# nor a CustomDependencies edge to the other (directly or transitively); the
# scheduler always waits for every parent table to finish before dispatching
# its dependents. See "Parallel execution" below for the determinism
# guarantees and connection-pool considerations.
MaxParallelTables: 8

# Optional: back any column with a fixed list of values. Each entry maps a
# `schema.table.column` to EITHER a `File:` path (flat values file, one value
# per line, blank lines skipped) OR an inline `Values:` list. Exactly one of
# the two must be set per entry.
#
# A column listed here MUST exist in the actual database schema (validated at
# startup). Two usage modes are supported:
#
# 1. **Standalone (in-scope) entry** — the column lives INSIDE TablesToInclude
#    and is NOT referenced by any CustomDependencies group. The generator picks
#    values for that column directly from the file/inline list. Use this for
#    enum-like columns (status codes, regions, categories) where the live DB
#    doesn't yet have representative values to mirror.
#
# 2. **CustomDependencies-backed entry** — the column appears in a
#    CustomDependencies group. The list backs the group's source column (and
#    therefore every dependent in the group), avoiding any SQL cursor against
#    the source table. The column may be either out-of-scope (the classic
#    external-root replacement) or in-scope (where it doubles as the column's
#    own generator AND the source feed for its dependents).
#
# A standalone CustomValueLists entry whose column is NOT in TablesToInclude
# (and NOT in any CustomDependencies group) fails fast at validation time.
CustomValueLists:
  # File-backed external root: path is absolute, or relative to the working
  # directory / plan file.
  - Column: dbo.Lookup.Code
    File: ./values/lookup_codes.txt
  # Inline-backed external root: values written directly in YAML.
  - Column: dbo.Lookup.Region
    Values:
      - APAC
      - EMEA
      - AMER
  # Standalone in-scope: dbo.Orders is in TablesToInclude and Status is not in
  # any CustomDependencies group — every generated row's Status comes from this list.
  - Column: dbo.Orders.Status
    Values:
      - Pending
      - Active
      - Closed
```

### External custom dependency roots

Suppose you have a large `dbo.Lookup` table that you do not want to insert into
(perhaps because it is reference data already maintained elsewhere), but you do
want the synthetic rows generated for `dbo.Orders` to use realistic values from
`dbo.Lookup.Code`. Just leave `dbo.Lookup` out of `TablesToInclude` and declare
the relationship under `CustomDependencies`:

```yaml
TablesToInclude:
  - dbo.Orders
CustomDependencies:
  # dbo.Lookup is NOT in TablesToInclude — it becomes an external root whose
  # values are streamed from the DB at runtime and copied into Orders.LookupCode.
  # Order does not matter; the external column always wins as source.
  - dbo.Orders.LookupCode|dbo.Lookup.Code
```

If you would rather control the source values yourself — for example, when the
live `dbo.Lookup` is empty in lower environments, or you want to constrain
generated orders to a known short list of codes — pair the same
`CustomDependencies` entry with a `CustomValueLists` entry. You can supply the
values either as a flat text file (`File:`) or inline in YAML (`Values:`).
Exactly one of the two must be set per entry.

```yaml
TablesToInclude:
  - dbo.Orders
CustomValueLists:
  # File-backed: one value per line.
  - Column: dbo.Lookup.Code
    File: ./values/lookup_codes.txt
  # Inline-backed: values declared right here.
  - Column: dbo.Lookup.Region
    Values:
      - APAC
      - EMEA
      - AMER
CustomDependencies:
  - dbo.Orders.LookupCode|dbo.Lookup.Code
  - dbo.Orders.RegionCode|dbo.Lookup.Region
```

Every inserted `dbo.Orders.LookupCode` will be picked uniformly at random from
the file's lines, and every `dbo.Orders.RegionCode` from the inline list. No
SQL cursor is opened against `dbo.Lookup`. Inline values are also embedded
directly into the generated `plan.yaml`, so plan files using `Values:` stay
self-contained and can be re-executed without the original `appsettings.yaml`.

### Standalone CustomValueLists for in-scope columns

`CustomValueLists` also works as a simple "pick from this set" generator for
columns that ARE in `TablesToInclude`. When an entry's column is in scope and
not referenced by any `CustomDependencies` group, the generator emits values
for that column directly from the file/inline list — no dependency wiring
required. This is the cleanest way to populate small enum-like columns like a
`Status`, `Region`, or `Category`:

```yaml
TablesToInclude:
  - dbo.Orders
CustomValueLists:
  - Column: dbo.Orders.Status
    Values:
      - Pending
      - Active
      - Closed
```

If the column is NOT in scope and the entry is not used by any
`CustomDependencies` group either, validation fails fast with a message
asking you to add the column to `TablesToInclude` or include it in a
`CustomDependencies` group.

### One source-data provider per dependency group

`CustomDependencies` groups can have at most one column that supplies "real"
source data. The two ways a column qualifies as a source-data provider are:

1. It is **out-of-scope** (an external root) and has no `CustomValueLists`
   backing — values stream from the live DB.
2. It has a **`CustomValueLists`** entry — values come from the file/inline
   list (regardless of whether the column itself is in scope or not).

A group with two providers is a configuration mistake; the tool fails fast
and reports both offenders, e.g.:

```
CustomDependencies group [dbo.Orders.RegionCode | dbo.Lookup.Region | dbo.Areas.Code]
has multiple source-data providers: [dbo.Lookup].[Region] (CustomValueLists)
and [dbo.Areas].[Code] (external root). At most one source-data provider is
allowed per group (an external column or a CustomValueLists-backed column).
```

A group with **zero** providers (every column in scope, no value lists) is
valid: the source-resolution cascade picks one in-scope column to generate,
and dependents copy from it.

### Parallel execution

By default the executor inserts (and updates) multiple **unrelated** tables in
parallel, capped by `MaxParallelTables` (defaults to
`Environment.ProcessorCount`; set to `1` to force the legacy sequential
behavior). Two tables are considered unrelated only when there is no
foreign-key edge and no `CustomDependencies` edge between them, directly or
transitively — every dependency target is fully written before any dependent
table starts, so referential integrity and `_generatedKeys`-driven FK
resolution still work the way they always have.

Determinism with `Seed:` is preserved per-table: each table builds its own
`Bogus.Faker` seeded from `(Seed, table.FullName)` via a stable FNV-1a hash
of the table name. The same seed therefore produces the same rows for any
given table regardless of how the scheduler interleaves it with siblings; a
seeded run is fully reproducible row-for-row even when `MaxParallelTables > 1`.

A few practical notes:

- Each in-flight table opens its own pooled `SqlConnection`. If you set
  `MaxParallelTables` higher than the connection string's `Max Pool Size`
  (default 100), you will hit pool-exhaustion timeouts; bump `Max Pool Size`
  in the connection string to at least `MaxParallelTables`.
- A failure on one table does not cancel its in-flight siblings; they run to
  completion (or their own failure) and dependents are still attempted, with
  the same fall-back behavior as the sequential path (an empty parent
  `_generatedKeys` simply yields generated/null FK values).
- The order in which tables appear in the final progress output reflects
  completion order, not the plan's `order` field.

## Usage

Build the project first:

```bash
dotnet build
```

Then run the tool with one of the following commands:

| Command | Description |
|---------|-------------|
| `dotnet run --project src/SyntheticDataGenerator -- insert` | Read the database schema and insert synthetic rows immediately, using the scope defined in `appsettings.yaml`. |

## Running Tests

The test suite uses xUnit and connects to whatever SQL Server is configured in `src/SyntheticDataGenerator/appsettings.yaml` (the same `ConnectionString` used by the app — the file is linked into the test project at build time). Any SQL Server instance you can reach works (LocalDB, Express, Developer, a containerized instance, etc.); the connecting account just needs permission to create and drop databases on that server.

For each test run a uniquely-named database (`SyntheticDataGenTest_<guid>`) is created on that server and dropped automatically when the run finishes, so your existing databases are not touched.

```bash
dotnet test
```
