# Installation and updates

The current release is [v2.0.5 — Linux preview](https://github.com/ni1by2/houseCARL-Amethyst/releases/tag/v2.0.5), a Linux x64 prerelease. Use the [Linux installation guide](linux-install.md) for runtime requirements, connection setup and release limits.

## Install a release

1. Install the [runtime prerequisites](linux-install.md#runtime-requirements), including Python 3.12 or newer. The package includes the .NET runtime; running it does not require the SDK or build tools.
2. Download `housecarl-amethyst-linux-x64.tar.gz`, `housecarl-amethyst-linux-x64.tar.gz.sha256` and `install-linux.py` from the release page into the same directory.
3. From that directory, verify the archive:

   ```sh
   sha256sum -c housecarl-amethyst-linux-x64.tar.gz.sha256
   ```

4. If verification reports `OK`, run the installer as your normal user:

   ```sh
   python3 install-linux.py install --archive housecarl-amethyst-linux-x64.tar.gz
   ```

The `install` action and `--archive` argument are required. Running `python3 install-linux.py` alone exits with a missing-action error and does not install anything. Use the downloaded script and archive directly; extraction is handled by the installer.

The installer registers Claude Code and Codex. Restart your host and [select an Amethyst saved-snapshot connection](linux-install.md#select-an-amethyst-connection). Installation does not select, enable or deploy a Skyrim profile.

## Update, rollback and uninstall

Use the [current installer instructions](linux-install.md#install-update-and-remove) for version activation, updates, rollback and uninstall. Configuration and transaction recovery state live outside the program directory. Pending or incompatible transaction journals block version changes.

The former `housecarl-amethyst-install --codex`, `--claude` and `--connect-amethyst` commands belonged to an older build and do not apply to the downloadable 2.0.5 package. For source builds, use the [Linux development guide](linux-development.md).
