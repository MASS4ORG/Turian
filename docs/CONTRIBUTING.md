# Contributing to Turian

We are really honored for your interest! This document covers how to report issues, submit pull requests, and the licensing terms that apply to contributions.

Check the list of [Contributors](CONTRIBUTORS.md)!

## Community

Before you start, come say hi:

- **Matrix** — [#turian:matrix.org](https://matrix.to/#/!vRaFlDqBZyMXNRKDch:matrix.org)
- **Discord** — [#turian channel](https://discord.com/channels/1104509879269457982/1384499574281867274)

### Code of conduct

We follow the [Contributor Covenant Code of Conduct v2.1](https://www.contributor-covenant.org/version/2/1/code_of_conduct/).

> In short: be kind, assume good intent, keep discussion technical and respectful.

Report violations to the maintainers via the Github, Discord or Matrix.

## Coding and Reporting bugs and Features

Open an issue on [GitHub](https://github.com/MASS4ORG/Turian/issues) and include:

- Turian version (or git commit hash).
- Operating system and your .NET version.
- Steps to reproduce.
- Expected vs. actual behaviour.
- Relevant log output or a minimal reproducing project.

Label the issue with one of the existing labels (`t:bug`, `p:*`, etc.). Open an issue and use the `t:feature` label. Describe the use case — not just the proposed API — so we can discuss the design before implementation begins.

### Development Environment

- .NET 10 SDK
- Vulkan SDK (for graphics work)
- `glslc` (shader compiler)

## Submitting changes

1. **Fork** the repository and create a feature branch from `main`.
2. Build with the .NET 10 SDK and run the test suite before pushing:
   `dotnet run --project Turian.Tests/Turian.Tests.csproj`. The build must stay free of warnings.
3. Keep commits small and focused; use [Conventional Commits](https://www.conventionalcommits.org/)
(`feat:`, `fix:`, `docs:`, `refactor:`, etc.).
4. Open the pull request against `main` with a clear description of *what* and *why*.
5. Link any related issues with `Closes #N` in the description.

CI (`.github/workflows/ci.yml`: build, shaders and unit tests) must pass before a pull request can be merged.

## Licensing

Turian is licensed under the [Mozilla Public License 2.0](../LICENSE.md). Contributions are accepted under the terms below.

## Contribution license

By submitting any contribution (including code, documentation, or assets) to this repository, you agree to the following terms:

1. You license your contribution under the project's primary outbound license ([Mozilla Public License 2.0](../LICENSE.md)).

2. You grant the project maintainer(s) a perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable copyright license to distribute, adapt, dual-license, or relicense your contributions under any alternative terms at their sole discretion.

## Sponsoring the project

If Turian saves you time, consider supporting the project financially:

* [Patreon](https://www.patreon.com/c/MASS4)
* [Ko-fi](https://ko-fi.com/mass4)
