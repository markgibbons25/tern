# Speed comes from the client; stock Management Services only

Most of the Official CLI's slowness is server-side. Every Pull or Push asks the server to compute the Signature of every item. After every 26-command write batch, the server resets the template engine, queues updates on every index and rebuilds every GraphQL schema. A server module of our own could avoid all of that, but every team would have to deploy it to every CM, which breaks Drop-in on day one. So the tool talks only to the stock Management Services that ship on every CM. It gets its speed on the client side: bigger write batches (so far fewer server resets), more parallel reads, a local cache of Signatures, and no-op Pulls answered from the Change history instead of a full Signature fetch.

The connection to a CM sits behind one interface. An optional Accelerator can be added later, but only if benchmarks show the stock server has become the bottleneck. It must always remain optional, and the tool must work without it.

## Consequences

- Change history only keeps a day of entries. Every fast path that depends on it needs a correct full fallback, and the differential tests cover both paths.
