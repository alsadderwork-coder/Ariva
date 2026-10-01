---
name: aman-conventions
description: Ariva's C# conventions inherited from AMAN (services, Result<T>, validation, controllers, permissions, entities, caching, logging, tests). Load before writing any backend code.
---
# AMAN conventions in Ariva

Source: AMAN's CLAUDE.md and code in ../Aman (read-only), adapted. Where AMAN's CLAUDE.md is stale (it says Rebus; AMAN uses MassTransit with the Kafka Rider), Ariva follows its ADRs (Confluent.Kafka behind ISvcMessageBus).

## Language and style
- .NET 10, C# 14: primary constructors, collection expressions, raw string literals, pattern matching, records for DTOs, file-scoped namespaces in new files.
- Nullable is disabled globally (as in AMAN): explicit guard clauses, `??`, no `!`.
- Early returns; expression-bodied members for one-liners; `var` when obvious; LINQ method syntax; interpolation over concatenation (never for SQL); pass `CancellationToken` through every async chain.
- One class per file; `#region` blocks; member order: fields, constructors, properties, methods.
- Interfaces `I*`, services `Svc*` (`ISvcZoneProfile`, `SvcZoneProfile`).

## Results and validation
- Services return `Result<T>` / `Result` from FluentX: `Result.Return(data)`, `Result.Error<T>(message)`. No exceptions for expected failures; no try/catch except at boundaries (global handler exists).
- Validate with `Fx.Specification<TRequest>().And(rule, Validations.X)...ValidateAllAsync(request)` before touching entities. Messages come from `Ariva.Resources` (`Validation.resx`, `Messages.resx`, English and Arabic).

## Service shape
```csharp
internal class SvcZoneProfile(IUnitOfWork uow, IFusionCache cache, ISvcValidation validation, ICurrentUser currentUser, ISiteScope siteScope)
    : SvcBase(uow, cache, validation, currentUser), ISvcZoneProfile
```
Flow: Create = validate, load related with `ToFuture`, check existence, `Entity.Create(...)`, save, publish domain events. Update = validate, load tracked, rich method, save, `Cache.RemoveByTagAsync(nameof(Entity))`. Get = `Cache.GetOrSetAsync(key, ..., tags: [nameof(Entity)])`, `QueryAsNoTracking`, map with Mapster. Search = criteria, `ToFuture` count plus page, `PagedData<T>`.

## Controllers
```csharp
[ApiController, Route("AdminArea/ZoneProfile")]
public class Controller(ISvcZoneProfile svc) : ControllerBase
{
    [HttpPost, Permission(nameof(Permissions.CreateZoneProfile))]
    public Task<Result<Guid>> Create([FromBody] CreateZoneProfileRequest request, CancellationToken ctx) => svc.Create(request, ctx);
}
```
Folder per entity, file `Controller.cs`, class `Controller`. Sub-features in subfolders. `.http` files under the host's `Http/<Entity>/`.

## Permissions
`Global.Defaults.Permissions` in `Ariva.Core/Global.cs`: View, Create, Edit, Search, Delete per entity, plus site-scoped variants. Roles: BorderShiftSupervisor, TerminalDutyManager, HandlerStationManager, SystemAdministrator (`RoleCodes.cs`). Role to permission mapping is data (seeded), mirrored in `security/permission-matrix.json` for tests. Never authorize by role strings.

## Entities
Rich methods only (`profile.Publish(publishedBy, now)`), never property assignment from outside. `IAuditable` fields set by listeners; `ISoftDeletable` where history matters (not for time series). UTC everywhere (`TimeProvider`).

## View models and inputs
`*CompactViewModel`, `*DetailsViewModel`, `*WithRelationsViewModel`; `Create*Request`, `Update*Request`; `Criteria*` for search.

## Caching and logging
FusionCache with Redis backplane and tags per entity. Serilog structured logging with named properties; never log tokens, secrets, TOTP codes or `access_token` query strings.

## Configuration
`appsettings.base.json` and `appsettings.base.<env>.json` in Ariva.Api.Common (linked into every host), `appsettings.service.<env>.json` per host, `environment.json`; environments vm-local, k8s-dev, k8s-demo, k8s-prd; `${placeholders}` resolved from secrets.

## Tests
`MethodName_Should_ExpectedResult_When_Condition`; Arrange, Act, Assert; Bogus fakers per request type; xUnit v3, FluentAssertions, Moq.
