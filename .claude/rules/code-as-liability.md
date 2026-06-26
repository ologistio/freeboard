# Code is a liability

The asset is the capability delivered to users. Code is the cost paid for it.
Every line adds future burden: reading, testing, debugging, securing, migrating,
deleting. Treat AI-generated code as untrusted until integrated, tested, and
readable. Fewer lines with clear behaviour beat more lines with broad surface.

## Decision order

Before adding code, try in this order:

1. Delete obsolete code.
2. Simplify existing code.
3. Reuse an existing module, pattern, library, or platform feature.
4. Move complexity into configuration, data, or tests.
5. Only then add new code.

## Add code only when it

- is necessary for the requested capability
- is smaller than the plausible alternatives
- matches existing project patterns
- is readable without extra explanation
- has tests, or a clear reason tests are impractical
- is observable where runtime failure is possible

Justify any new file, dependency, abstraction, public API, background worker,
migration, or integration point.

## Avoid

Speculative abstractions, single-caller helpers, new deps for small
conveniences, duplicate implementations, broad rewrites without clear payoff,
code that hides failure, and TODO scaffolding for imagined requirements.

## When changing behaviour

Make the smallest coherent change. Keep the blast radius local. Update or remove
affected tests, superseded code paths, and stale comments. Do not leave two
systems doing the same job - consolidate first. Remove dead code; do not keep it
out of sentiment.

## Report

State the capability added or preserved, code added, code removed or avoided,
checks run, and any remaining liability. If the best move is not to write code,
say so and give the lower-liability alternative.
