# Shared source, not a shared assembly

Code both halves of the link need, compiled into each of them rather than shipped beside them.

## Why source and not a library

Each plugin is one self-contained file on purpose - a `.gha` and a `.rhp` that a person can copy
somewhere and have work. A `ProjectReference` would put a third assembly next to them that both have to
find at load time, and Rhino's plugin loading is the wrong place to discover that a dependency did not
resolve. So each project globs these files in:

```xml
<Compile Include="..\Phenome.Apps.Shared\**\*.cs" LinkBase="Shared" />
```

One copy of the source, two copies of the IL, nothing new to deploy.

## Why namespace `Phenome.Apps`

It is the parent of `Phenome.Apps.GrasshopperLink` and `Phenome.Apps.RhinoLink`, so C# finds these
types from inside either without a `using` and without qualifying a single call site. The nesting is
doing the work that an import would otherwise have to.

## What may live here

Rhino-only. Plain .NET plus RhinoCommon, and nothing from Grasshopper - the Rhino half exists to answer
about a dialog that appears *before* Grasshopper has loaded, so it must not be made to depend on it.
Anything that touches a canvas belongs in the Grasshopper project.

## Why it exists at all

The two halves had drifted while nobody was looking, and both drifts were the same shape: a feature the
protocol description and the MCP tool schema both advertised, implemented in one copy and missing from
the other.

- `dismiss` took a `key` on the canvas side and dropped it on the Rhino side. Reading the Rhino copy,
  an agent reported a bug the *serving* copy did not have, and had to retract it.
- `/pulse` reported `clickable` on the canvas side only - while the Rhino half's own protocol text told
  callers to look at it. That half exists precisely for the Eto dialogs where `clickable` is false, so
  the field was missing exactly where it mattered most.

Neither was found by reading. Both were paid for in a wrong diagnosis.
