---
description: Scaffold a configuration entity end to end in AMAN style (entity, mapping, service, controller, permissions, resources, http file, tests)
argument-hint: <EntityName> <one-line purpose>
---
Create the entity $ARGUMENTS following the aman-conventions skill and ../Aman as reference (read-only):

1. Ariva.Core/Domain/Entities/<Entity>.cs with rich methods and invariants; Criteria, InputModels (Create, Update requests with length limits), ViewModels (Compact, Details).
2. Permissions in Ariva.Core/Global.cs (View, Create, Edit, Search, Delete) and their mapping to roles in the permission seed; add rows to security/permission-matrix.json.
3. ISvc<Entity> in Ariva.Core/Services and Svc<Entity> in Ariva.Infra/Services (Result<T>, Fx.Specification, FusionCache tags, ISiteScope when site-bound).
4. NHibernate mapping through the convention mapper (and explicit rules where needed).
5. Ariva.Api.Main/Controllers/AdminArea/<Entity>/Controller.cs with [Permission] on every action; Http/<Entity>/CRUD.http.
6. Validation and message strings in Ariva.Resources (English and Arabic).
7. Tests: unit (entity invariants, service paths), integration (mapping round-trip), E2E API (CRUD, 401, 403 per role, cross-site 403, attack payloads).
Then run `node scripts/verify.mjs backend` and the E2E API project.
