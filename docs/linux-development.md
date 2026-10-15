# Linux / Amethyst development

The source build and CI target Ubuntu 24.04 x64. Amethyst and its native filegraph must match the tested API 13 / schema 9 contract. CI builds the filegraph from the pinned Amethyst source revision.

## Requirements

- Linux x64, .NET 9 SDK and Python 3.12 or newer.
- Git, Rust/Cargo, a C compiler and the normal Rust build prerequisites for Amethyst's filegraph.
- To build Caprica and Champollion from source: Clang/Clang++, CMake, Ninja, curl, zip, unzip and tar.
- Claude Code or OpenAI Codex to run the MCP server.

## Build the Amethyst native component

The native module is built from Amethyst commit `edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92`. The upstream build leaves the resulting extension in its source checkout; it does not change a Skyrim profile.

```sh
git clone https://github.com/ChrisDKN/Amethyst-Mod-Manager.git ../Amethyst-Mod-Manager
git -C ../Amethyst-Mod-Manager checkout edde06e518c0a3ca5ba47a8bbf065c4ecc4a8f92
(cd ../Amethyst-Mod-Manager && native/amethyst_filegraph/build.sh)
```

## Build and test this source tree

Build the native Papyrus tools from the pinned source commits first. The five Caprica tests use the resulting binary; an absent compiler is reported as a skip.

```sh
python3 scripts/build-linux-tools.py
dotnet build housecarl.sln -c Release
AMETHYST_FILEGRAPH_EXTENSION="../Amethyst-Mod-Manager/src/amethyst_filegraph.abi3.so" dotnet test src/housecarl-mcp-tests -c Release --no-build
python3 -m unittest discover -s scripts/tests -v
```

The C# suite builds saved-snapshot fixtures with Amethyst's native producer, using the extension at `AMETHYST_FILEGRAPH_EXTENSION`. Installer and package checks run in isolated temporary homes and leave the current host registration unchanged.

## Assemble a Linux package candidate

The release recipe targets Ubuntu 24.04 x64 and requires Python 3.12+, the native build tools above, and the Amethyst extension built from the pinned source. Run `scripts/build-linux-package.sh` from the repository root with `AMETHYST_FILEGRAPH_EXTENSION` set to the built module. Package installation, upgrade, rollback, uninstall and selected-Proton acceptance are separate release checks; passing the source CI does not claim those checks have run.

The current Arch-derived development build is useful for development and tests, but its glibc 2.44 native binaries cannot be used for the Ubuntu 24.04 package. Build the distributable native tools on Ubuntu 24.04 x64, where the glibc baseline is 2.39.
