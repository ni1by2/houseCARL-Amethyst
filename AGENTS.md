# AGENTS.md

AI coding agents must read [CLAUDE.md](CLAUDE.md) before working in this repository. It contains the operating instructions. [README.md](README.md) explains houseCARL and its build; [CONTRIBUTING.md](CONTRIBUTING.md) covers contributions.

## Amethyst mod installation rules

Here, **category** means an Amethyst divider/group.

1. **Preserve installed mods' categories and relative order.** Directly moving or reordering an
   installed mod, including within its current category, requires explaining its current and proposed
   placement and obtaining explicit user confirmation. Divider changes that alter mod grouping require
   the same confirmation. Automatic renumbering below an insertion is already authorized: report only
   the priority numbers of the mods being installed, and do not calculate or report the resulting
   index shifts for existing mods or request separate confirmation for them. Verify existing categories
   and relative order instead.
2. **Install at most ten mods per batch.** Downloads are unrestricted. Updates and replacements
   count toward the limit; keep new-install batches separate from update/replacement batches.
   After each batch, verify every mod's installation and intended behaviour. Wait for the user's
   explicit confirmation of successful in-game testing before starting the next batch.
3. **Keep each batch within one category.** Cross-category batches are allowed only for connected
   mods, such as dependencies, compatibility patches, or parts of one feature. Explain the
   connection before installing. The ten-mod limit and separation of batch types still apply.
4. **Use the earliest suitable category while building the foundation.** Place new mods in the
   earliest appropriate divider within their own area: animation foundations in `01.8`, for example,
   and base mesh fixes in `04.1`. Do not place a foundation mod in a late category merely because it
   is a fix. This does not authorize moving existing mods; each existing move still needs explicit
   user approval under rule 1.
