# Deviations only where the Official CLI fails

The Official CLI has real bugs. For example, its Item file parser breaks on whitespace that editors routinely trim. Being Drop-in means we cannot fix them by changing results. The rule:

- Wherever the Official CLI succeeds, this tool produces the identical result.
- This tool may succeed where the Official CLI fails.
- This tool never writes anything the Official CLI cannot read.

Every Deviation is listed in `docs/deviations.md` with a differential test that proves it.

Some examples allowed under this rule:

- reading Item files the official parser rejects
- Pull without `allowWrite`
- never re-saving `sitecore.json` when a token refreshes
- refreshing tokens before they expire

Writing a "nicer" Item file format is not allowed.
