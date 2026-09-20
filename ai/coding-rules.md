# AI Coding Rules

1. Inspect the existing repository before changing architecture.
2. Reuse existing patterns when they are sound; do not rewrite unrelated code.
3. Never invent infrastructure that the repository cannot run.
4. Keep domain logic separate from transport and media-provider code.
5. Do not put secrets in source code, logs, database JSON, or client bundles.
6. Prefer small, reviewable changes.
7. Every database change includes a migration.
8. Every new API write action has authorization and validation.
9. Every asynchronous job has retry/idempotency behavior defined.
10. Every new failure state is represented in tests.
11. Do not claim a feature works without running the relevant test/build commands.
12. Update documentation when behavior or contracts change.
13. Avoid speculative abstractions; add interfaces at real integration boundaries.
14. Preserve backward compatibility unless the phase explicitly authorizes a breaking change.
15. Do not silently change the Live Session state machine.
16. Media plane failures and external destination failures must not automatically terminate the core session unless the documented policy says so.
