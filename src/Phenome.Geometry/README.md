# Phenome.Geometry

A small geometry kernel for building meshes and displaying them in a browser.

Targets `net10.0`, has no external dependencies, and survives a fully trimmed publish. The compiled
library is around 23 KB.

---

## What this is

A clean-room replacement for an older .NET Framework 4.8 geometry kernel. Type names are deliberately
familiar — `Point3d`, `Vector3d`, `Plane`, `Mesh` — but nothing else is carried over. The old API is not a
migration target.

Three constraints shape almost every decision here:

**It runs client-side in the browser.** Blazor WebAssembly. Payload size counts, and the garbage collector
is single-threaded and slower than on the desktop. So: no external packages, no `Parallel.For`, no LINQ in
hot paths, spans at the boundaries, and `IsAotCompatible` switched on so the trim and AOT analysers fail
the build rather than letting a problem surface at publish time.

**It has to hold about a million quad faces.** That number is why mesh faces live in a flat buffer rather
than one object each, and why there is no single-face removal.

**It is meant to be driven by a visual, block-based editor eventually.** This is the reason the API looks
the way it does — see below.

---

## Why the API looks unusual

**Types hold data. Modules hold behaviour.**

```csharp
Point3d a = PointOps.Create(0, 0, 0);     // not new Point3d(...)
double d = PointOps.DistanceTo(a, b);     // not a.DistanceTo(b)
double n = VectorOps.Length(v);           // not v.Length
Mesh box = MeshBuilders.CreateBox(10, 20, 30);
```

A member stays on a type only if it hands back a stored field, possibly under a different name —
`Plane.Normal` is `ZAxis`, the `TMatrix` indexer selects one of sixteen fields. Anything that *computes*
lives in a module.

The reason is the block editor. A plain static function with declared inputs and outputs maps one-to-one
onto a node; an instance method needs a special "this" input, which is a second shape the editor and the
node-catalogue generator would both have to special-case. The cost is real — you cannot discover the API
by typing `v.` in IntelliSense any more — and it was accepted knowingly, because in the editor the node
palette becomes the discovery mechanism.

If the C# ergonomics start to hurt, the escape hatch is C# 14 extension members: declare
`extension(Vector3d v) { public double Length => VectorOps.Length(v); }` in a separate namespace and
both spellings work over one implementation. Nothing in the design depends on that, and it can be added
later without reversing anything.

**What stays on types anyway**, because the language or common sense requires it: operators (C# demands
they be declared in an operand type), `Equals`/`GetHashCode`/`ToString`/`Deconstruct`, named constants like
`Vector3d.XAxis` and `TMatrix.Identity`, and mutators on mutable containers such as `Mesh.AddFace` — those
genuinely change the type's own data.

---

## Conventions

Follow these when adding anything. Most of them exist because the alternative produced a real bug in the
kernel this replaces.

### No public constructors

Constructors are `internal`; `XxxOps.Create(...)` is the way in. Properties are `{ get; }`, never
`{ get; init; }`, because a public `init` setter would reopen the door — and for `Plane` that would let
someone assemble a frame whose axes are not orthonormal, which the rest of the type relies on.

### The `Try` pattern: `bool` plus a *nullable* `out`

```csharp
if (Transforms.TryInvert(matrix, out TMatrix? inverse))
    point = PointOps.Transform(point, inverse.Value);
```

`false` means the `out` is `null`. There is **no fallback value** — no identity matrix, no zero vector.
Handing back something usable on failure is a silent wrong answer: geometry quietly goes untransformed and
the model looks plausible and is wrong. A `null` cannot be mistaken for a result, and a caller who skips
the `bool` gets a compile error at the point of misuse rather than a NaN twenty calls away.

Every such `out` needs `[NotNullWhen(true)]`, or callers get `CS8629` on the `.Value`.

Primitives with a single failure mode stop there. Algorithms that can *partially* succeed return
`OperationResult` instead — a status of `Success`/`Partial`/`Failed` plus a message, allocated only when
there is something to say. It replaces the integer exit code the old kernel returned from operations like
the mesh dual, where `1` meant "some faces were skipped" and there was no way to learn which.

### The signature is the contract

A parameter typed `Mesh` and not `Mesh?` says null is not allowed, and **nothing checks it again at run
time**. There is no `ArgumentNullException.ThrowIfNull` anywhere in this library. Nullable reference types
are on, so a caller passing null has already been told by the compiler; a guard would only restate the
signature, and it costs a line in every function to do it.

Same for the elements of a collection: `IReadOnlyList<Polyline>` declares its entries non-null too, so
neither the list nor what is in it gets tested.

The honest limit: the guarantee is compile-time, and only inside nullable-aware code. Something arriving
from reflection or a deserialiser can still be null, and then it surfaces as a `NullReferenceException`
inside the function rather than an `ArgumentNullException` at its edge. The stack trace still points at the
right call, which is the same trade this library takes everywhere else — see `Point3d.Unset` propagating NaN
rather than throwing on construction.

### Parameter modifiers

