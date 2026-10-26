# Shared storage internals

Shared internal C# source is compiled into each platform storage assembly through
`Shared.Storage.projitems`. This directory is not a project or NuGet package.
Keep platform-specific dependencies in the corresponding backend project.
Encryption, atomic file handling, and lock helpers will be added with storage features.
