# Synthetic Data Generator

A .NET console application that connects to a Microsoft SQL Server database, reads its schema (tables, columns, primary keys, and foreign keys), and automatically generates realistic synthetic data using the [Bogus](https://github.com/bopoda/Bogus) library. Tables are inserted in the correct order based on foreign-key dependencies so referential integrity is preserved.

## Features

- **Schema-driven generation** — reads table and column metadata directly from SQL Server system views, so no manual mapping is needed.
- **Automatic dependency ordering** — uses topological sort (Kahn's algorithm) to determine safe insertion order. Self-referencing foreign keys are detected and handled separately.
- **Smart value generation** — column names are matched against heuristic rules (e.g. `email` → realistic email, `first_name` → realistic first name). When no name rule matches, values are generated based on the SQL data type.
- **External dependency detection** — warns when foreign keys reference tables outside the current scope (outbound) or when external tables reference scoped tables (inbound).
- **Custom dependency ordering** — define non-FK column relationships so tables are inserted in the right order even without formal foreign keys.
- **Table and column filtering** — optionally restrict generation to a specific schema, an explicit list of tables, and per-table column lists.
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

# Optional: restrict generation to a single schema. Defaults to all schemas.
Schema: dbo

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
# The first entry is the source (must be inserted first); subsequent entries depend on it.
CustomDependencies:
  - dbo.Lookup.Code|dbo.Orders.LookupCode
  - dbo.Categories.Id|dbo.Products.CategoryId|dbo.Inventory.CategoryId
```

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
