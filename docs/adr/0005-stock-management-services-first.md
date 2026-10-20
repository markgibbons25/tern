# Speed comes from the client; stock Management Services only

Most of the Official CLI's slowness is server-side. Every Pull or Push asks the server to compute the Signature of every item. After every 26-command write batch, the server resets the template engine, queues updates on every index and rebuilds every GraphQL schema. A server module of our own could avoid all of that, but every team would have to deploy it to every CM, which breaks Drop-in on day one. So the tool talks only to the stock Management Services that ship on every CM. It gets its speed on the client side: bigger write batches (so far fewer server resets), more parallel reads, a faster client, and target lookups batched one level of the tree at a time.

The connection to a CM sits behind one interface. An optional Accelerator can be added later, but only if benchmarks show the stock server has become the bottleneck. It must always remain optional, and the tool must work without it.

## Considered Options

- **No-op Pulls answered from the Change history.** We rejected this after testing. Management Services writes items with events switched off, so a Push by any CLI never appears in the Change history: on the local CM, two pushes left the history empty. A Pull that trusted the history would miss content someone else pushed. Watch has the same blind spot in both tools, and it only follows edits made in Sitecore's editors.
