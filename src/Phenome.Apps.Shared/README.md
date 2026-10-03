# Shared source, not a shared assembly

This folder holds code that the canvas half, the Rhino half and the headless server need. It is compiled
into each of them and not shipped beside them. The headless server leaves `Pulse.cs` out: a core with no
window has no frame and no dialogs to read.

## Why source and not a library

Each plugin is a single self-contained file on purpose: a `.gha` and a `.rhp` that a person can copy
somewhere and have work. A `ProjectReference` would add a third assembly that both must find at load time,
and plugin load is the worst place to discover an unresolved dependency. Each project includes these files
instead:

```xml
<Compile Include="..\Phenome.Apps.Shared\**\*.cs" LinkBase="Shared" />
```

The result is one copy of the source, one copy of the IL per assembly and nothing extra to deploy.

## Why namespace `Phenome.Apps`

It is the parent of `Phenome.Apps.GrasshopperLink` and `Phenome.Apps.RhinoLink`. C# resolves these types from
inside either project with no `using` and no call-site qualification, and the namespace nesting replaces what
an import would otherwise do.

## What may live here

Code here uses plain .NET and RhinoCommon, and nothing from Grasshopper. The Rhino half answers about dialogs
that appear *before* Grasshopper loads, and it must not depend on Grasshopper. Anything that touches a canvas
belongs in the Grasshopper project.

## Why it exists at all

The two halves drifted three times. Two drifts were advertised protocol features implemented in one copy and
missing from the other; the third was a port-binding fix applied to one copy only.

- `dismiss` accepted a `key` on the canvas side and ignored it on the Rhino side. Reading the Rhino copy, an
  agent reported a bug the serving copy did not have, then had to retract it.
- `/pulse` reported `clickable` on the canvas side only, while the Rhino half's protocol text told callers to
  read it. The Rhino half exists for the Eto dialogs where `clickable` is false, and the field was missing
  in exactly that half.
- The canvas half replaced probe-release-bind `FreePort()` with `Loopback.Listen`; the Rhino half kept the
  racing implementation. Two Rhinos starting together could choose the same port, and the loser wrote a
  discovery file for a port with no listener.

Reading the code caught none of the three, and the first two each caused a wrong diagnosis before they were
found.
