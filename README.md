# Octo Asset Repository Services

The main data-access service of OctoMesh: a multi-tenant ASP.NET Core backend that exposes runtime entities, construction kit models, and time-series stream data over GraphQL and REST. Each tenant gets its own dynamically generated GraphQL schema derived from the tenant's CK model.

## Overview

`Meshmakers.Octo.Backend.AssetRepServices` is a Docker-deployed web service that connects to MongoDB via the Octo Runtime Engine and to CrateDB for stream-data archives. The service produces no NuGet packages of its own — it ships exclusively as a container image and is consumed via its HTTP APIs.

The GraphQL endpoint serves three top-level types per tenant:

- **ConstructionKit** — query type definitions (entities, attributes, enums, associations).
- **Runtime** — query and mutate runtime entity instances.
- **StreamData** — query time-series data for tenants that have stream types defined.

Authentication is wired through OpenID Connect / JWT bearer against the OctoMesh identity service, and tenant routing follows the platform-wide `{tenantId}/...` pattern with `allowed_tenants` claim enforcement via the shared `TenantAuthorizationMiddleware`.

## Project structure

| Project | Description |
| --- | --- |
| `src/AssetRepositoryServices` | ASP.NET Core service host (not packable). |
| `src/AssetRepositoryServices.Resources` | Localized resource strings for the service. |
| `tests/AssetRepositoryServices.UnitTests` | Unit tests. |
| `tests/AssetRepositoryServices.IntegrationTests` | Integration tests (Testcontainers MongoDB + CrateDB). |
| `tests/AssetRepositoryIntegrationTestCkModel` | CK model used by the integration tests. |

## Build

```bash
dotnet build Octo.AssetRepServices.sln
```

For local development with monorepo-built dependencies, use the `DebugL` configuration (consumes packages from `../nuget`):

```bash
dotnet build Octo.AssetRepServices.sln -c DebugL
```

## Test

```bash
dotnet test Octo.AssetRepServices.sln
```

Integration tests require Docker (Testcontainers spins up MongoDB and CrateDB).

## Run locally

```bash
dotnet run --project src/AssetRepositoryServices/AssetRepositoryServices.csproj -c DebugL
```

Configuration is environment-variable driven with the `OCTO_` prefix; see the configuration documentation linked below for the full option set.

## Documentation

The complete OctoMesh documentation is available at https://docs.meshmakers.cloud.

## License

Released under the MIT License.
