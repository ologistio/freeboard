# Plain ASCII punctuation

Use plain ASCII punctuation in all generated source, docs, comments, commit
messages, issues, and pull requests. Do not use Unicode where a normal
keyboard character works.

Replace:

- `-` for em/en dashes
- `->` for arrows
- `...` for ellipsis
- `"` and `'` for curly quotes
- regular spaces for non-breaking or invisible spaces

Exception: Unicode is fine when semantically required - user-facing localized
copy, fixtures that need it, or exact quotes of third-party text.
