using System.Collections.Concurrent;
using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The Red Mage replay's commands (2026-11-18). Every Red Mage in every Dancing Mad pull (or --me only) is replayed through the
///     RDM rule files (RDM_ST.json with its layers, the Jolt button) and scored against what that Red Mage really did:
///
///   run       all|&lt;log&gt; rdm [--me=HEX] [--rules=DIR] [--set=a=b,...] [--ab=a=b,...] [--seeds=5] [--from=YYYYMMDD] [--file=substr]
///             [--min-active=60] [--detail] [--fires] [--until=P2 (stop at the next phase's start)]
///   timeline  &lt;log&gt; &lt;pull&gt; rdm --me=HEX [--seed=1] [--from=s --to=s] [--trace] [--set=a=b,...]
///
/// Score: potency x own Embolden (+10% magic, the game data) x other players' raid buffs (World.RaidBuffs). --ab replays every pull
/// and seed a second time with those defines on top of --set, and prints the paired difference (all pulls, by length, first 30 s).
/// </summary>
internal static class RdmCommands
{
    public const byte RdmJob = 35;
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private const string RepoRules = @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples";
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");

    public static bool Handles(string[] args) => args.Length > 2 && args[0] is "run" or "timeline" && (args[2] == "rdm" || args.Contains("--job=rdm") || args.Length > 3 && args[3] == "rdm");

    public static int Main(string[] args, EncounterRegistry registry, Func<string, FileIndex> _)
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
        var p = Path.Combine(AppContext.BaseDirectory, "rdm_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "rdm_symbols.json");
    }

    private static readonly ConcurrentDictionary<string, string> fileTexts = new();

