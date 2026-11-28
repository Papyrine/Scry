// The wire vocabulary, as Scry.Tests renders it from Scry.Wire. Imported from the snapshot itself rather
// than copied, so the two cannot drift: a change to the wire fails TypeScriptFixtureTests.Wire on the
// .NET side, and accepting it changes what this client is checked against.
export * from "../../../src/Scry.Tests/TypeScript/wire.verified.mts";
