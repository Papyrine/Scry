# Scry.Server.Disclosure.Explorer

An opt-in browser explorer over the [Scry](https://github.com/Papyrine/Scry) [disclosure audit](https://github.com/Papyrine/Scry/blob/main/docs/disclosure-audit.md), for whoever has to answer who saw what.

```cs
app.MapScryDisclosureExplorer();
```

It asks the record the three questions it exists to answer: who received a row, what a caller received in a range of time, and whether a caller ever received a member. Any event opens to the request that was answered, the members it read, the rows it carried, and the payload put back together from the store.

Reading the record is itself recorded. Every question is written to the record before it is answered, through the same sink the answers went through, and a reviewer shown an event is from then on among those who received its rows.

The explorer is a window onto the most sensitive data the host holds, so every way in is shut until a host opens it:

- `EnableGuard` covers the whole explorer. Development-only by default, and a closed explorer answers `404`.
- `EnableExport` covers a result leaving as a file. Development-only by default.
- `EnableErase` covers erasing a row's content. Off by default, everywhere.
- `Reviewer` says who is reading. A request that names nobody is refused.

`RequireAuthorization` on what `MapScryDisclosureExplorer` returns reaches every route it mapped.

Docs: [Disclosure audit](https://github.com/Papyrine/Scry/blob/main/docs/disclosure-audit.md)
