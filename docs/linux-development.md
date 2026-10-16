# Linux / Amethyst development

The source build and CI target Ubuntu 24.04 x64. Amethyst and its native filegraph must match the tested API 13 / schema 9 contract. CI builds the filegraph from the pinned Amethyst source revision.

For the published 2.0.5 Linux preview, use the [installation guide](linux-install.md). The [release verification record](testing/upstream-205-integration-2026-10-15.md) lists completed checks and distinguishes them from earlier candidate runs. The commands below build a new candidate from your checkout.

## Requirements

- Linux x64, .NET 9 SDK and Python 3.12 or newer.
- Git, Rust/Cargo, a C compiler and the normal Rust build prerequisites for Amethyst's filegraph.
- To build Caprica and Champollion from source: Clang/Clang++, Clang's dependency scanner (`clang-tools-18` on Ubuntu 24.04), CMake, Ninja, curl, zip, unzip and tar.
- Claude Code or OpenAI Codex to run the MCP server.

## Build the Amethyst native component

The native module is built from Amethyst commit `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92`. The upstream build leaves the resulting extension in its source checkout; it does not change a Skyrim profile.

```sh
git clone https://github.com/ChrisDKN/Amethyst-Mod-Manager.git ../Amethyst-Mod-Manager
git -C ../Amethyst-Mod-Manager checkout edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92
(cd ../Amethyst-Mod-Manager && native/amethyst_filegraph/build.sh)
```

## Create a connection manifest from source

The standalone setup command retains native and Flatpak discovery. It creates a schema-v1 connection manifest without installing the package, registering an MCP host, changing a profile, or deploying mods:

```sh
dotnet run --project src/housecarl-setup -- --connect-only
```

If more than one Amethyst configuration is found, add `--amethyst-config /absolute/path/to/AmethystModManager` to select one explicitly. This checks connection paths only. The server checks filegraph compatibility when you select the printed manifest with `housecarl_set_amethyst_connection` and `filegraph_mode="saved-snapshot"`.

## Build and test this source tree

Build the native Papyrus tools from the pinned source commits first. The five Caprica tests use the resulting binary; an absent compiler is reported as a skip.

```sh
python3 scripts/build-linux-tools.py
dotnet build housecarl.sln -c Release
AMETHYST_FILEGRAPH_EXTENSION="../Amethyst-Mod-Manager/src/amethyst_filegraph.abi3.so" dotnet test src/housecarl-mcp-tests -c Release --no-build
python3 -m unittest discover -s scripts/tests -v
```

The C# suite builds saved-snapshot fixtures with Amethyst's native producer, using the extension at `AMETHYST_FILEGRAPH_EXTENSION`. Installer and package checks run in isolated temporary homes and leave the current host registration unchanged.

The C# test executor sets `XDG_STATE_HOME` to a private temporary directory before running tests. Each default MCP server fixture also gets its own state directory; fixtures can explicitly select a different temporary root. This keeps unfinished synthetic transactions out of the developer's real state directory, where they would block package installation. Cleanup runs when the owning fixture is disposed or the test process exits normally.

## Assemble a Linux package candidate

The release recipe targets Ubuntu 24.04 x64 and requires Python 3.12+, the native build tools above, and the Amethyst extension built from the pinned source. Run `scripts/build-linux-package.sh` from the repository root with `AMETHYST_FILEGRAPH_EXTENSION` set to the built module. Package installation, upgrade, rollback, uninstall and selected-Proton acceptance are separate release checks; passing the source CI does not claim those checks have run.

The current Arch-derived development build is useful for development and tests, but its glibc 2.44 native binaries cannot be used for the Ubuntu 24.04 package. Build the distributable native tools on Ubuntu 24.04 x64, where the glibc baseline is 2.39.

After assembling the archive, check the actual packaged server:

```sh
python3 scripts/probes/check_linux_package.py dist/housecarl-amethyst-linux-x64.tar.gz
```

This validates the payload hashes, extracts it to a temporary directory, and checks MCP initialization, package version and the published tool list. It uses temporary server configuration and does not register hosts or connect to Amethyst. Add `--installed-prefix /path/to/install` to check an existing installation's active version against the same archive. Installation itself also changes host registrations in the current user's home; custom `--prefix` and `--state-root` paths alone do not isolate those registrations. Use a disposable user or container for installation tests.