| | where | why |
|---|---|---|
| `in` | `TMatrix` (128 B), `Plane` (96 B), `Line` (48 B) | avoids a copy; free here because these are `readonly struct`, so no defensive copies |
| by value | `Point3d`, `Vector3d` (24 B) | under ~32 bytes a copy beats a dereference |
| `out` | `Try…` results | a second node output |
| `Span<T>` | caller-owned buffers | the modern answer to "write your result here" |
| `ref` | **never**, in public API | both input and output at once, which has no meaning in a dataflow graph |

Every `ref` in the old kernel was really an `out`; none read its incoming value.

### Point and vector arithmetic

They are separate types, with implicit conversions both ways, and **the left operand decides the result**:

| | | |
|---|---|---|
| `p + v`, `p - v`, `p + p` | → `Point3d` | a position on the left keeps you in point space |
| `v + v`, `v - v` | → `Vector3d` | a direction on the left keeps you in vector space |
| `p - p` | → `Vector3d` | the one exception: the difference of two positions is a displacement |
| `v + p`, `v - p` | **forbidden** | a direction plus or minus a position has no meaning |

All eight combinations are declared explicitly — including the two forbidden ones, which are declared and
poisoned with `[Obsolete(error: true)]`. That is not decoration: because both types convert implicitly to
one another, a merely *absent* overload would not forbid the expression — the compiler would quietly convert
the point and add two vectors. Declared and poisoned, `v + p` fails to compile with a message that speaks
geometry ("write `point + vector` to translate a position") instead of overload resolution.

### Where a function belongs

Operations on one type go in that type's module. A relation between two types belongs to the module of the
**richer** one: point against a plane is in `PlaneOps`, point against a line in `LineOps`,
point against point in `PointOps`. Write it down when you add a new pairing, or it will drift.

### Other

- **No required preallocation.** `List<T>` grows amortised-constant; capacity hints are `internal` at most,
  used by builders that happen to know their output size.
- **Degeneracy is never silent.** Normalising a zero vector, inverting a singular matrix, fitting a plane to
  collinear points — each has a `Try…` form that reports and a throwing form. Neither returns NaN.
- **Formatting is `InvariantCulture`.** The old kernel used the current culture, so on a Polish machine it
  emitted `Point(1,5, 0, 0)` — three coordinates and four separators.
- **Angles use `atan2`, not `acos`.** The arc-cosine form loses precision near 0 and π and rounding pushes
  its argument past 1 often enough to return NaN. Signed angles about an axis exist separately, because an
  unsigned angle cannot express direction and that broke every winding-order-dependent operation in the old
  kernel.

---

## Numeric choices

**Geometry is `double`. Display attributes are `float`.**

Vertices are `Point3d`, double precision, because construction is where robustness matters — welding at a
0.001 mm tolerance, intersection denominators, orientation predicates in hull construction. At 10 metres a
`float` steps in units of about 0.0012 mm, which is *coarser than the model tolerance*, so welding would
stop being repeatable.

Normals and texture coordinates are `System.Numerics.Vector3`/`Vector2`, and colours a 4-byte `Color32`,
because those exist only to be looked at. The payoff is not just memory: they upload to the GPU as a
straight memory copy with no per-element conversion, since the in-memory layout already matches what a
vertex buffer wants.

For a fully attributed million-quad mesh that is roughly 72 MB, or 44 MB with geometry alone.

`System.Numerics` was considered for the *base* types and rejected. It has one type for both positions and
directions, so the point/vector distinction above could not exist; `System.Numerics.Plane` is a normal plus
a distance with no frame, so it cannot evaluate at surface coordinates; `Matrix4x4` uses the opposite
multiplication convention; and float32 breaks the tolerance-sensitive modules still to come.

**Tolerances are never global.** `Tolerance` holds `const` defaults for parameter values, but anything that
depends on model scale takes its tolerance explicitly, because only the caller knows the units. The old
kernel had a mutable `static double Tolerance` — shared mutable state that made results depend on call
order.

---

## Matrix conventions

`TMatrix` fields are `M`*row**column*, so `M23` is row 2, column 3. The matrix acts on **column vectors**
(`p' = M * p`), which puts translation in the last column (`M14`, `M24`, `M34`) and makes the bottom row
`(0, 0, 0, 1)` for an affine transform. This matches RhinoCommon.

Composition follows suit: in `a * b` the **right-hand matrix applies first**. Read
`Translate(c) * Rotate(axis, angle) * Translate(-c)` right to left as "move to the origin, rotate, move
back".

---

## Mesh storage

Faces are stored compressed rather than as one object each:

```
_faceCorners = [ 0,1,2, 2,1,3,4, 4,3,5 ]     every face's corners, concatenated
_faceStarts  = [ 0,     3,       7,    10 ]  where each face begins, plus a sentinel
```

Face `i` is the slice between `_faceStarts[i]` and `_faceStarts[i + 1]`, handed out as a
`ReadOnlySpan<int>` — no copy, no allocation. For a million quads that is 20 MB in **three** heap objects;
one array per face would be 48 MB in **a million** objects, all of which a single-threaded browser
collector has to walk on every collection.

