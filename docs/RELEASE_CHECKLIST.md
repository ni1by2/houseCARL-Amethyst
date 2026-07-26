# v1 release and rollback checklist

This checklist is the release gate for a Linux x86_64 houseCARL-Amethyst v1
candidate. Do not mark the roadmap complete from synthetic evidence alone.

## Source and metadata

- [ ] The working tree is clean and the candidate commit is pushed.
- [ ] The candidate deliberately includes or declines current upstream
  houseCARL and Amethyst compatibility changes.
- [ ] `plugin/.claude-plugin/plugin.json`, changelog, README, notices, and
  roadmap agree on the version and supported scope.
- [ ] No connector repository or Amethyst plugin is described as required.
- [ ] PapyrusCompiler and BSArch execution are described as post-v1.

## Automated Linux gates

- [ ] `dotnet restore` succeeds with .NET 9.
- [ ] `dotnet build housecarl.sln --configuration Release` completes with no
  warnings or errors.
- [ ] `ci-all` passes all 108 registered CI probes.
- [ ] The cold `freshness-capture-guard` passes all five arms.
- [ ] GitHub's Ubuntu workflow passes build, probes, cold freshness, package
  creation, checksum checks, ELF architecture inspection, and `.exe`
  exclusion.
- [ ] Two release builds from the same commit produce the same archive SHA-256.
- [ ] The archive contains no developer paths, private notes, Windows installer
  assets, or untracked files.

## Clean installation

- [ ] Install succeeds without system .NET in a clean Linux x86_64 environment.
- [ ] Codex starts the stdio server.
- [ ] Claude Code starts the same installed stdio server.
- [ ] Native/AppImage/AUR-style discovery writes a valid schema-v1 manifest.
- [ ] Flatpak discovery writes a valid schema-v1 manifest.
- [ ] Upgrade preserves the connection, consent, and pending-redeployment
  configuration.
- [ ] Rollback restores the prior server and both hosts still start it.
- [ ] Uninstall removes only houseCARL files/registrations and preserves XDG
  configuration.

## Disposable real hardlink profile

Use a disposable Amethyst profile and a harmless synthetic plugin. Preserve a
known-good profile and game backup before this gate.

- [ ] `housecarl_amethyst_status` reports the intended active profile, shared or
  profile-specific staging, hardlink deployment, `Data_Core`, filemap, and mod
  index.
- [ ] A read resolves the same plugin winner and loose-file winner as Amethyst.
- [ ] A normal patch is written under staging, appears after refresh, can be
  enabled, enters the rebuilt filemap, and deploys.
- [ ] The deployed patch content matches staging after deployment.
- [ ] A guarded in-place edit refuses without `in_place=true`.
- [ ] It refuses without the per-plugin consent handshake.
- [ ] It refuses without `confirm_amethyst_redeploy=true`.
- [ ] Before replacement, staging and deployed copies are confirmed as the same
  hardlink.
- [ ] After atomic staging replacement, deployed `Data` retains the old inode
  and houseCARL reports pending redeployment honestly.
- [ ] After Amethyst refresh, filemap rebuild, and redeploy, deployed content or
  hardlink identity matches the new staging file and pending state clears.
- [ ] No operation writes through deployed `Data` or edits Amethyst-owned order
  and filemap files.

## Opportunistic deployment modes

- [ ] Symlink smoke testing needs no production mode-specific branch before it
  is documented as supported.
- [ ] Copy-mode smoke testing verifies content rather than inode identity and
  remains non-gating for v1.
- [ ] No VFS support is claimed.

## Release and rollback

- [ ] Create the signed or annotated `v1.0.0` tag from the verified commit.
- [ ] Publish the archive and matching `.sha256` file without rebuilding.
- [ ] Record the hosted workflow URL, release archive hash, Amethyst version,
  profile mode, and real hardlink evidence in the roadmap.
- [ ] Keep the previous release available until the new release has passed an
  install, update, and rollback smoke test.
- [ ] If a gate fails, withdraw the candidate, document the failure, and direct
  users to `--rollback`; never relabel the same binary as fixed.
