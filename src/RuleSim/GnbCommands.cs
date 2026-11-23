using System.Collections.Concurrent;
using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The Gunbreaker replay's commands (2026-11-23, user: "run it through the sim and compared to the previous ruleset"). Every
///     Gunbreaker in every Dancing Mad pull (or --me only) is replayed through the GNB rule files (GNB_ST.json with its layers, the
///     Keen Edge button) and scored against what that Gunbreaker really did:
///
///   run       all|&lt;log&gt; gnb [--me=HEX] [--rules=DIR] [--set=a=b,...] [--ab=a=b,...] [--from=YYYYMMDD] [--file=substr]
///             [--min-active=60] [--detail] [--fires] [--until=P2 (stop at the next phase's start)]
///   timeline  &lt;log&gt; &lt;pull&gt; gnb --me=HEX [--from=s --to=s] [--trace] [--set=a=b,...]
///
/// Score: potency x No Mercy (+20%) x other players' raid buffs (World.RaidBuffs). The model is deterministic (no procs), so one run
/// a pull. --ab replays every pull a second time with those defines on top of --set and prints the paired difference, with the
/// cartridges: made, spent inside and outside No Mercy, wasted to the cap, vanished with Bloodfest, held at a death, and the
/// seconds a minute at 2+ outside No Mercy.
/// </summary>
internal static class GnbCommands
{
    public const byte GnbJob = 37;
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private const string RepoRules = @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples";
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");

    public static bool Handles(string[] args) => args.Length > 2 && args[0] is "run" or "timeline" && (args[2] == "gnb" || args.Contains("--job=gnb") || args.Length > 3 && args[3] == "gnb");

    public static int Main(string[] args, EncounterRegistry registry)
    {
        A = args;
        return args[0] == "timeline" ? Timeline(registry) : Run(registry);
    }

    private static FileIndex Index(string path, EncounterRegistry reg)
    {
        var prev = IndexStore.Load(CacheDir, path);
        var idx = IndexStore.IndexFile(path, prev, new EncounterObserverFactory(reg), reg.HashFor);
        if (!ReferenceEquals(idx, prev)) IndexStore.Save(CacheDir, idx);
        return idx;
    }

    private static List<string> Logs(string spec)
    {
        if (spec != "all") return [File.Exists(spec) ? spec : Path.Combine(LogsDir, spec)];
        var from = Opt("from", "20260918");
        var only = Opt("file", "");
        return Directory.GetFiles(LogsDir, "Network_*.log")
            .Where(f => string.Compare(Path.GetFileNameWithoutExtension(f)[^8..], from, StringComparison.Ordinal) >= 0 && (only == "" || f.Contains(only)))
            .OrderBy(f => Path.GetFileNameWithoutExtension(f)[^8..]).ThenBy(f => f).ToList();
    }

