# Upstream bulk reads — November 2026

The upstream change at `a4dc3ebee6ce2a0b2f8672eb5bb77fcd75206a0f` makes BodyGather the shared record-body access path. Source chains memoize named donors, closure walks read a bounded depth at a time, and asset walks prefetch template members and races in rounds. Faulted groups retain per-key errors; joined groups still use the narrower seek where needed. Overlay delta warnings are collected in row order after each window.

Amethyst's saved-provider selection and source checks remain authoritative. The port retains the Traits-cycle refusal before Mutagen voice resolution and the existing staging/consent/deployment rules. Record coverage remains generated from Mutagen; this change adds no record-type maps or job-specific tools.

Upstream tests cover shared faults, chunked overlay winners, post-state warning order, donor reuse and bounded template traversal. Native fixture tests separately cover Amethyst schemas 9 and 10, including rules 15.
