namespace Phenome.Apps;

/// <summary>Finds a layer by its full path, and makes it and its parents when they are missing.</summary>
/// <remarks>
/// Both halves put objects on a layer an agent names: the canvas half when it bakes, the Rhino half when a
/// Python script adds geometry. Without a named layer the objects land on whichever layer is current, which is
/// rarely the one a later clear or export works on, and models from two runs end up mixed in one picture.
/// </remarks>
internal static class Layers
{
    /// <summary>
    /// The index of the layer at <paramref name="path"/>, such as <c>Model::Walls</c>, made if missing.
    /// </summary>
    internal static int Ensure(Rhino.RhinoDoc doc, string path)
    {
        string[] names = path.Split("::", StringSplitOptions.TrimEntries);

        if (names.Length == 0 || names.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException($"'{path}' is not a layer path; levels are separated by '::'.");
        }

        Guid parent = Guid.Empty;
        int index = -1;

        for (int level = 0; level < names.Length; level++)
        {
            string full = string.Join("::", names.Take(level + 1));

            index = doc.Layers.FindByFullPath(full, -1);

            if (index < 0)
            {
                Rhino.DocObjects.Layer layer = new() { Name = names[level] };

                if (parent != Guid.Empty)
                {
                    layer.ParentLayerId = parent;
                }

                index = doc.Layers.Add(layer);

                if (index < 0)
                {
                    throw new InvalidOperationException($"Rhino would not make the layer '{full}'.");
                }
            }

            parent = doc.Layers[index].Id;
        }

        return index;
    }
}
