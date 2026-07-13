# Comment etiquette

Comments are a liability like code: they must be read, trusted, and kept in sync.
Write few, and make each one earn its place. Follow `plain-simple-english.md` -
the same brevity and directness apply to comments.

## Comment the why, not the what

Good comments explain intent, constraints, and non-obvious reasoning: why this
approach, why this order, what breaks if you change it, a tricky edge case, a
security or concurrency invariant, a deliberate deviation from the obvious.

Do not narrate what the code already says. If the code is clear, no comment is
the right amount of comment.

```csharp
// Bad - restates the code
// Increment the counter
counter++;

// Bad - obvious from the signature
// This method gets the user by id
public User GetUser(string id)

// Good - explains a non-obvious why
// HMAC, not a fast hash: the token is high-entropy so we skip a slow KDF, but a
// keyed MAC stops a database dump from being turned into a lookup table.
```

## Prevent over-commenting and over-verbosity

- Prefer self-explanatory names and small functions over comments that
  compensate for unclear code. Fix the code first.
- No redundant comments, no commented-out code, no decorative banners or section
  dividers, no per-line narration. Use `#region`/`#endregion` preprocessor
  directives to group related members in large files instead of comment
  separators.
- No changelog/history in comments ("changed X to Y", "previously did Z",
  "added for ...") - that is what version control is for.
- One clear sentence beats a paragraph. Cut hedging and filler. Do not repeat in
  a comment what an adjacent comment or the XML doc already says.
- Keep doc comments (`///`) to a crisp summary plus genuinely useful params/
  returns/exceptions. Do not pad them to look thorough.
- A comment that would go stale the moment the code changes usually should not
  exist; encode the rule in a test or an assertion instead.

## Keep them true

If you change code, update or delete the comments it invalidates. A wrong
comment is worse than none. Do not leave a comment referring to something that
no longer exists.

See also `no-process-references.md`: never reference planning/review/agent
workflow internals (finding ids, task numbers, OpenSpec) in comments.
