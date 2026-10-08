# Project Instructions

## Branching and merging

- `master` is protected. Never commit or push directly to `master`, and never bypass the protection, even if you have admin rights.
- Every change goes through a pull request: create a feature branch from the latest `origin/master`, push it, and open a PR.
- Open PRs against `frizat82/peloton-to-garmin` (base `master`), never the upstream `philosowaffle/peloton-to-garmin` repo.
- Merge only after the PR Check ("Build and Test the code") passes.
- Merging to `master` publishes the `*-latest` Docker images. `*-stable` images are built only by the manual `publish-release.yaml` workflow.
