using System.Globalization;
using System.Runtime.InteropServices;
using PitGlow;
using PitGlow.Backends;
using PitGlow.Config;
using PitGlow.Devices;
using PitGlow.Engine;
using PitGlow.OpenRgb;
using PitGlow.RgbNet;
using PitGlow.Rendering;
using PitGlow.Sources;

namespace PitGlow.Cli;

public static class Program
{
    public static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(8);

    public sealed class Args
    {
        public string Command = "run";
        public string Config = "pitglow.toml";
        public string Host = "127.0.0.1";
        public int Port = 6742;
        public bool HostGiven, PortGiven;
        public string? Game;
        public double? Seconds;
        public bool Force;
    }

    public static int Main(string[] argv)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Args args;
        try
        {
            args = ParseArgs(argv);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine(ex.Message);
            PrintUsage();
            return 2;
        }
        Log.Path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args.Config)) ?? ".", "pitglow.log");
        try
        {
            return args.Command switch
            {
                "devices" => Devices(args),
                "init" => Init(args),
                "calibrate" => Calibrate(args),
                "demo" => Demo(args),
                "run" => Run(args),
                _ => Usage(),
            };
        }
        catch (BackendException ex)
        {
            Console.WriteLine($"\n{ex.Message}");
            Log.Error("backend", ex);
            return 1;
        }
        catch (ConfigException ex)
        {
            Console.WriteLine($"\nSettings problem: {ex.Message}");
            Log.Error("config", ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nError: {ex.Message}");
            Log.Error("failed", ex);
            return 1;
        }
    }

    private static int Usage()
    {
        PrintUsage();
        return 2;
    }

    private static void PrintUsage() => Console.WriteLine(
        $"Potato PitGlow {typeof(Program).Assembly.GetName().Version?.ToString(3)}\n" +
        "usage: PitGlow [run|demo|devices|init|calibrate] [--config FILE] [--host H] [--port P]\n" +
        "               [--game iracing|acevo|acrally|wreckfest2|beamng] [--seconds N] [--force]");

    public static Args ParseArgs(string[] argv)
    {
        var a = new Args();
        for (int i = 0; i < argv.Length; i++)
        {
            string arg = argv[i];
            string Next() => i + 1 < argv.Length ? argv[++i] : throw new ArgumentException($"{arg} needs a value");
            switch (arg)
            {
                case "run" or "demo" or "devices" or "init" or "calibrate": a.Command = arg; break;
                case "--config": a.Config = Next(); break;
                case "--host": a.Host = Next(); a.HostGiven = true; break;
                case "--port": a.Port = int.Parse(Next(), CultureInfo.InvariantCulture); a.PortGiven = true; break;
                case "--game": a.Game = Next(); break;
                case "--seconds": a.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--force": a.Force = true; break;
                case "-h" or "--help" or "help": a.Command = "help"; break;
                default: throw new ArgumentException($"Unknown argument: {arg}");
            }
        }
        return a;
    }

    /// <summary>Tests switch the vendor SDK routes off; they need real hardware.</summary>
    public static bool IncludeVendorBackends { get; set; } = OperatingSystem.IsWindows();

    /// <summary>Every lighting route this session uses, vendor SDKs first.</summary>
    private static BackendSet Backends(Args args, Settings? cfg)
    {
        var list = new List<IBackend>();
        if (IncludeVendorBackends)
        {
            if (cfg?.ICueEnabled != false) list.Add(RgbNetBackend.Corsair());
            if (cfg?.RazerEnabled != false) list.Add(RgbNetBackend.Razer());
        }
        if (cfg?.OpenRgbEnabled != false)
            list.Add(new OpenRgbBackend(args.HostGiven || cfg is null ? args.Host : cfg.Host,
                                        args.PortGiven || cfg is null ? args.Port : cfg.Port));
        return new BackendSet(list);
    }

    private static BackendSet.Result DiscoverAll(BackendSet set, bool quiet = false)
    {
        var result = set.Discover(DiscoverTimeout);
        if (!quiet) foreach (var note in result.Notes) Console.WriteLine($"  - {note}");
        if (result.Devices.Count == 0)
            throw new BackendException(
                "No lighting devices found. Start iCUE (for Corsair), Synapse (for Razer) or OpenRGB with its SDK Server on, then try again.");
        return result;
    }

    // ---------------------------------------------------------------------------------

    private static int Devices(Args args)
    {
        Settings? cfg = File.Exists(args.Config) ? SettingsFile.Load(args.Config) : null;
        Console.WriteLine("Lighting routes:");
        var infos = DiscoverAll(Backends(args, cfg)).Devices;
        foreach (var info in infos)
        {
            Console.WriteLine($"\n{info.Label}   [{info.KindName}]   {info.LedCount} LEDs   via {info.Backend}");
            Console.WriteLine($"    modes: {(info.ModeNames.Count > 0 ? string.Join(", ", info.ModeNames) : "-")}");
            if (info.Location.Length > 0) Console.WriteLine($"    location: {info.Location}");
            foreach (var zone in info.Zones) Console.WriteLine($"    zone '{zone.Name}': {zone.Leds.Count} LEDs");
        }
        return 0;
    }

    private static int Init(Args args)
    {
        if (File.Exists(args.Config) && !args.Force)
        {
            Console.WriteLine($"{args.Config} already exists. Use --force to replace it.");
            return 1;
        }
        Console.WriteLine("Lighting routes:");
        var infos = DiscoverAll(Backends(args, null)).Devices;
        var cfg = Suggest.Config(infos);
        cfg.Host = args.Host;
        cfg.Port = args.Port;
        SettingsFile.Save(cfg, args.Config);
        Console.WriteLine($"Wrote {args.Config}:");
        PrintPlan(cfg, infos);
        if (args.Command == "init") Console.WriteLine("\nNext: Calibrate.bat   (pick where each fan's gauge starts)");
        return 0;
    }

    private static bool EnsureConfig(Args args)
    {
        if (File.Exists(args.Config)) return true;
        Console.WriteLine($"No {args.Config} yet, creating one from your OpenRGB devices.\n");
        args.Force = false;
        bool ok = Init(args) == 0;
        Console.WriteLine();
        return ok;
    }

    private static LayoutPlan PrintPlan(Settings cfg, IReadOnlyList<DeviceInfo> infos)
    {
        var plan = Layouts.Build(cfg, infos);
        foreach (var layout in plan.Layouts)
            foreach (var g in layout.Groups)
                Console.WriteLine($"  {g.Label,-45} {g.Leds.Count,3} LEDs  -> {(g.Role == Roles.Tach ? "RPM gauge" : "flags")}");
        foreach (var note in plan.Notes) Console.WriteLine($"  * {note}");
        foreach (var problem in plan.Problems) Console.WriteLine($"  ! {problem}");
        if (plan.Layouts.Count == 0) Console.WriteLine("  Nothing assigned. Run Devices.bat to see what OpenRGB reports, then edit pitglow.toml.");
        return plan;
    }

    /// <summary>Discover, recover from a crashed session if needed, and build the plan.</summary>
    private static (BackendSet Backends, IReadOnlyList<DeviceInfo> Infos, LayoutPlan Plan, RestoreJournal Journal)
        Prepare(Args args, Settings cfg)
    {
        var set = Backends(args, cfg);
        var journal = new RestoreJournal(RestoreJournal.DefaultPath());
        Console.WriteLine("Lighting routes:");
        var infos = DiscoverAll(set).Devices;
        int restored = 0;
        foreach (var backend in set.Backends)
            if (journal.Entries().Any(e => e.Backend == backend.Id))
                restored += journal.Recover(backend, infos.Where(i => i.Backend == backend.Id).ToList());
        if (restored > 0)
        {
            Console.WriteLine($"Put back the lighting on {restored} device(s) left over from a session that didn't close cleanly.");
            infos = DiscoverAll(set, quiet: true).Devices;
        }
        Console.WriteLine("Lighting plan:");
        var plan = PrintPlan(cfg, infos);
        if (plan.SettingsChanged)
        {
            SettingsFile.Save(cfg, args.Config);
            Log.Info("Updated device names / locations in settings");
        }
        return (set, infos, plan, journal);
    }

    private static int RunLoop(Args args, Settings cfg, ITelemetrySource source, double? seconds, bool quiet = false)
    {
        var (backend, _, plan, journal) = Prepare(args, cfg);
        if (plan.Layouts.Count == 0) return 1;
        var runner = new Runner(cfg, source, backend.OpenSession, plan.Layouts, journal);
        using var stop = new CancellationTokenSource();
        using var finished = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        using var closeHandler = ConsoleClose.Install(stop, finished);
        Console.WriteLine("\nRunning. Press Ctrl+C to stop and restore your lighting.\n");
        try
        {
            runner.Run(stop.Token, seconds, quiet ? null : StatusLine);
        }
        finally
        {
            Console.WriteLine("\nStopping, restoring your lighting...");
            finished.Set();
        }
        return 0;
    }

    private static void StatusLine(string text)
    {
        var line = text.Length > 150 ? text[..150] : text.PadRight(150);
        Console.Write("\r" + line);
    }

    private static int Run(Args args)
    {
        if (!EnsureConfig(args)) return 1;
        var cfg = SettingsFile.Load(args.Config);
        if (args.Game is not null) cfg.Game.Source = args.Game;
        return RunLoop(args, cfg, SourceFactory.Make(cfg.Game), args.Seconds);
    }

    private static int Demo(Args args)
    {
        if (!EnsureConfig(args)) return 1;
        var cfg = SettingsFile.Load(args.Config);
        Console.WriteLine("Demo: fake RPM sweeps on the fans, every flag in turn on the flag devices " +
                          "(4 s each), pit limiter at 12-15 s.");
        return RunLoop(args, cfg, new DemoSource(), args.Seconds ?? 60);
    }

    // ---- calibrate: choose start LED and direction for every gauge ----------------------

    private const string CalibrateHelp = """

        Each fan shows its gauge layout: WHITE = where the gauge starts,
        then GREEN, then dim green = the direction it fills. Other fans are dark.
          Enter = looks right, next fan     n / p = move the start LED forward / back
          r = reverse direction             a = use these settings for every remaining fan
          q = quit without saving
        """;

    private static int Calibrate(Args args)
    {
        if (!EnsureConfig(args)) return 1;
        var cfg = SettingsFile.Load(args.Config);
        var (backend, _, plan, journal) = Prepare(args, cfg);
        var workers = plan.Layouts.ToDictionary(l => l, l => new DeviceWorker(l.Info, () => backend.OpenSession(l.Info), l.Cfg.MaxFps, journal));
        foreach (var w in workers.Values) w.Start();
        Console.WriteLine(CalibrateHelp);
        try
        {
            var tachs = plan.Layouts.SelectMany(l => l.Groups.Where(g => g.Role == Roles.Tach).Select(g => (Layout: l, Group: g))).ToList();
            bool copyRest = false;
            for (int number = 1; number <= tachs.Count && !copyRest; number++)
            {
                var (layout, group) = tachs[number - 1];
                var zoneCfg = layout.Cfg.Zones.First(z => layout.Info.Zone(z.Zone)?.Leds.SequenceEqual(group.Leds) == true);
                int n = group.Leds.Count;
                while (true)
                {
                    ShowCalibration(plan.Layouts, workers, group, zoneCfg);
                    var way = zoneCfg.Reverse ? "counter-clockwise" : "clockwise";
                    Console.Write($"[{number}/{tachs.Count}] {group.Label}: start LED {zoneCfg.StartLed}, {way} (as OpenRGB numbers it)  > ");
                    var answer = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
                    if (answer == "") break;
                    if (answer == "n") zoneCfg.StartLed = Effects.PyMod(zoneCfg.StartLed + 1, n);
                    else if (answer == "p") zoneCfg.StartLed = Effects.PyMod(zoneCfg.StartLed - 1, n);
                    else if (answer == "r") zoneCfg.Reverse = !zoneCfg.Reverse;
                    else if (answer == "a")
                    {
                        foreach (var (l2, g2) in tachs.Skip(number))
                            foreach (var z in l2.Cfg.Zones)
                                if (l2.Info.Zone(z.Zone)?.Leds.SequenceEqual(g2.Leds) == true && g2.Leds.Count == n)
                                    (z.StartLed, z.Reverse) = (zoneCfg.StartLed, zoneCfg.Reverse);
                        copyRest = true;
                        break;
                    }
                    else if (answer == "q")
                    {
                        Console.WriteLine("Not saved.");
                        return 1;
                    }
                    group = group with { StartLed = zoneCfg.StartLed, Reverse = zoneCfg.Reverse };
                }
            }
            SettingsFile.Save(cfg, args.Config);
            Console.WriteLine($"Saved to {args.Config}.");
            return 0;
        }
        finally
        {
            foreach (var w in workers.Values) w.Stop();
        }
    }

    private static void ShowCalibration(IReadOnlyList<Layout> layouts, Dictionary<Layout, DeviceWorker> workers, Group current, ZoneCfg zoneCfg)
    {
        Rgb[] marks = [new(255, 255, 255), new(0, 255, 0), new(0, 70, 0)];
        foreach (var layout in layouts)
        {
            var frame = layout.Info.OriginalColors.ToArray();
            foreach (var g in layout.Groups.Where(g => g.Role == Roles.Tach))
            {
                var ring = new Rgb[g.Leds.Count];
                if (g.Leds.SequenceEqual(current.Leds) && layout.Groups.Contains(g))
                {
                    int n = g.Leds.Count;
                    for (int k = 0; k < Math.Min(marks.Length, n); k++)
                        ring[Effects.PyMod(zoneCfg.StartLed + (zoneCfg.Reverse ? -k : k), n)] = marks[k];
                }
                for (int i = 0; i < g.Leds.Count; i++) frame[g.Leds[i]] = ring[i];
            }
            workers[layout].Submit(frame);
        }
    }
}

/// <summary>Restore lights when the console window is closed, not just on Ctrl+C.</summary>
internal static class ConsoleClose
{
    private delegate bool HandlerRoutine(uint ctrlType);

#pragma warning disable SYSLIB1054 // delegates need classic marshalling
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine? handler, bool add);
#pragma warning restore SYSLIB1054

    private sealed class Registration(HandlerRoutine handler) : IDisposable
    {
        public void Dispose()
        {
            try { SetConsoleCtrlHandler(handler, false); } catch { /* ignore */ }
            GC.KeepAlive(handler);
        }
    }

    public static IDisposable Install(CancellationTokenSource stop, ManualResetEventSlim finished)
    {
        if (!OperatingSystem.IsWindows()) return new Registration(_ => false);
        HandlerRoutine handler = ctrlType =>
        {
            if (ctrlType == 0) return false;     // Ctrl+C: Console.CancelKeyPress handles it
            stop.Cancel();
            finished.Wait(TimeSpan.FromSeconds(4.5));
            return true;
        };
        try { SetConsoleCtrlHandler(handler, true); } catch { /* ignore */ }
        return new Registration(handler);
    }
}
