# CLI only, with no MCP server mode

Agents use Tern through the shell, with `--json`, the same as a script would. Tern does not run an MCP server. The repo ships an agent skill file that describes the commands, the Plan and Apply workflow, and the safety rules. Interactive item work through MCP is already covered by the Sitecore MCP server, so a `tern mcp` mode would duplicate it. A CLI plus a skill file works with every agent and needs no long-running process.
