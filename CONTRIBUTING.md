# Contributing

## Development workflow

1. Create a focused branch from the current default branch.
2. Keep credentials and customer data out of source, tests, logs, screenshots, and commits.
3. Add or update tests for behavior changes.
4. Run the desktop build, service tests, and relevant smoke tests locally.
5. Open a pull request describing behavior, risk, verification, and rollback considerations.

Pull requests must pass CI, security scanning, dependency review, and human review before merge. Production releases must be generated from a reviewed tag rather than a developer workstation.
