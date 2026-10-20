# Drop-in means byte-identical, proven against the Official CLI

The tool is a Drop-in replacement across six layers:

1. `sitecore.json` and Module files are read the same way.
2. Pull writes byte-identical Item files.
3. Commands, options, short flags and exit codes are the same.
4. The Official CLI's login store is shared.
5. An optional compatibility package installs the command name `sitecore`.
6. Plugins are replaced by built-in commands.

Byte-identical output is the strictest of these and the most important: without it, the first Pull after switching rewrites every file in the repo. That is why the Item file writer is hand-built rather than a general YAML library.

Correctness is proved by differential testing. Each command runs with both the Official CLI and this tool from the same starting state, and we compare file bytes, CM item state, output and exit codes. Golden-file tests cover the formats without needing a CM.
