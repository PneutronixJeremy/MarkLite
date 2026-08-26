# Releasing MarkLite

Velopack builds the installer, the update packages and the portable zip; GitHub
Releases hosts them and doubles as the update feed installed copies poll.

## Machine prerequisites

- **.NET 10 SDK.**
- **`vpk`** (Velopack CLI) as a global tool:
  `dotnet tool install -g vpk`.
- **PowerShell 7** (`pwsh`) — the scripts use PS7 syntax.
- **A linker for NativeAOT.** `build/publish.ps1` encodes a VS-less setup: the
  MSVC toolset staged onto `PATH` plus the Windows SDK import libraries taken
  from the `Microsoft.Windows.SDK.CPP.x64` NuGet package. On a machine with the
  Visual Studio "Desktop development with C++" workload installed, stock
  `dotnet publish -c Release -r win-x64` works instead.
- **A GitHub token** for the upload step: `PneutronixJeremy_Github_Token`
  (fine-grained PAT, Contents read/write on this repository) or `GITHUB_TOKEN`,
  read from the environment by `build/release.ps1`.

End users need none of this: `Setup.exe` is a per-user install with no admin
prompt and no runtime install, and the portable zip runs from any folder.

## Routine release

From a clean `main` with all checks green:

1. Bump `<Version>` in `src/MarkLite/MarkLite.csproj` — the single source of
   truth for the installer, the nupkg and the update feed.
2. Write the release notes as `docs/release-notes/vX.Y.Z.md` — markdown, and
   what lands in the GitHub release body verbatim.
3. Commit (`Distribution > vX.Y.Z: …`) and `git push`.
4. `.\build\pack.ps1` — AOT publish + `vpk pack` into `releases/`. **Keep the
   previous release's files in `releases/`** so vpk can build a delta package
   against them.
5. `.\build\release.ps1` — uploads `Setup.exe`, the full and delta nupkg, the
   portable zip and the `RELEASES` manifest, creates the `vX.Y.Z` tag, and
   writes the release body from the notes file. Add `-Draft` for a release you
   want to review before it goes live. `vpk` has no notes option of its own, so
   the body is PATCHed over the GitHub API with the same token afterwards;
   `-NotesFile` points somewhere else, `-NoNotes` ships an empty body, and a
   missing notes file stops the run **before** anything is uploaded.
6. Installed copies self-update: a background check runs ~3 s after launch (or
   on demand via Help > Check for updates), the package downloads silently, and
   it applies on the next restart — "Restart to update" banner or apply-on-exit.

## Verifying an update actually shipped

Run an already-installed copy with `MARKLITE_DEBUG=1` and watch stderr: the
update lines report the version found on GitHub and whether a delta or a full
package was applied. Portable copies never auto-update by design.

## Artifacts a release should contain

| File | Purpose |
|------|---------|
| `MarkLite-win-Setup.exe` | per-user installer, no admin |
| `MarkLite-<version>-full.nupkg` | full update package |
| `MarkLite-<version>-delta.nupkg` | delta against the previous release |
| `MarkLite-win-Portable.zip` | no-install copy |
| `RELEASES` | Velopack update manifest |
