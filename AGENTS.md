# MarkLite — agent instructions

## Plans and sensitive information

- `plans/` is local-only and gitignored. Never commit anything under it, and
  never lift its contents into committed files verbatim.
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
