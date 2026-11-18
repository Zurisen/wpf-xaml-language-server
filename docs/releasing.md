# Releasing

Releases are published by [.github/workflows/release.yml](../.github/workflows/release.yml) when a version tag is
pushed. It builds, tests and packs the `wpf-xaml-ls` tool, publishes it to nuget.org, and creates a GitHub release
with the package and that version's changelog section.

## One-time setup (maintainer)

1. Sign in to [nuget.org](https://www.nuget.org) with the account that will own the `WpfXaml.LanguageServer` package.
2. Under your username, choose **Trusted Publishing** and add a policy:
   - **Repository Owner:** `Zurisen`
   - **Repository:** `wpf-xaml-language-server`
   - **Workflow File:** `release.yml`
   - **Environment:** leave empty.

   No API key is stored anywhere: the workflow exchanges GitHub's OIDC token for a one-hour key at publish time.
3. In the GitHub repository, add the secret `NUGET_USER` (Settings → Secrets and variables → Actions) with your
   nuget.org **profile name**, not your email address.

## Releasing a version

1. In [CHANGELOG.md](../CHANGELOG.md), rename `## [Unreleased]` to `## [<version>] - <date>` and add a new, empty
   `## [Unreleased]` above it. The workflow uses that section as the release notes, and fails if it's missing.
2. Merge that change to `master`.
3. Tag the merge commit and push the tag:

   ```bash
   git tag v<version>
   ```

   ```bash
   git push origin v<version>
   ```

Versions with a suffix (`0.1.0-alpha`, `0.2.0-beta.1`) are published as prereleases. Users install them with
`--prerelease`.

## Dry run

Run the **Release** workflow manually from the Actions tab and enter a version. It builds, tests and packs, then
uploads the package as an artifact. Nothing is published.
