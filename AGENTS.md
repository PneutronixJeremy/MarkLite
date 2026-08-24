# MarkLite — agent instructions

## Plans and sensitive information

- `plans/` is local-only and gitignored, with ONE exception:
  `plans/marklite-native-markdown-viewer.md` is committed (user decision
  2026-08-24) and must therefore stay scrub-clean at all times — every edit to
  it follows the sensitive-information rules below. `plans/reference/` and
  everything else under `plans/` stays untracked; the committed plan may
  mention those files but readers of the public repo cannot see them.
- Plan files, and any file that might ever be committed, must not contain
  sensitive or machine-/user-identifying information: no absolute local paths
  (beyond what a build script functionally requires), no personal details or
  habits, no private email addresses, no credentials or tokens, no screenshots
  showing personal content, window layouts, or other applications.
- Assume everything committed to this repo becomes public on GitHub
  (https://github.com/PneutronixJeremy/MarkLite).
- Screenshots for docs come only from the fixtures in `testdata/` (fictional
  content, written for this purpose).

## Committing

- Author/committer email for this repo is the GitHub noreply address
  (set in local git config) — do not change it back to a personal address.
- Commit subjects: the whole repo is MarkLite, so drop the `MarkLite > `
  prefix — breadcrumbs start at the area (e.g. `Distribution > 11: …`,
  `Docs > …`), still in the user's `Area > Subarea > Description. [w/ Claude]`
  format (user decision 2026-08-24).
