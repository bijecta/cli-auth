using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Bijecta.CliAuth.StorageTests")]

// Cross-process race tests drive the coordinator and store through their internal seams in a child process.
[assembly: InternalsVisibleTo("Bijecta.CliAuth.SessionProcess")]
