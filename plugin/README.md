# houseCARL-Amethyst

houseCARL-Amethyst gives Claude Code or Codex data-layer access to an Amethyst-managed Skyrim Special Edition load order. It reads the manager's saved profile snapshot and filegraph. New patches go to staging for review and deployment through Amethyst; editing an existing plugin requires explicit consent.

The server uses .NET 10 and exposes 34 MCP tools over stdio. Record coverage is generated from Mutagen's model. The [repository README](https://github.com/ni1by2/houseCARL-Amethyst#readme) describes the tools, skills, record operations and Asset Walk.

## Install or upgrade on Linux

Use the [Linux installation guide](https://github.com/ni1by2/houseCARL-Amethyst/blob/amethyst-main/docs/linux-install.md). Download the archive, checksum and standalone installer from [2.1.4 — Linux preview](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.1.4). Linux x64 and Python 3.12 or newer are required; the guide lists the operating-system libraries.

The Linux archive includes the .NET 10 and ASP.NET Core runtimes. You do not need to install a system .NET runtime or SDK to run it.

Quit every Claude Code, Codex or other MCP host running houseCARL, and keep a separate terminal open. From the download directory, run as your normal user:

```sh
sha256sum -c housecarl-amethyst-linux-x64.tar.gz.sha256 &&
  python3 install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
```

The `install` action and `--archive` argument are required. Reopen the host afterward and verify the server version and Amethyst connection. Existing connection settings are retained. A first installation needs an explicit saved-snapshot selection through `housecarl_set_amethyst_connection`; see the guide. Installation does not enable, reorder or deploy mods.

The installer retains the previous owned package for `python3 install-linux.py rollback`. Pending transaction journals block version changes and must be recovered through the supported workflow; preserve them if you need recovery help.

## Build from source

Source builds require the **.NET 10 SDK, 10.0.400 or newer**, Python 3.12+, and the Rust/C/C++ tools listed in the [Linux development guide](https://github.com/ni1by2/houseCARL-Amethyst/blob/amethyst-main/docs/linux-development.md). `global.json` controls SDK selection. Build all four pinned Amethyst native readers before packaging. The package supports schema 9/rules 13, schema 10/rules 14, 15, 18 and 19; other producer contracts refuse. Rules 18 reuses the rules-15 reader; rules 19 requires its matching reader.

The retained Windows framework-dependent build requires the x64 .NET 10 Runtime and ASP.NET Core 10 Runtime; its build also needs the .NET 10 SDK and PowerShell. It is not the supported Linux preview package, and Windows execution has not been validated by this fork.

## Using the saved profile

Responses identify the saved profile and generation and warn that changes since Amethyst's last Refresh may be missing. If the snapshot is stale, use **Refresh Modlist** in Amethyst and retry the read. Deployment does not replace Refresh.

Review staged writes, then Refresh, enable and deploy through Amethyst. A later houseCARL deployment check verifies which file wins in the game. Staging success alone does not establish game visibility.

## Test a newer Amethyst version

Use the [compatibility test guide](https://github.com/ni1by2/houseCARL-Amethyst/blob/v2.1.4/docs/amethyst-compatibility-testing.md) for GitHub Actions or local synthetic-profile comparisons. This evaluates new upstream revisions without opening your live modlist. Reports separate native-reader results from runtime admission and never automatically approve a new contract.

## Licence

houseCARL-Amethyst inherits GPL-3.0-only from the [original houseCARL project](https://github.com/Avick3110/houseCARL). Upstream houseCARL uses and bundles Mutagen and other GPL-3.0-only libraries without a linking exception, so the combined program must be distributed under GPL version 3. The bundled Amethyst filegraph is GPL-3.0-or-later and is distributed with this program under version 3. Caprica retains its MIT licence. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for the licence texts, component notices, and source links.

If you distribute houseCARL-Amethyst or a modified version, keep the copyright and licence notices, identify your changes, and license the covered combined work under GPL-3.0-only. Distributing binaries also requires providing their Corresponding Source in a form permitted by GPLv3, including modified source and the scripts needed to build and install it. Separate bundled components retain their own licence notices. These obligations are explained in [GPLv3, sections 4 to 6](https://www.gnu.org/licenses/gpl.en.html).

You can run the program and make private changes without publishing your source. Using it does not automatically place your mods or generated patches under the GPL; output is covered only when its contents themselves form a covered work. Any source material used in a patch still carries its own permissions and licence requirements. See the [GNU licence FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#GPLOutput).

## Credits

- [Amethyst Mod Manager](https://github.com/ChrisDKN/Amethyst-Mod-Manager) by ChrisDKN and contributors, whose manager and native filegraph provide the profile and asset view used by this fork.
- [Original houseCARL](https://github.com/Avick3110/houseCARL) by Avick3110 and contributors, the project this Amethyst integration builds on.
- [Caprica](https://github.com/Styyx1/Caprica), originally by Orvid King, with the fork used here maintained by Styyx1. It provides the native Papyrus compiler.
- [Mutagen](https://github.com/Mutagen-Modding/Mutagen), Noggog. The Bethesda-format library houseCARL is built on.
- [papyrus-index](https://github.com/BellCubeDev/papyrus-index), BellCube. Source corpus for `papyrus-reference`.
- Zzyxzz (SkyPatcher) and powerofthree (SPID, KID). Public documentation the distributor grammars were drawn from.
- DrHeisen. The `open-animation-replacer` skill, and two earlier skills since retired.

The grammar, the predicate language, coverage, the runtime layers and the internals are in the [repository README](https://github.com/ni1by2/houseCARL-Amethyst#readme).
