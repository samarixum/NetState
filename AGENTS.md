# AGENTS.md

## Setup
- Build: `dotnet build NetState.slnx -c Debug`
- Run tests: `dotnet test -c Debug --logger "trx;LogFileName=test_results.trx"`

## Use Exact Code style
always use PascalCase
read [Style.md](Style.md)

## CONTRIBUTING
See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines and workflows.
When writing code, always add documentation and update existing relevant comments and documentation.

# Dotnet
Always use the latest .net 10 lts C# language features.
When making changes to dotnet projects read the .csproj files to understand dependencies, project structure and dotnet configurations.


