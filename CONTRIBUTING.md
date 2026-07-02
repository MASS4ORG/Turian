# Contributing to Turian Engine

Thank you for your interest in Turian! This document covers how to report issues, submit merge requests, and the licensing terms that apply to contributions.

---

## Community

Before you start, come say hi:

- **Discord** — [#turian channel](https://discord.com/channels/1104509879269457982/1384499574281867274)
- **Matrix** — [#turian:matrix.org](https://matrix.to/#/!vRaFlDqBZyMXNRKDch:matrix.org)

These are good places to discuss feature ideas and get quick answers before opening a formal issue.

---

## Contribution license

> **Important:** by submitting any contribution (code, documentation, assets, or other content) to this repository, you agree to license your contribution under the **MIT License**.

This keeps the contributor side simple and permissive. The engine as a whole remains GPLv3 for distribution purposes; the individual contributions you make are MIT so that they can be reused freely.

---

## Code of conduct

We follow the [Contributor Covenant Code of Conduct v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).
In short: be kind, assume good intent, keep discussion technical and respectful.
Violations can be reported to the maintainers via the GitLab project or the Discord.

---

## Reporting bugs

Open an issue on [GitLab](https://gitlab.com/mass4org/mega4/turian/-/issues) and include:

- Turian version (or git commit hash).
- Operating system and Zig version.
- Steps to reproduce.
- Expected vs. actual behaviour.
- Relevant log output or a minimal reproducing project.

Label the issue with one of the existing labels (`t:bug`, `p:*`, etc.).

---

## Suggesting features

Open an issue and use the `t:feature` label. Describe the use case — not just the proposed API — so we can discuss the design before implementation begins. Check the existing [milestones](https://gitlab.com/mass4org/mega4/turian/-/milestones) first; your feature may already be planned.

---

## Submitting a merge request

1. **Fork** the repository and create a feature branch from `main`.
2. Run the formatter and tests before pushing:
   ```bash
   zig fmt engine/ editor/ studio/ subsystems/ tools/ examples/ build.zig
   zig build test
   ```
3. Keep commits small and focused; use [Conventional Commits](https://www.conventionalcommits.org/)
   (`feat:`, `fix:`, `docs:`, `refactor:`, etc.).
4. Open the MR against `main` with a clear description of *what* and *why*.
5. Link any related issues with `Closes #N` in the description.

CI must pass (format check + unit tests + example builds) before a MR can be merged. See [docs/ci.md](docs/ci.md) for how the pipeline itself works (the CI image, caching, and the one-time bootstrap a Dockerfile change needs).

---

## Development setup

See [docs/install.md](docs/install.md) for the full developer setup guide.

---

## Sponsoring the project

If Turian saves you time, consider supporting the project financially:

- [Patreon](https://www.patreon.com/c/MASS4)
- [Ko-fi](https://ko-fi.com/mass4)
