using Phenome.Apps.RhinoInsideLink;

// Nothing in this file may name a RhinoCommon type: the resolver must run before the runtime searches for one,
// and the runtime searches when it compiles the method, not when it reaches the line.
return Serve(args);

static int Serve(string[] args)
{
    if (args.Any(a => a is "--help" or "-h" or "/?"))
    {
        Console.WriteLine("A Rhino core with no window, answering over loopback HTTP.");
        Console.WriteLine();
        Console.WriteLine("  Phenome.Apps.RhinoInsideLink [--quiet]");
        Console.WriteLine();
        Console.WriteLine("Prints the port it bound, and writes it to");
        Console.WriteLine("  %TEMP%\\phenome-rhinoinside-<pid>.port");
        Console.WriteLine("GET / on that port describes the whole protocol. POST /quit ends the process.");
        return 0;
    }

    bool quiet = args.Contains("--quiet");

    try
    {
        using HeadlessRhino rhino = HeadlessRhino.Start();

        InsideServer.Start(rhino);

        if (!quiet)
        {
            Console.WriteLine($"Phenome Rhino Inside Link: listening on http://127.0.0.1:{InsideServer.Port}/");
            Console.WriteLine($"  Rhino from {HeadlessRhino.SystemDirectory}");
            Console.WriteLine($"  port file  %TEMP%\\phenome-rhinoinside-{System.Environment.ProcessId}.port");
            Console.WriteLine("  GET / describes the protocol; POST /quit ends this.");
        }

        // Ctrl-C ends the process the same way /quit does, and the port file is removed either way.
        Console.CancelKeyPress += (_, cancelling) =>
        {
            cancelling.Cancel = true;
            rhino.Stop();
        };

        // The main thread becomes Rhino's: it serves the queue that requests marshal onto and returns when
        // Stop is called. Requests are handled on worker threads meanwhile.
        rhino.Serve();

        InsideServer.Stop();

        if (!quiet)
        {
            Console.WriteLine("Phenome Rhino Inside Link: stopped.");
        }

        return 0;
    }
    catch (Exception failure)
    {
        Console.Error.WriteLine($"Phenome Rhino Inside Link: could not start. {failure.Message}");
        return 1;
    }
}
