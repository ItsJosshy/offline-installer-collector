# InstallerVault implementation plan

Goal: a usable authenticated installer archive with persistent library state and jobs.
Architecture: modular ASP.NET Core 10 monolith, SQLite EF migrations, React/TypeScript/Vite static frontend. Never execute installers. All server filesystem and HTTP access crosses guards.
Spec: docs/requirements.txt (the user's full requirements).

## Tasks
- [ ] Foundation: entities, EF migrations, safe filesystem browser/scanner, authentication and CSRF. Tests exercise traversal, links, repeated scans, missing files, login/setup and download authorization.
- [ ] Providers: GitHub/direct/Homebrew/WinGet implementations against guarded HTTP; deterministic parsing tests. Metadata previews are required before tracking assets; ambiguous metadata stays unknown.
- [ ] Jobs: persistent queue, immutable library destination, streaming staging/checksum/atomic publication, cancellation/recovery/retention, schedule and SignalR. Test failure preservation, duplicate jobs, pinning and unmanaged exclusions.
- [ ] Frontend: real API state, secure setup/login, server folder picker, dashboard/library/discovery/jobs/settings and review workflow, responsive dark/light design and accessible dialogs. Build and browser tests.
- [ ] Delivery: production Docker/Compose, native Windows instructions, capability matrix, backup/import/export, source archive. Execute backend/frontend tests and available Docker checks; record limits honestly.

## Shared contracts
See src/InstallerVault/Domain.cs and docs/api-contract.md. Foundation owns Program.cs and Api.cs; providers own Providers/; jobs own Jobs/; UI owns frontend/. No shared-file editing by agents.
Network interface: ISafeHttp.SendAsync(HttpRequestMessage,CancellationToken) returns response with streaming content; caller disposes it. Guard all redirects and connect to validated IP addresses.
Filesystem interface: PathGuard.Validate(path, mustExist=true), ForLibrary(Library,relativePath,mustExist=true). Reject all reparse points, including ancestors. Library sentinel identity prevents writing to replaced empty mounts.

Ruling: proceed without optional approval checkpoints per explicit user instruction. Work in the clean supplied checkout on codex/installervault. Independent provider and frontend boundaries permit parallel work under dispatching-parallel-agents; integrate and review centrally.
