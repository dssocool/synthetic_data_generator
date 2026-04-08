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
│       │   └── GenerationPlan.cs        # YAML plan DTOs
│       └── Services/
│           ├── SchemaReader.cs          # Reads DB metadata from sys views
│           ├── DependencyGraph.cs       # FK graph + topological sort
│           ├── PlanGenerator.cs         # Builds/writes/reads YAML plans
│           ├── ColumnValueGenerator.cs  # Executes Bogus generators
│           └── DataInserter.cs          # INSERT statement generation/execution
└── tests/
    └── SyntheticDataGenerator.Tests/    # xUnit integration tests
        ├── DatabaseFixture.cs           # Creates/drops a LocalDB test database
        └── IntegrationTests.cs          # 20 integration tests
```

## Configuration

Create an `appsettings.yaml` file in `src/SyntheticDataGenerator/` (this file is gitignored):

```yaml
ConnectionString: Server=YOUR_SERVER;Trusted_Connection=True;TrustServerCertificate=True;
DatabaseName: YOUR_DATABASE
Schema: dbo
TablesToInclude: []
TablesToExclude: []
RowsPerTable: 100
Seed: 12345
Locale: en
```

| Key | Required | Default | Description |
|-----|----------|---------|-------------|
| `ConnectionString` | Yes | — | SQL Server connection string |
| `DatabaseName` | No | — | Database name; overrides `Initial Catalog` / `Database` in the connection string |
| `Schema` | No | all schemas | Restrict to a single schema name |
| `TablesToInclude` | No | `[]` | Only generate data for these tables |
| `TablesToExclude` | No | `[]` | Skip these tables |
| `RowsPerTable` | No | `100` | Number of rows to insert per table |
| `Seed` | No | random | Integer seed for reproducible data |
| `Locale` | No | `en` | Bogus locale for generated data |

## Usage

### Build

```bash
dotnet build
```

### Run — Direct Mode

Reads the database schema and inserts synthetic rows immediately. A `plan.yaml` file is also saved in the current directory so you can inspect or re-run what was generated:

```bash
dotnet run --project src/SyntheticDataGenerator
```

### Run — Generate Plan

Creates a YAML plan file that you can review and edit before inserting any data:

```bash
dotnet run --project src/SyntheticDataGenerator -- --generate-plan plan.yaml
```

If the output path is omitted it defaults to `plan.yaml`.

### Run — Execute Plan

Inserts data according to a previously generated (and optionally edited) plan file:

```bash
dotnet run --project src/SyntheticDataGenerator -- --execute-plan plan.yaml
```

## Running Tests

The test suite uses xUnit and requires **SQL Server LocalDB** (typically included with Visual Studio or SQL Server Express).

```bash
dotnet test
```

A temporary database is created and dropped automatically for each test run.