    private static string SymbolsPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "gnb_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "gnb_symbols.json");
    }

    private static readonly ConcurrentDictionary<string, string> fileTexts = new();

    /// <summary> RuleEngine.FilesFor for GNB_ST_CustomRules (Keen Edge): the job's layers for the button in name order, then GNB_ST.json. </summary>
    public static List<CompiledRuleSet> Compile(GnbSim sim, string rulesDir, string set)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var selected = Opt("select", "GNB_ST.json");
        var layers = new List<CompiledRuleSet>(); var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var text = fileTexts.GetOrAdd(path, File.ReadAllText);
            var file = JsonConvert.DeserializeObject<RuleSetFile>(text);
            if (file is null || !string.Equals(file.Job, "GNB", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var compiled = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, GA.KeenEdge);
            if (compiled is null) throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't compile:\n    " + string.Join("\n    ", errors));
            var forButton = compiled.Replaces.Count == 0 || compiled.Replaces.Contains(GA.KeenEdge);
            if (!forButton) continue;
            if (compiled.Layer) layers.Add(compiled);
            else if (Path.GetFileName(path).Equals(selected, StringComparison.OrdinalIgnoreCase)) others.Add(compiled);
        }
        return layers.Concat(others).ToList();
    }

    private static readonly HashSet<string> Gcds25 = new(StringComparer.OrdinalIgnoreCase)
        { "Keen Edge", "Brutal Shell", "Solid Barrel", "Burst Strike", "Savage Claw", "Wicked Talon", "Noble Blood", "Lion Heart", "Sonic Break" };

    /// <summary> Median weaponskill GCD of the logged Gunbreaker (intervals of 2.3-2.6 s), else 2500. </summary>
    private static int MeasureGcd(World w)
    {
        var t = w.R.Actions.Where(a => a.Source.Id == w.Me.Id && Gcds25.Contains(a.Name)).Select(a => a.T).OrderBy(x => x).ToList();
        var iv = t.Zip(t.Skip(1), (a, b) => b - a).Where(d => d is >= 2300 and <= 2600).OrderBy(x => x).ToList();
        return iv.Count >= 5 ? iv[iv.Count / 2] : 2500;
    }

    private static readonly Dictionary<string, int> Potency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Keen Edge"] = 300, ["Brutal Shell"] = 380, ["Solid Barrel"] = 460, ["Burst Strike"] = 420, ["Gnashing Fang"] = 440, ["Savage Claw"] = 500,
        ["Wicked Talon"] = 560, ["Double Down"] = 1000, ["Sonic Break"] = 940, ["Reign of Beasts"] = 800, ["Noble Blood"] = 900, ["Lion Heart"] = 1000,
        ["Lightning Shot"] = 150, ["Jugular Rip"] = 220, ["Abdomen Tear"] = 260, ["Eye Gouge"] = 300, ["Hypervelocity"] = 180, ["Blasting Zone"] = 800,
        ["Bow Shock"] = 450,
    };
    private static readonly HashSet<string> Ogcds = new(StringComparer.OrdinalIgnoreCase)
        { "Jugular Rip", "Abdomen Tear", "Eye Gouge", "Hypervelocity", "Blasting Zone", "Bow Shock" };

    /// <summary> The logged Gunbreaker's own potency under the same model (combo potencies; DoTs in full at the hit). </summary>
    private static (double Pot, int Gcds, int Bursts) Actual(World w, int end = int.MaxValue)
    {
        var me = w.Me;
        var nm = w.R.Statuses.Where(s => s.Target.Id == me.Id && s.StatusId == GS.NoMercy && s.Source?.Id == me.Id).ToList();
        double pot = 0; int gcds = 0, bursts = 0;
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == me.Id && a.T >= -1000 && a.T < end))
        {
            if (a.Name == "Burst Strike") bursts++;
            if (!Potency.TryGetValue(a.Name, out var p)) continue;
            var hit = a.Hits.FirstOrDefault(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage && h.Damage > 0);
            if (hit is null) continue;
            if (!Ogcds.Contains(a.Name)) gcds++;
            var t = Math.Max(0, a.T);
            pot += p * (nm.Any(s => s.Active(t)) ? GnbSim.NoMercyMult : 1) * w.RaidBuffs(hit.Target, t).Mult;
        }
        return (pot, gcds, bursts);
    }

    /// <summary> The countdown the replay gets: the pull's (up to 30 s), else 0 (the file waits for the countdown, then Lightning Shot). </summary>
    private static int StartMs(PullSummary s) => s.CountdownSeconds > 0 ? -Math.Min(s.CountdownSeconds, 30) * 1000 : 0;

    private static int EndOf(PullReplay r)
    {
        var until = Opt("until", "");
        if (until == "") return r.EndMs;
        var phases = r.Phases.Where(p => !p.IsSegment).OrderBy(p => p.StartMs).ToList();
        var i = phases.FindIndex(p => p.Name.Split(' ')[0].Equals(until, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < phases.Count ? Math.Min(r.EndMs, phases[i + 1].StartMs) : r.EndMs;
    }

    public sealed record SimRun(double Pot, double Gcds, int Made, int Spent, int InNm, int Outside, int Wasted, int Vanished, int LostToDeath,
        double At2Outside, Dictionary<string, int> Uses, Dictionary<string, double> Fires, int Unmodelled);

    private static SimRun Simulate(PullSummary s, World w, PullReplay r, string rulesDir, string set, int gcd)
    {
        var sim = new GnbSim(w, gcd);
        var files = Compile(sim, rulesDir, set);
        sim.T = StartMs(s);
        var end = EndOf(r);
        while (sim.T < end)
        {
            sim.Press(files);
            var next = Math.Min(sim.NextWake(40), end);
            if (next <= sim.T) break;
            sim.Advance(next - sim.T);
        }
        return new SimRun(sim.Damage.Sum(d => d.Potency * d.Mult), sim.Gcds, sim.CartsMade, sim.CartsSpent, sim.SpentInNoMercy, sim.SpentOutside, sim.CartsWasted,
            sim.CartsVanished, sim.CartsLostToDeath + sim.Carts, sim.MsAt2Outside / 1000.0, new(sim.Uses),
            sim.RuleFires.ToDictionary(kv => kv.Key, kv => (double)kv.Value), sim.Unmodelled);
    }

    public sealed record Outcome(string Key, string Name, uint Id, double ActiveMin, double EndS, double RealPot, int RealGcds, int RealBursts, SimRun Base, SimRun? Alt, string Phase);

    private static Outcome? One(PullSummary s, Actor gnb, PullReplay r, string rulesDir, string key)
    {
        var w = new World(r, gnb.Id);
        var gcd = MeasureGcd(w);
        var end = EndOf(r);
        var real = Actual(w, end);
        var set = Opt("set", "");
        var ab = Opt("ab", "");
        var b = Simulate(s, w, r, rulesDir, set, gcd);
        var alt = ab == "" ? null : Simulate(s, w, r, rulesDir, set == "" ? ab : set + "," + ab, gcd);
        var probe = new GnbSim(w, gcd) { T = 0 };
        while (probe.T < end) probe.Advance(Math.Min(100, end - probe.T));
        return new Outcome(key, gnb.Name, gnb.Id, probe.AvailMs / 60000.0, end / 1000.0, real.Pot, real.Gcds, real.Bursts, b, alt, r.PhaseAt(end - 1));
    }

    private static int Run(EncounterRegistry registry)
    {
        var rulesDir = Opt("rules", RepoRules);
        var minActive = double.Parse(Opt("min-active", "60")) / 60;
        uint? me = Opt("me", "") is { Length: > 0 } m ? Convert.ToUInt32(m, 16) : null;
        var jobs = new List<(PullSummary S, string Key)>();
        foreach (var path in Logs(A[1]))
        {
            FileIndex idx;
            try { idx = Index(path, registry); } catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
            foreach (var s in idx.Pulls.Where(p => p.ZoneId == 1363 && p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond
                                                    && p.Party.Any(x => x.Job == GnbJob && (me is null || x.Id == me))))
                jobs.Add((s, $"{Path.GetFileNameWithoutExtension(path)[^8..]}_{s.Ordinal:D3}"));
        }
        Console.Error.WriteLine($"{jobs.Count} pulls; rules {rulesDir}; set {Opt("set", "-")}; ab {Opt("ab", "-")}");
        var results = new ConcurrentBag<Outcome>();
        var done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, job =>
        {
            PullReplay r;
            try { r = PullLoader.Load(job.S, null, registry); } catch { return; }
            var gnbs = r.Party.Count > 0 ? r.Party : r.Players.ToList();
            foreach (var g in gnbs.Where(x => x.Job == GnbJob && (me is null || x.Id == me)))
            {
                try { if (One(job.S, g, r, rulesDir, job.Key) is { } o) results.Add(o); }
                catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); }
                catch (Exception e) { Console.Error.WriteLine($"{job.Key} {g.Name}: {e.GetType().Name} {e.Message}"); }
            }
            if (Interlocked.Increment(ref done) % 50 == 0) Console.Error.WriteLine($"  {done}/{jobs.Count}");
        });
        var R = results.Where(o => o.ActiveMin >= minActive && o.RealPot > 0).OrderBy(o => o.Key).ToList();
        Console.WriteLine($"\n{R.Count} Gunbreaker pulls with >= {minActive * 60:0} s active ({results.Count} total), rules {Path.GetFileName(rulesDir)}, set {Opt("set", "-")}");
        if (A.Contains("--detail"))
            foreach (var o in R)
                Console.WriteLine($"{o.Key} {o.Name,-20} {o.ActiveMin,5:0.0}m {o.Phase,-22} pot/min sim {o.Base.Pot / o.ActiveMin,7:0} real {o.RealPot / o.ActiveMin,7:0} ({100 * (o.Base.Pot / o.RealPot - 1),+6:0.0}%)" +
                    (o.Alt is { } alt ? $"  alt {alt.Pot / o.ActiveMin,7:0} ({100 * (alt.Pot / o.Base.Pot - 1),+6:0.00}%)" : "") + $"  gcd {o.Base.Gcds:0}/{o.RealGcds}");
        Console.WriteLine($"{"Gunbreaker",-22} {"pulls",5} {"min",6} {"pot/min sim",11} {"real",7} {"diff",7} {"GCD/min sim",11} {"real",6} {"BS/10m sim",10} {"real",5}");
        void Line(string name, List<Outcome> L)
        {
            var act = L.Sum(o => o.ActiveMin);
            double sp = L.Sum(o => o.Base.Pot), rp = L.Sum(o => o.RealPot);
            Console.WriteLine($"{name,-22} {L.Count,5} {act,6:0} {sp / act,11:0} {rp / act,7:0} {100 * (sp / rp - 1),+6:0.0}% {L.Sum(o => o.Base.Gcds) / act,11:0.00} {L.Sum(o => o.RealGcds) / act,6:0.00} " +
                              $"{10 * L.Sum(o => o.Base.Uses.GetValueOrDefault("Burst Strike")) / act,10:0.0} {10 * L.Sum(o => o.RealBursts) / act,5:0.0}");
        }
        foreach (var g in R.GroupBy(o => o.Name).OrderByDescending(g => g.Count())) Line(g.Key, g.ToList());
        Line("ALL", R);
        if (R.Any(o => o.Alt is not null))
        {
            Console.WriteLine($"\nA/B: --ab={Opt("ab", "")} (alt) against the base (same pulls)");
            void Ab(string name, List<Outcome> L)
            {
                if (L.Count == 0) return;
                var act = L.Sum(o => o.ActiveMin);
                double b = L.Sum(o => o.Base.Pot), a = L.Sum(o => o.Alt!.Pot);
                var better = L.Count(o => o.Alt!.Pot > o.Base.Pot + 1); var worse = L.Count(o => o.Alt!.Pot < o.Base.Pot - 1);
                Console.WriteLine($"  {name,-30} {L.Count,4} pulls {act,6:0} min: pot/min base {b / act,7:0} alt {a / act,7:0} ({100 * (a / b - 1),+6:0.00}% alt vs base); alt better in {better}, worse in {worse}");
            }
            Ab("all", R);
            Ab("pulls under 3:20 (Phase 1)", R.Where(o => o.EndS < 200).ToList());
            Ab("pulls 3:20 to 9:00", R.Where(o => o.EndS is >= 200 and < 540).ToList());
            Ab("pulls 9:00 or more", R.Where(o => o.EndS >= 540).ToList());
            foreach (var g in R.GroupBy(o => o.Name).Where(g => g.Count() >= 5).OrderByDescending(g => g.Count())) Ab(g.Key, g.ToList());
            string Carts(Func<Outcome, SimRun> pick, List<Outcome> L)
            {
                var act = L.Sum(o => o.ActiveMin);
                double Per10(Func<SimRun, double> f) => 10 * L.Sum(o => f(pick(o))) / act;
                return $"per 10 min: made {Per10(s => s.Made):0.0}, spent in No Mercy {Per10(s => s.InNm):0.0} / outside {Per10(s => s.Outside):0.0}, wasted to the cap {Per10(s => s.Wasted):0.0}, " +
                       $"vanished (Bloodfest) {Per10(s => s.Vanished):0.0}, held at a death or the end {Per10(s => s.LostToDeath):0.0}; at 2+ outside No Mercy {60 * L.Sum(o => pick(o).At2Outside) / (act * 60) * 60 / 60:0.0} s a minute; " +
                       $"Burst Strike {Per10(s => s.Uses.GetValueOrDefault("Burst Strike")):0.0}, Hypervelocity {Per10(s => s.Uses.GetValueOrDefault("Hypervelocity")):0.0}, Gnashing Fang {Per10(s => s.Uses.GetValueOrDefault("Gnashing Fang")):0.0}, " +
                       $"Double Down {Per10(s => s.Uses.GetValueOrDefault("Double Down")):0.0}, No Mercy {Per10(s => s.Uses.GetValueOrDefault("No Mercy")):0.0}";
            }
            Console.WriteLine($"  cartridges, base: {Carts(o => o.Base, R)}");
            Console.WriteLine($"  cartridges, alt:  {Carts(o => o.Alt!, R)}");
        }
        Console.WriteLine($"  unmodelled presses (mitigation etc.) per pull: {R.Average(o => (double)o.Base.Unmodelled):0}");
        if (A.Contains("--fires"))
            foreach (var kv in R.SelectMany(o => o.Base.Fires).GroupBy(kv => kv.Key).Select(g => (g.Key, N: g.Sum(x => x.Value))).OrderByDescending(x => x.N))
                Console.WriteLine($"  {kv.N,9:0} {kv.Key}");
        return 0;
    }

    private static int Timeline(EncounterRegistry registry)
    {
        var path = Logs(A[1])[0];
        var ordinal = int.Parse(A[2]);
        var meId = Convert.ToUInt32(Opt("me", "10041112"), 16);
        var s = Index(path, registry).Pulls.First(p => p.Ordinal == ordinal);
        var r = PullLoader.Load(s, null, registry);
        var w = new World(r, meId);
        int from = (int)(double.Parse(Opt("from", "-30")) * 1000), to = (int)(double.Parse(Opt("to", "9999")) * 1000);
        var sim = new GnbSim(w, MeasureGcd(w)) { Trace = A.Contains("--trace"), TraceFrom = from, TraceTo = to };
        var files = Compile(sim, Opt("rules", RepoRules), Opt("set", ""));
        sim.T = StartMs(s);
        while (sim.T < r.EndMs)
        {
            sim.Press(files);
            var next = Math.Min(sim.NextWake(40), r.EndMs);
            if (next <= sim.T) break;
            sim.Advance(next - sim.T);
        }
        Console.WriteLine($"{Path.GetFileName(path)} #{ordinal} {w.Me.Name} gcd {sim.GcdMs} countdown {s.CountdownSeconds}; sim left, real right");
        var real = r.Actions.Where(a => a.Source.Id == meId && a.T >= from && a.T <= to && a.Name != "attack").Select(a => (a.T, Text: $"{"",-64} | {a.Name}")).ToList();
        var mine = sim.Log.Where(e => e.T >= from && e.T <= to).Select(e => (e.T, Text: $"{e.What,-28} {e.Rule.Split(": ").Last(),-35}|")).ToList();
        foreach (var (t, text) in mine.Concat(real).OrderBy(x => x.T)) Console.WriteLine($"{SageSim.F(t),8} {text}");
        Console.WriteLine($"sim potency {sim.Damage.Sum(d => d.Potency * d.Mult):0}, GCDs {sim.Gcds}, cartridges made {sim.CartsMade} spent {sim.CartsSpent} (No Mercy {sim.SpentInNoMercy}) wasted {sim.CartsWasted} vanished {sim.CartsVanished}");
        var act = Actual(w);
        Console.WriteLine($"real potency {act.Pot:0}, GCDs {act.Gcds}, Burst Strikes {act.Bursts}");
        return 0;
    }
}