    /// <summary> RuleEngine.FilesFor for RDM_ST_CustomRules (Jolt): the job's layers for the button in name order, then RDM_ST.json. </summary>
    public static List<CompiledRuleSet> Compile(RdmSim sim, string rulesDir, string set)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var selected = Opt("select", "RDM_ST.json");
        var layers = new List<CompiledRuleSet>(); var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var text = fileTexts.GetOrAdd(path, File.ReadAllText);
            var file = JsonConvert.DeserializeObject<RuleSetFile>(text);
            if (file is null || !string.Equals(file.Job, "RDM", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var compiled = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, RA.Jolt);
            if (compiled is null) throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't compile:\n    " + string.Join("\n    ", errors));
            var forButton = compiled.Replaces.Count == 0 || compiled.Replaces.Contains(RA.Jolt);
            if (!forButton) continue;
            if (compiled.Layer) layers.Add(compiled);
            else if (Path.GetFileName(path).Equals(selected, StringComparison.OrdinalIgnoreCase)) others.Add(compiled);
        }
        return layers.Concat(others).ToList();
    }

    private static readonly HashSet<string> Instants25 = new(StringComparer.OrdinalIgnoreCase) { "Verflare", "Verholy", "Scorch", "Resolution", "Grand Impact" };

    /// <summary> Median spell GCD of the logged Red Mage (finisher-to-finisher and Grand Impact intervals of 2.3-2.6 s), else 2500. </summary>
    private static int MeasureGcd(World w)
    {
        var t = w.R.Actions.Where(a => a.Source.Id == w.Me.Id && Instants25.Contains(a.Name)).Select(a => a.T).OrderBy(x => x).ToList();
        var iv = t.Zip(t.Skip(1), (a, b) => b - a).Where(d => d is >= 2300 and <= 2600).OrderBy(x => x).ToList();
        return iv.Count >= 5 ? iv[iv.Count / 2] : 2500;
    }

    private static readonly Dictionary<string, int> Potency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Jolt III"] = 360, ["Verthunder III"] = 440, ["Veraero III"] = 440, ["Verfire"] = 380, ["Verstone"] = 380, ["Grand Impact"] = 600,
        ["Verflare"] = 650, ["Verholy"] = 650, ["Scorch"] = 750, ["Resolution"] = 850, ["Enchanted Riposte"] = 340, ["Enchanted Zwerchhau"] = 380,
        ["Enchanted Redoublement"] = 560, ["Fleche"] = 480, ["Contre Sixte"] = 420, ["Engagement"] = 180, ["Corps-a-corps"] = 130,
        ["Prefulgence"] = 1200, ["Vice of Thorns"] = 950, ["Impact"] = 210, ["Enchanted Reprise"] = 420,
    };

    /// <summary> The logged Red Mage's own potency under the same model (combo potencies for the melee steps). </summary>
    private static (double Pot, int Gcds, int Accel, int Gi, int Cancels) Actual(World w, int end = int.MaxValue)
    {
        var me = w.Me;
        var emb = w.R.Statuses.Where(s => s.Target.Id == me.Id && s.StatusId == RS.Embolden && s.Source?.Id == me.Id).ToList();
        double pot = 0; int gcds = 0, accel = 0, gi = 0;
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == me.Id && a.T >= -1000 && a.T < end))
        {
            if (a.Name == "Acceleration") accel++;
            if (a.Name == "Grand Impact") gi++;
            if (!Potency.TryGetValue(a.Name, out var p)) continue;
            var hit = a.Hits.FirstOrDefault(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage && h.Damage > 0);
            if (hit is null) continue;
            if (a.Name is not ("Fleche" or "Contre Sixte" or "Engagement" or "Corps-a-corps" or "Prefulgence" or "Vice of Thorns")) gcds++;
            var t = Math.Max(0, a.T);
            pot += p * (emb.Any(s => s.Active(t)) ? RdmSim.EmboldenSelf : 1) * w.RaidBuffs(hit.Target, t).Mult;
        }
        var cancels = w.R.Casts.Count(c => c.Source.Id == me.Id && c.StartMs >= 0 && c.StartMs < end && c.Outcome == CastOutcome.Cancelled);
        return (pot, gcds, accel, gi, cancels);
    }

    /// <summary> The countdown the replay gets: the pull's (up to 30 s), or 16 s when the logged Red Mage acted before the pull without one detected. </summary>
    private static int StartMs(PullSummary s, World w)
    {
        if (s.CountdownSeconds > 0) return -Math.Min(s.CountdownSeconds, 30) * 1000;
        return w.R.Actions.Any(a => a.Source.Id == w.Me.Id && a.T < -1000 && a.Name != "attack") ? -16000 : 0;
    }

    public sealed record SimRun(double Pot, double Pot30, double Gcds, double Accel, double Gi, double Cancels, double Expired, Dictionary<string, double> Fires, string FirstGcds,
        List<RunProbe>? Runs = null);

    /// <summary> Dancing Mad's two long runs (2026-11-18, optAccelForRuns): Acceleration charges at the run's start (the second Graven
    /// Image + 9.8 s, Bowels of Agony + 7.0 s), and the Accelerations, GCDs and cancelled casts in the next 12 s. Alive throughout. </summary>
    public sealed record RunProbe(string Name, int Charges, int Accel, int Gcds, int Cancels);

    private static List<(string Name, int T, int Len)> RunTimes(World w)
    {
        var l = new List<(string, int, int)>();
        var gi = w.CastStarts("gravenimage").ToList();
        if (gi.Count >= 2) l.Add(("second Graven Image run", gi[1] + 9800, 12000));
        var boa = w.CastStarts("bowelsofagony").ToList();
        if (boa.Count >= 1) l.Add(("Bowels of Agony run", boa[0] + 7000, 12000));
        return l;
    }

    /// <summary> --until=P2 (2026-11-18): the start of the first phase after it (the pull's end if it never got there); else the pull's end. </summary>
    private static int EndOf(PullReplay r)
    {
        var until = Opt("until", "");
        if (until == "") return r.EndMs;
        var phases = r.Phases.Where(p => !p.IsSegment).OrderBy(p => p.StartMs).ToList();
        var i = phases.FindIndex(p => p.Name.Split(' ')[0].Equals(until, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < phases.Count ? Math.Min(r.EndMs, phases[i + 1].StartMs) : r.EndMs;
    }

    private static SimRun Simulate(PullSummary s, World w, PullReplay r, string rulesDir, string set, int seed, int gcd, bool trace = false, int from = 0, int to = 0)
    {
        var sim = new RdmSim(w, gcd, seed) { Trace = trace, TraceFrom = from, TraceTo = to };
        var files = Compile(sim, rulesDir, set);
        sim.T = StartMs(s, w);
        var end = EndOf(r);
        var runs = RunTimes(w).Where(x => x.T + x.Len <= end && !w.Dead(w.Me, x.T) && !w.Dead(w.Me, x.T + x.Len)).ToList();
        var atStart = new Dictionary<string, int>();
        while (sim.T < end)
        {
            foreach (var x in runs)
                if (sim.T >= x.T && !atStart.ContainsKey(x.Name)) atStart[x.Name] = sim.ChargesNow(RA.Acceleration);
            sim.Press(files);
            var next = Math.Min(sim.NextWake(40), end);
            if (next <= sim.T) break;
            sim.Advance(next - sim.T);
        }
        var probes = runs.Where(x => atStart.ContainsKey(x.Name)).Select(x =>
        {
            var inRun = sim.Log.Where(e => e.T >= x.T && e.T < x.T + x.Len).ToList();
            return new RunProbe(x.Name, atStart[x.Name], inRun.Count(e => e.What == "Acceleration"),
                inRun.Count(e => RdmSim.Defs.Values.Any(d => d.Name == e.What && d.Kind == AKind.Gcd)), inRun.Count(e => e.What.StartsWith("CANCELLED")));
        }).ToList();
        var first = string.Join(", ", sim.Log.Where(e => e.T <= 12000 && RdmSim.Defs.Values.Any(d => d.Name == e.What && d.Kind == AKind.Gcd)).Select(e => $"{SageSim.F(e.T)} {e.What}").Take(8));
        return new SimRun(sim.Damage.Sum(d => d.Potency * d.Mult), sim.Damage.Where(d => d.T < 30000).Sum(d => d.Potency * d.Mult), sim.Gcds,
            sim.Uses.GetValueOrDefault("Acceleration"), sim.Uses.GetValueOrDefault("Grand Impact"), sim.Cancelled, sim.Expired.Values.Sum(),
            sim.RuleFires.ToDictionary(kv => kv.Key, kv => (double)kv.Value), first, probes);
    }

    public sealed record Outcome(string Key, string Name, uint Id, double ActiveMin, double EndS, int Countdown, double RealPot, int RealGcds, int RealAccel, int RealGi,
        int RealCancels, SimRun Base, SimRun? Alt, string Phase);

    private static SimRun Mean(List<SimRun> l) => new(l.Average(x => x.Pot), l.Average(x => x.Pot30), l.Average(x => x.Gcds), l.Average(x => x.Accel), l.Average(x => x.Gi),
        l.Average(x => x.Cancels), l.Average(x => x.Expired),
        l.SelectMany(x => x.Fires).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value) / l.Count), l[0].FirstGcds,
        l.SelectMany(x => x.Runs ?? []).ToList());

    private static Outcome? One(PullSummary s, Actor rdm, PullReplay r, string rulesDir, int seeds, string key)
    {
        var w = new World(r, rdm.Id);
        var gcd = MeasureGcd(w);
        var end = EndOf(r);
        var real = Actual(w, end);
        var set = Opt("set", "");
        var ab = Opt("ab", "");
        var baseRuns = new List<SimRun>(); var altRuns = new List<SimRun>();
        var avail = 0;
        for (var seed = 1; seed <= seeds; seed++)
        {
            var sd = seed * 7919 + s.Ordinal;
            baseRuns.Add(Simulate(s, w, r, rulesDir, set, sd, gcd));
            if (ab != "") altRuns.Add(Simulate(s, w, r, rulesDir, set == "" ? ab : set + "," + ab, sd, gcd));
        }
        // Active time: alive, a target up, not incapacitated (from a probe sim, which only reads the World).
        var probe = new RdmSim(w, gcd, 1) { T = 0 };
        while (probe.T < end) probe.Advance(Math.Min(100, end - probe.T));
        avail = probe.AvailMs;
        return new Outcome(key, rdm.Name, rdm.Id, avail / 60000.0, end / 1000.0, s.CountdownSeconds, real.Pot, real.Gcds, real.Accel, real.Gi, real.Cancels,
            Mean(baseRuns), altRuns.Count > 0 ? Mean(altRuns) : null, r.PhaseAt(end - 1));
    }

    private static int Run(EncounterRegistry registry)
    {
        var rulesDir = Opt("rules", RepoRules);
        var seeds = int.Parse(Opt("seeds", "5"));
        var minActive = double.Parse(Opt("min-active", "60")) / 60;
        uint? me = Opt("me", "") is { Length: > 0 } m ? Convert.ToUInt32(m, 16) : null;
        var jobs = new List<(PullSummary S, string Key)>();
        foreach (var path in Logs(A[1]))
        {
            FileIndex idx;
            try { idx = Index(path, registry); } catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
            foreach (var s in idx.Pulls.Where(p => p.ZoneId == 1363 && p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond
                                                    && p.Party.Any(x => x.Job == RdmJob && (me is null || x.Id == me))))
                jobs.Add((s, $"{Path.GetFileNameWithoutExtension(path)[^8..]}_{s.Ordinal:D3}"));
        }
        Console.Error.WriteLine($"{jobs.Count} pulls; rules {rulesDir}; set {Opt("set", "-")}; ab {Opt("ab", "-")}; seeds {seeds}");
        var results = new ConcurrentBag<Outcome>();
        var done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, job =>
        {
            PullReplay r;
            try { r = PullLoader.Load(job.S, null, registry); } catch { return; }
            var rdms = r.Party.Count > 0 ? r.Party : r.Players.ToList();
            foreach (var d in rdms.Where(x => x.Job == RdmJob && (me is null || x.Id == me)))
            {
                try { if (One(job.S, d, r, rulesDir, seeds, job.Key) is { } o) results.Add(o); }
                catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); }
                catch (Exception e) { Console.Error.WriteLine($"{job.Key} {d.Name}: {e.GetType().Name} {e.Message}"); }
            }
            if (Interlocked.Increment(ref done) % 50 == 0) Console.Error.WriteLine($"  {done}/{jobs.Count}");
        });
        var R = results.Where(o => o.ActiveMin >= minActive && o.RealPot > 0).OrderBy(o => o.Key).ToList();
        var cds = R.GroupBy(o => o.Countdown).OrderBy(g => g.Key).Select(g => $"{g.Key} s: {g.Count()}");
        Console.WriteLine($"\n{R.Count} Red Mage pulls with >= {minActive * 60:0} s active ({results.Count} total), {seeds} seeds, rules {Path.GetFileName(rulesDir)}, set {Opt("set", "-")}");
        Console.WriteLine($"countdowns: {string.Join(", ", cds)}");
        if (A.Contains("--detail"))
            foreach (var o in R)
                Console.WriteLine($"{o.Key} {o.Name,-20} {o.ActiveMin,5:0.0}m cd {o.Countdown,2} {o.Phase,-20} pot/min sim {o.Base.Pot / o.ActiveMin,7:0} real {o.RealPot / o.ActiveMin,7:0} ({100 * (o.Base.Pot / o.RealPot - 1),+6:0.0}%)" +
                    (o.Alt is { } alt ? $"  alt {alt.Pot / o.ActiveMin,7:0} ({100 * (alt.Pot / o.Base.Pot - 1),+6:0.00}%)  accel {o.Base.Accel:0.0}/{alt.Accel:0.0} GI {o.Base.Gi:0.0}/{alt.Gi:0.0}" : "") +
                    $"  gcd {o.Base.Gcds:0}/{o.RealGcds} cancels {o.Base.Cancels:0.0}/{o.RealCancels}");
        Console.WriteLine($"{"Red Mage",-22} {"pulls",5} {"min",6} {"pot/min sim",11} {"real",7} {"diff",7} {"GCD/min sim",11} {"real",6} {"Accel/10m sim",13} {"real",5} {"GI/10m sim",10} {"real",5} {"cancel/10m sim",14} {"real",5}");
        void Line(string name, List<Outcome> L)
        {
            var act = L.Sum(o => o.ActiveMin);
            double sp = L.Sum(o => o.Base.Pot), rp = L.Sum(o => o.RealPot);
            Console.WriteLine($"{name,-22} {L.Count,5} {act,6:0} {sp / act,11:0} {rp / act,7:0} {100 * (sp / rp - 1),+6:0.0}% {L.Sum(o => o.Base.Gcds) / act,11:0.00} {L.Sum(o => o.RealGcds) / act,6:0.00} " +
                              $"{10 * L.Sum(o => o.Base.Accel) / act,13:0.0} {10 * L.Sum(o => o.RealAccel) / act,5:0.0} {10 * L.Sum(o => o.Base.Gi) / act,10:0.0} {10 * L.Sum(o => o.RealGi) / act,5:0.0} " +
                              $"{10 * L.Sum(o => o.Base.Cancels) / act,14:0.0} {10 * L.Sum(o => o.RealCancels) / act,5:0.0}");
        }
        foreach (var g in R.GroupBy(o => o.Name).OrderByDescending(g => g.Sum(o => o.RealPot) / g.Sum(o => o.ActiveMin))) Line(g.Key, g.ToList());
        Line("ALL", R);
        if (R.Any(o => o.Alt is not null))
        {
            Console.WriteLine($"\nA/B: --ab={Opt("ab", "")} against the base (same pulls and seeds)");
            void Ab(string name, List<Outcome> L)
            {
                if (L.Count == 0) return;
                var act = L.Sum(o => o.ActiveMin);
                double b = L.Sum(o => o.Base.Pot), a = L.Sum(o => o.Alt!.Pot), b30 = L.Sum(o => o.Base.Pot30), a30 = L.Sum(o => o.Alt!.Pot30);
                var better = L.Count(o => o.Alt!.Pot > o.Base.Pot + 1); var worse = L.Count(o => o.Alt!.Pot < o.Base.Pot - 1);
                Console.WriteLine($"  {name,-30} {L.Count,4} pulls {act,6:0} min: pot/min {b / act,7:0} -> {a / act,7:0} ({100 * (a / b - 1),+6:0.00}%), first 30 s {b30 / L.Count,7:0} -> {a30 / L.Count,7:0} per pull, " +
                                  $"Acceleration {L.Sum(o => o.Base.Accel) / L.Count:0.00} -> {L.Sum(o => o.Alt!.Accel) / L.Count:0.00}, Grand Impact {L.Sum(o => o.Base.Gi) / L.Count:0.00} -> {L.Sum(o => o.Alt!.Gi) / L.Count:0.00} per pull; better {better}, worse {worse}");
            }
            Ab("all", R);
            Ab("countdown under 19 s", R.Where(o => o.Countdown is > 0 and < 19).ToList());
            Ab("countdown 19 s or more", R.Where(o => o.Countdown >= 19).ToList());
            Ab("pulls under 3:20 (Phase 1)", R.Where(o => o.EndS < 200).ToList());
            Ab("pulls 3:20 to 9:00", R.Where(o => o.EndS is >= 200 and < 540).ToList());
            Ab("pulls 9:00 or more", R.Where(o => o.EndS >= 540).ToList());
            foreach (var g in R.GroupBy(o => o.Name).Where(g => g.Count() >= 3).OrderByDescending(g => g.Count())) Ab(g.Key, g.ToList());
            // The two long runs: Acceleration charges at the start, Accelerations, GCDs and cancels in the 12 s after (every seed).
            foreach (var name in R.SelectMany(o => o.Base.Runs ?? []).Select(x => x.Name).Distinct())
            {
                string Sum(IEnumerable<RunProbe> ps)
                {
                    var p = ps.Where(x => x.Name == name).ToList();
                    if (p.Count == 0) return "-";
                    return $"charges 2/1/0: {100.0 * p.Count(x => x.Charges >= 2) / p.Count:0}%/{100.0 * p.Count(x => x.Charges == 1) / p.Count:0}%/{100.0 * p.Count(x => x.Charges == 0) / p.Count:0}%, " +
                           $"Acceleration {p.Average(x => x.Accel):0.00}, GCDs {p.Average(x => x.Gcds):0.00}, cancels {p.Average(x => x.Cancels):0.00} ({p.Count} runs)";
                }
                Console.WriteLine($"  {name}: base {Sum(R.SelectMany(o => o.Base.Runs ?? []))}");
                Console.WriteLine($"  {"",-23} alt  {Sum(R.SelectMany(o => o.Alt!.Runs ?? []))}");
            }
            var ex = R.Where(o => o.Alt is not null).OrderByDescending(o => o.EndS).First();
            Console.WriteLine($"  first GCDs, base: {ex.Base.FirstGcds}\n  first GCDs, alt:  {ex.Alt!.FirstGcds}   ({ex.Key} {ex.Name})");
        }
        if (A.Contains("--fires"))
            foreach (var kv in R.SelectMany(o => o.Base.Fires).GroupBy(kv => kv.Key).Select(g => (g.Key, N: g.Sum(x => x.Value))).OrderByDescending(x => x.N))
                Console.WriteLine($"  {kv.N,9:0} {kv.Key}");
        return 0;
    }

    private static int Timeline(EncounterRegistry registry)
    {
        var path = Logs(A[1])[0];
        var ordinal = int.Parse(A[2]);
        var meId = Convert.ToUInt32(Opt("me", "1001A4A8"), 16);
        var s = Index(path, registry).Pulls.First(p => p.Ordinal == ordinal);
        var r = PullLoader.Load(s, null, registry);
        var w = new World(r, meId);
        int from = (int)(double.Parse(Opt("from", "-30")) * 1000), to = (int)(double.Parse(Opt("to", "9999")) * 1000);
        var sim = new RdmSim(w, MeasureGcd(w), int.Parse(Opt("seed", "1")) * 7919 + s.Ordinal) { Trace = A.Contains("--trace"), TraceFrom = from, TraceTo = to };
        var files = Compile(sim, Opt("rules", RepoRules), Opt("set", ""));
        sim.T = StartMs(s, w);
        while (sim.T < r.EndMs)
        {
            sim.Press(files);
            var next = Math.Min(sim.NextWake(40), r.EndMs);
            if (next <= sim.T) break;
            sim.Advance(next - sim.T);
        }
        Console.WriteLine($"{Path.GetFileName(path)} #{ordinal} {w.Me.Name} gcd {sim.GcdMs} countdown {s.CountdownSeconds}; sim left, real right");
        var real = r.Actions.Where(a => a.Source.Id == meId && a.T >= from && a.T <= to && a.Name != "attack")
            .Select(a => (a.T, Text: $"{"",-64} | {a.Name}"))
            .Concat(r.Casts.Where(c => c.Source.Id == meId && c.StartMs >= from && c.StartMs <= to && c.Outcome == CastOutcome.Cancelled).Select(c => (T: c.StartMs, Text: $"{"",-64} | {c.Name} (cancelled)"))).ToList();
        var mine = sim.Log.Where(e => e.T >= from && e.T <= to).Select(e => (e.T, Text: $"{e.What,-28} {e.Rule.Split(": ").Last(),-35}|")).ToList();
        foreach (var (t, text) in mine.Concat(real).OrderBy(x => x.T)) Console.WriteLine($"{SageSim.F(t),8} {text}");
        Console.WriteLine($"sim potency {sim.Damage.Sum(d => d.Potency * d.Mult):0}, GCDs {sim.Gcds}, cancelled {sim.Cancelled}, uses: {string.Join(", ", sim.Uses.Where(kv => kv.Key is "Acceleration" or "Grand Impact" or "Swiftcast" or "Manafication" or "Embolden").Select(kv => $"{kv.Key} {kv.Value}"))}; expired: {string.Join(", ", sim.Expired.Select(kv => $"{kv.Key} {kv.Value}"))}");
        var act = Actual(w);
        Console.WriteLine($"real potency {act.Pot:0}, GCDs {act.Gcds}, Acceleration {act.Accel}, Grand Impact {act.Gi}, cancelled {act.Cancels}");
        return 0;
    }
}
