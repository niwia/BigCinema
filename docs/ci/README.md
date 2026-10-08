# Landing the CI test job

`build-with-tests.patch` is the change to `.github/workflows/build.yml` that adds two jobs:

- `test`: runs `dotnet run --project tests/BigScreen.Tests` on Linux. The tests need no game
  references, so this costs about a second and catches the queue arithmetic that would
  otherwise only ever be wrong for one lobby at 2am.
- `build-linux`: the same build on Ubuntu. The mod has had a native Linux code path since
  libmpv landed (`libmpv.so.2` search paths, the executable-bit fix for yt-dlp), and until
  now CI was Windows-only, so nothing ever compiled those branches.

## Why this is a patch and not a commit

A personal access token without the `workflow` scope cannot create or update files under
`.github/workflows/`, on any branch and through any API - GitHub returns `404` for the
contents API and rejects the push outright. So the change is shipped here as a patch, and it
lands one of three ways:

```powershell
# CLI, anywhere you currently have the workflow scope
git checkout main
git pull
git am docs/ci/build-with-tests.patch
git push
```

Or in the browser, which needs no token changes at all: open the PR, click
**Edit this file** on `.github/workflows/build.yml`, replace its contents with the file in the
patch, and commit to the branch.

Or give the token the `workflow` scope (github.com/settings/tokens, tick *workflow*) and the
branch can simply be pushed - it is already staged in this working tree.
