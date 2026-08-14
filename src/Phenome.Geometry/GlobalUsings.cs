// The kernel's namespaces follow its folders, so inside it the three parts have to find each other.
//
// Two of the three come free from where a file sits: name lookup walks outwards, so anything in
// Phenome.Geometry.Types can already see Phenome.Geometry, which is where Tolerance, Guard and OperationResult
// live. What it cannot see is its sibling, and the types and the modules over them are used together constantly —
// that is the design, not an accident of layout.
//
// Declared once here rather than at the top of thirty-one files, because it is a fact about the assembly rather
// than about any file in it. A consumer says the same thing in its own GlobalUsings.cs.

global using Phenome.Geometry.Modules;
global using Phenome.Geometry.Types;
