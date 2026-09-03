global using System.Diagnostics;
global using System.Globalization;
global using System.Text.Json;
global using Scry;
// Supplied by Polyfill in the shipped projects, which this one does not reference. Declared here so
// the project alias CLAUDE.md mandates is available in the explorer too.
global using Cancel = System.Threading.CancellationToken;
global using CancelSource = System.Threading.CancellationTokenSource;