Faces can have any number of corners. A fixed four-corner struct would save 4 MB and give up n-gons, and
n-gons are what let a six-edged panel be one face.

**There is deliberately no way to remove a single face.** With a flat buffer that rewrites everything after
it, and in a loop it degrades to quadratic — exactly the trap the old kernel fell into through its vertex
removal. Use `MeshOps.RemoveFaces`, which compacts in one pass whether you remove one face or a
thousand.

The four attribute lists — normals, texture coordinates, vertex colours, face groups — are `null` until
something sets them, so an untextured single-material mesh pays nothing for what it does not use. They are
set in bulk, which is also what keeps the one-entry-per-vertex invariant checkable.

---

## Polyline conventions

A polyline of `n` points has `n - 1` segments, and **closing it means repeating the first point at the end**
— a closed square is five points, not four. Same as RhinoCommon, and it survives a round trip through a file
or a buffer without a flag riding alongside. The cost is that anything consuming a closed polyline has to
know about the repeated point, so test with `PolylineOps.IsClosed`, which takes a tolerance rather
than comparing floats.

Parameters are **index-based**: the integer part picks a segment and the fraction is the position along it,
so `2.5` is the midpoint of segment 2. That evaluates without knowing the total length. When arc length is
what you mean, use `PointAtLength` — on an L-shaped polyline of legs 2 and 1, parameter `1.5` and length
`1.5` land in completely different places, which is why both exist.

Evaluation clamps at both ends rather than extrapolating; a polyline has no natural extension past its last
point, so producing one would be inventing geometry.

---

---

## What exists

| | |
|---|---|
| `Tolerance` | numeric defaults for parameter values |
| `OperationResult` | `Success`/`Partial`/`Failed` plus a message |
| `Point3d`, `Vector3d` | position and direction, 24 B each |
| `Line` | segment, doubling as the infinite line through its endpoints |
| `Plane` | origin plus a right-handed orthonormal frame |
| `TMatrix` | 4×4 transform |
| `Interval` | a pair of bounds carrying a direction; a decreasing one is legal and means "backwards" |
| `Circle` | a plane plus a radius, so it has a start point and a sweep direction |
| `Arc` | a plane, a radius, and an angle domain |
| `Polyline` | an ordered run of points joined by straight segments |
| `Mesh` | vertices, compressed n-gon faces, four optional attribute lists |
| `Color32` | 4-byte RGBA in vertex-buffer order |
| `Transforms` | translate, scale, rotate, frame-to-world, plane-to-plane, invert, determinant |
| `BoundingBox` | three intervals, so it is a box in world axes and nothing more |
| `Triangulation` | ear clipping, with hole bridging and a self-crossing test |
| `MeshBuilders` | `Box`, `Grid`, `Prism`, `Cylinder`, `Cone`, `Pyramid`, `Sphere`, `CreatePlanarRegion`, `CreateExtrusion`, `CreateRevolution`, `Loft`, `Sweep` |
| `MeshCutting` | split or trim by a plane, capped or open |
| `RenderBuffers` | positions, triangle indices, and zero-copy attribute bytes |

---

## Triangulation

Ear clipping, deliberately, over a constrained Delaunay triangulation. The outlines here have tens of
corners, so the asymptotics do not matter, and a renderer cannot tell a well-shaped triangle from a sliver.
What ear clipping buys is a few hundred lines of arithmetic with no exact predicates to get wrong — a CDT
needs a robust `incircle`, and without one it does not degrade, it loops or produces inconsistent topology.

Three things worth knowing before using it:

- **`Partial` means clipping stalled. It does not mean the outline was simple.** Ear clipping cannot detect
  self-intersection: a bowtie finds an ear at every step and hands back overlapping triangles covering twice
  the real area, reporting success. That is why `SelfIntersects` runs first and a crossing outline is
  `Failed` outright. That check finds proper crossings only — edges that merely touch, or lie along each
  other, come back `false`.
- **No vertices are added.** Holes are bridged by repeating two existing corners in the traversal, so the
  triangles index straight into the points that went in and nothing needs welding afterwards. The cost is
  two extra triangles per hole: `RegionTriangleCount` accounts for it.
- **The triangle count is fixed at `n - 2` whatever happens**, including on `Partial`, so a buffer can be
  sized before the outline is looked at. When clipping stalls the remainder is fanned to make up the count.

A single outline's triangles keep the **winding of the outline as given** — `Mesh` faces depend on that for
their normals. A *region* with holes is always wound counter-clockwise about the plane normal instead, since
its winding is normalised anyway and the caller supplied the plane.

Quality is not guaranteed. If something later needs it — a scalar field drawn across a region, an offset, a
subdivision — the route is a Delaunay edge-flip pass over this output, not a different triangulator.
Refinement in the style of Chew's algorithm sits on top of a Delaunay triangulation, so that order of work
is not wasted.

`RenderBuffers` and `MeshOps.Triangulate` both go through this, but only for faces of five corners or more.
A triangle is copied through and a quad picks its shorter diagonal — two distance comparisons, no
allocation, which at a million faces is the whole budget.
