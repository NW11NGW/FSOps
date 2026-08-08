using System.Runtime.CompilerServices;

// Lets the test project call route pairing logic (RouteEndpoints.CreateAsync/ListAsync/DeleteAsync)
// directly against an isolated in-memory database, without standing up a full HTTP host.
[assembly: InternalsVisibleTo("FSOps.Server.Tests")]
