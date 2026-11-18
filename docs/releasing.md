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

## VS Code extension

The extension in `editors/vscode` is released separately by
[.github/workflows/release-vscode.yml](../.github/workflows/release-vscode.yml). The workflow packages the extension
and creates a GitHub release with the `.vsix`. If a registry's token secret is configured, it also publishes there.

One-time setup:

1. **VS Code Marketplace:**
   - Create the publisher `zurisen` (or change `publisher` in `editors/vscode/package.json` to yours) at
     <https://marketplace.visualstudio.com/manage>.
   - Create an Azure DevOps personal access token with the **Marketplace (Manage)** scope.
   - Store it as the repository secret `VSCE_PAT`.
2. **Open VSX** (used by VSCodium, Cursor and other editors based on VS Code):
   - Create an access token at <https://open-vsx.org>.
   - Create the namespace once: `npx ovsx create-namespace zurisen -p <token>`.
   - Store the token as `OVSX_PAT`.

To release, set `version` in `editors/vscode/package.json`, add an entry to `editors/vscode/CHANGELOG.md`, merge, then
tag with the same version:

```bash
git tag vscode-v<version>
```

```bash
git push origin vscode-v<version>
```

The workflow fails if the tag and `package.json` disagree.

## Dry run

Run the **Release** workflow manually from the Actions tab and enter a version. It builds, tests and packs, then
uploads the package as an artifact. Nothing is published.
