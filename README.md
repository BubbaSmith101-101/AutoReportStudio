# AutoReport Studio

A Visual Studio 2022 ASP.NET Core MVC (.NET 8) solution written in C#.

## What it does
- Connects to SQL Server.
- Discovers tables, columns, primary keys, and foreign-key relationships.
- Lets the user select tables.
- Sends only the selected schema metadata to a local Ollama model for report planning.
- Requests 2–6 read-only SQL report sections.
- Validates generated SQL against modification/administrative keywords.
- Executes SELECT queries and renders the report in MVC.
- Uses Ollama to create a factual executive summary from query results.
- Exports report tables to Excel.

## Requirements
Visual Studio 2022, .NET 8 SDK, SQL Server, and Ollama.

The default model in `appsettings.json` is `qwen3-coder:30b`. Change it to any model installed in your Ollama environment.

## Run
1. Extract the ZIP and open `AutoReportStudio.sln`.
2. Restore NuGet packages.
3. Start Ollama and make sure the configured model exists.
4. Press F5 in Visual Studio.
5. Enter a SQL Server connection string, discover tables, select tables, and describe the report.

Example:
`Server=localhost;Database=MyDatabase;Trusted_Connection=True;TrustServerCertificate=True;`

## Important security note
Use a SQL Server login/user with SELECT-only permissions. The application's SQL keyword validation is defense-in-depth, not a substitute for database permissions. Before production use, add authentication/authorization, secret storage, auditing, a real T-SQL parser/AST allow-list, query resource limits, and schema/table allow-lists.
