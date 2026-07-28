The `500` message is fixed — `Query execution failed.` — and stack traces, SQL, and EF Core
messages are never returned to the client. The only variable part is the `staleClient` marker.
