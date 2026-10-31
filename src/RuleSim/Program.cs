using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

// Replays logged Sage pulls through the Custom Rules files: the world (boss casts, positions and movement, party HP,
// other players' buffs, hits, deaths) comes from the ACT log via RaidReplay.Core; the Sage's actions come from the rules.
//
//   RuleSim pulls <log>                                   list pulls
//   RuleSim calibrate <log> <pulls>                       movement threshold vs the user's real cast cancels
//   RuleSim run <log> <pulls> [--rules=DIR] [--me=HEX]    sim vs actual, per pull and in total
//   RuleSim timeline <log> <pull> [--from=s] [--to=s]     merged timeline: sim, actual, boss casts, raidwides
// <pulls> = "27,30,34" or "27-49" or "sge" (every Dancing Mad pull where the user is on Sage).
var cmd = args.Length > 0 ? args[0] : "help";
string Opt(string name, string def) => args.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
var cache = Path.Combine(AppContext.BaseDirectory, "cache");
Directory.CreateDirectory(cache);
var meId = Convert.ToUInt32(Opt("me", "1001A4A8"), 16);
var rulesDir = Opt("rules", @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples");
var symbolsPath = Path.Combine(AppContext.BaseDirectory, "sge_symbols.json");
if (!File.Exists(symbolsPath)) symbolsPath = Path.Combine(Directory.GetCurrentDirectory(), "sge_symbols.json");
var moveSpeed = double.Parse(Opt("move", "1.5"));
var registry = EncounterRegistry.Load();

FileIndex Index(string path)
{
    var prev = IndexStore.Load(cache, path);
    var idx = IndexStore.IndexFile(path, prev, new EncounterObserverFactory(registry), registry.HashFor);
    if (!ReferenceEquals(idx, prev)) IndexStore.Save(cache, idx);
    return idx;
}

List<PullSummary> Select(FileIndex idx, string spec)
{
    var all = idx.Pulls.Where(p => p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond).ToList();
    if (spec == "sge") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId && m.Job == 40)).ToList();
    var want = new HashSet<int>();
    foreach (var part in spec.Split(','))
        if (part.Contains('-')) { var x = part.Split('-'); for (var i = int.Parse(x[0]); i <= int.Parse(x[1]); i++) want.Add(i); }
        else want.Add(int.Parse(part));
    return all.Where(p => want.Contains(p.Ordinal)).ToList();
}

List<CompiledRuleSet> Compile(SageSim sim, uint trigger)
{
    var symbols = new SimSymbols(symbolsPath, sim);
    var sets = new List<CompiledRuleSet>();
    foreach (var path in Directory.GetFiles(rulesDir, "*.json").OrderBy(p => p))
    {
        var file = JsonConvert.DeserializeObject<RuleSetFile>(File.ReadAllText(path));
        if (file is null || !string.Equals(file.Job, "SGE", StringComparison.OrdinalIgnoreCase)) continue;
        // --set=name=value,... overrides defines (any file that defines the name), for A/B runs.
        foreach (var kv in Opt("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
            if (file.Define.ContainsKey(k)) file.Define[k] = v;
        }
        var errors = new List<string>();
        var set = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors);
        if (set is null) { Console.WriteLine($"  {Path.GetFileName(path)}: {string.Join("; ", errors)}"); continue; }
        var ids = file.Replaces.Select(n => symbols.TryResolveAction(n, out var id) ? id : 0).ToHashSet();
        if (ids.Contains(A.Dosis) || ids.Contains(A.Dosis3) || ids.Contains(trigger)) sets.Add(set);
    }
    // Layers first (name order), then the button's file, as RuleEngine.FilesFor does.
    return sets.OrderBy(s => s.Layer ? 0 : 1).ThenBy(s => s.Name).ToList();
}

int MeasureGcd(World w)
{
    var starts = w.R.Casts.Where(c => c.Source.Id == meId && c.Name == "Dosis III").Select(c => c.StartMs).OrderBy(x => x).ToList();
    var iv = starts.Zip(starts.Skip(1), (a, b) => b - a).Where(d => d is >= 2200 and <= 2700).OrderBy(d => d).ToList();
    return iv.Count >= 5 ? iv[iv.Count / 2] : 2500;
}

(SageSim Sim, World W, int ActiveMs) Simulate(PullSummary s)
{
    var r = PullLoader.Load(s, null, registry);
    var w = new World(r, meId) { MoveSpeed = moveSpeed, MinStep = double.Parse(Opt("step", "0.25")), RunSpeed = double.Parse(Opt("run", "5")) };
    var sim = new SageSim(w, MeasureGcd(w)) { T = -1000, SlideMs = int.Parse(Opt("slide", "700")), Trace = args.Contains("--trace"), TraceFrom = (int)(double.Parse(Opt("from", "-5")) * 1000), TraceTo = (int)(double.Parse(Opt("to", "9999")) * 1000) };
    var files = Compile(sim, A.Dosis3);
    var active = 0;
    var end = r.EndMs;
    while (sim.T < end)
    {
        sim.Advance(20);
        sim.Press(files, A.Dosis3);
        if (sim.T >= 0 && !w.Dead(w.Me, sim.T) && sim.Target is not null) active += 20;
    }
    return (sim, w, active);
}

var Pot = new Dictionary<string, int>
{
    ["Dosis III"] = 370, ["Phlegma III"] = 600, ["Toxikon II"] = 330, ["Psyche"] = 600, ["Pneuma"] = 330, ["Dyskrasia II"] = 170,
};
HashSet<string> GcdNames = ["Dosis III", "Eukrasian Dosis III", "Phlegma III", "Toxikon II", "Pneuma", "Dyskrasia II",
    "Eukrasian Dyskrasia", "Diagnosis", "Eukrasian Diagnosis", "Prognosis", "Eukrasian Prognosis II", "Egeiro", "Esuna"];

// The user's real play, scored the same way.
Actual ActualOf(World w)
{
    var a = new Actual();
    var mine = w.R.Actions.Where(x => x.Source.Id == meId).GroupBy(x => x.Sequence != 0 ? (long)x.Sequence : ((long)x.ActionId << 32) + x.T).Select(g => g.First()).OrderBy(x => x.T).ToList();
    foreach (var x in mine)
    {
        a.Counts[x.Name] = a.Counts.GetValueOrDefault(x.Name) + 1;
        if (GcdNames.Contains(x.Name) && x.T >= 0) a.Gcds++;
        var tgt = x.PrimaryTarget ?? x.Hits.FirstOrDefault()?.Target;
        if (Pot.TryGetValue(x.Name, out var p) && x.Hits.Any(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage))
            a.Damage.Add((x.T, x.Name, p, w.RaidBuffs(tgt is { IsPlayer: false } ? tgt : w.Target(x.T), x.T).Mult));
        a.Events.Add((x.T, x.Name, tgt is { IsPlayer: true } && tgt.Id != meId ? tgt.Name : ""));
    }
    foreach (var st in w.R.Statuses.Where(s => s.Source?.Id == meId && s.Name == "Eukrasian Dosis III" && !s.Target.IsPlayer))
    {
        var mult = w.RaidBuffs(st.Target, st.StartMs).Mult;
        a.DotApps.Add(st.StartMs);
        for (var t = st.StartMs + 1500; t < Math.Min(st.EndMs, w.R.EndMs); t += 3000)
            if (st.Target.IsTargetable(t)) a.Damage.Add((t, "Eukrasian Dosis III (tick)", 75, mult));
    }
    a.Cancels = w.R.Casts.Count(c => c.Source.Id == meId && c.Outcome == CastOutcome.Cancelled);
    a.Mine = w.R.Statuses.Where(s => s.Source?.Id == meId).ToList();
    return a;
}

string Cover(Func<string, bool> has) =>
    string.Join(" ", new[] { ("Kerachole", "Kera"), ("Holosakos", "Holos"), ("Panhaima", "Panh"), ("Eudaimonia", "Philo"), ("Physis II", "Phys"), ("Eukrasian Prognosis", "Shield") }
        .Where(x => has(x.Item1)).Select(x => x.Item2));

// Dancer: "run all|<log> dnc ..." / "timeline <log> <pull> dnc --me=HEX"; see DncCommands.cs.
if (DncCommands.Handles(args)) return DncCommands.Main(args, registry, Index);

// Astrologian: "run <log|all> ast ..." (the --me player's Astrologian pulls) or --job=ast (a stand-in); see AstCommands.cs.
if (AstCommands.Handles(args)) return AstCommands.Main(args, registry, Index);

// White Mage: "run <log> whm ..." (the --me player's White Mage pulls) or --job=whm (a stand-in); see WhmCommands.cs.
if (WhmCommands.Handles(args)) return WhmCommands.Main(args, registry, Index);

// Samurai: "run <log> sam ..." (the --me player's Samurai pulls) or --job=sam (a stand-in); see SamCommands.cs.
if (SamCommands.Handles(args)) return SamCommands.Main(args, registry, Index);

switch (cmd)
{
    case "pulls":
    {
        var idx = Index(args[1]);
        foreach (var p in Select(idx, args.Length > 2 ? args[2] : "sge"))
            Console.WriteLine($"#{p.Ordinal,-4} {new DateTime(p.StartTicks).ToLocalTime():MM-dd HH:mm:ss} {p.Outcome,-8} {(p.EndTicks - p.StartTicks) / 1e7,6:N1}s {p.FurthestPhase}");
        return 0;
    }
    case "calibrate":
    {
        var idx = Index(args[1]);
        var casts = new List<(World W, CastEvent C)>();
        foreach (var s in Select(idx, args[2]))
        {
            var w = new World(PullLoader.Load(s, null, registry), meId);
            foreach (var c in w.R.Casts.Where(c => c.Source.Id == meId && c.DurationMs >= 1000 && c.StartMs > 0))
                casts.Add((w, c));
        }
        var done = casts.Count(x => x.C.Outcome != CastOutcome.Cancelled);
        var canc = casts.Count - done;
        Console.WriteLine($"{done} completed casts, {canc} cancelled; columns: false cancels of completed casts / real cancels seen");
        void Try(string label, Func<World, int, bool> moving)
        {
            var line = $"  {label,-26}";
            foreach (var slide in new[] { 300, 500, 700, 900, 1100 })
            {
                int fp = 0, tp = 0;
                foreach (var (w, c) in casts)
                {
                    var cancelled = c.Outcome == CastOutcome.Cancelled;
                    var stop = cancelled ? Math.Min(c.EndMs + 300, c.StartMs + c.DurationMs - slide) : c.EndMs - slide;
                    var any = false;
                    for (var t = c.StartMs + 100; t <= stop && !any; t += 25) any = moving(w, t);
                    if (cancelled && any) tp++;
                    if (!cancelled && any) fp++;
                }
                line += $"  slide {slide}: {100.0 * fp / done,4:0.0}% / {100.0 * tp / canc,3:0}%";
            }
            Console.WriteLine(line);
        }
        foreach (var th in new[] { 1.0, 2.0, 3.0 })
            Try($"interpolated > {th} y/s", (w, t) => w.Speed(w.Me, t) > th);
        foreach (var run in new[] { 5.0, 6.0, 7.5 })
            foreach (var step in new[] { 0.25, 0.5 })
                Try($"burst {run} y/s, step {step} y", (w, t) => { w.RunSpeed = run; w.MinStep = step; return w.MovingBurst(w.Me, t); });
        return 0;
    }
    case "run":
    {
        var idx = Index(args[1]);
        var pulls = Select(idx, args[2]);
        var sheet = Metrics.LoadSheet(Opt("sheet", @"C:\Users\quanh\Workspace\WrathCombo\docs\guides\DMU"));
        double simPot = 0, actPot = 0, act = 0; int simG = 0, actG = 0, simC = 0, actC = 0;
        var anomalyKinds = new Dictionary<string, int>();
        var cover = new Dictionary<string, (int N, int SimKera, int ActKera, int SimShield, int ActShield, int SimAny, int ActAny)>();
        var fires = new Dictionary<string, int>();
        var done = new List<(PullSummary S, SageSim Sim, World W, Actual A)>();
        int bigN = 0, bigSimShield = 0, bigActShield = 0, bigSimAny = 0, bigActAny = 0, minMp = 10000;
        var allSlots = new List<Metrics.Slot>();
        long calRaw = 0, calPot = 0;
        foreach (var s in pulls)
        {
            var (sim, w, activeMs) = Simulate(s);
            var a = ActualOf(w);
            done.Add((s, sim, w, a));
            var cal = Metrics.CalibrationSample(w); calRaw += cal.Raw; calPot += cal.Pot;
            minMp = Math.Min(minMp, sim.MinMp);
            var mins = activeMs / 60000.0;
            double sp = sim.Damage.Sum(d => d.Potency * d.Mult), ap = a.Damage.Sum(d => d.Potency * d.Mult);
            simPot += sp; actPot += ap; act += activeMs; simG += sim.Gcds; actG += a.Gcds; simC += sim.Cancels; actC += a.Cancels;
            foreach (var (k, v) in sim.RuleFires) fires[k] = fires.GetValueOrDefault(k) + v;
            Console.WriteLine($"\n== pull #{s.Ordinal} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm} {SageSim.F(w.R.EndMs)} (target up and alive {activeMs / 1000.0:0}s), GCD {sim.GcdMs} ms");
            Console.WriteLine($"   {"",-26}{"rules",9}{"you",9}");
            void Row(string n, double x, double y, string fmt = "0") => Console.WriteLine($"   {n,-26}{x.ToString(fmt),9}{y.ToString(fmt),9}");
            Row("potency per minute", sp / Math.Max(0.01, mins), ap / Math.Max(0.01, mins));
            // 2026-10-28: Phase 3's two-boss stretch (Bowels of Agony, Implosion, Umbra Smash segments: about 7:26-8:35).
            {
                var segs = w.R.Phases.Where(ph => ph.IsSegment && ph.Name is "Bowels of Agony" or "Implosion" or "Umbra Smash + Ultima Blaster").ToList();
                if (segs.Count > 0)
                {
                    int s0 = segs.Min(x => x.StartMs), s1 = Math.Min(segs.Max(x => x.EndMs), w.R.EndMs);
                    var aliveMs = 0; for (var t = s0; t < s1; t += 100) if (!w.Dead(w.Me, t)) aliveMs += 100;
                    var p3s = sim.Damage.Where(dd => dd.T >= s0 && dd.T < s1).Sum(dd => dd.Potency * dd.Mult);
                    var p3a = a.Damage.Where(dd => dd.T >= s0 && dd.T < s1).Sum(dd => dd.Potency * dd.Mult);
                    var heals = sim.Log.Where(e => e.T >= s0 && e.T < s1 && e.What is "Diagnosis" or "Eukrasian Diagnosis" or "Prognosis").GroupBy(e => e.What).Select(g => $"{g.Key} {g.Count()}");
                    Console.WriteLine($"   p3window {SageSim.F(s0)}-{SageSim.F(s1)} alive {aliveMs / 1000.0:0} s potency rules {p3s:0} you {p3a:0}; GCD heals {string.Join(", ", heals)}");
                }
            }
            Row("GCDs per minute", sim.Gcds / Math.Max(0.01, mins), a.Gcds / Math.Max(0.01, mins), "0.0");
            Row("cast cancels", sim.Cancels, a.Cancels);
            Row("DoT applications", sim.DotApplications.Count, a.DotApps.Count);
            Row("DoT ticks", sim.Damage.Count(d => d.Src.EndsWith("(tick)")), a.Damage.Count(d => d.Src.EndsWith("(tick)")));
            foreach (var n in new[] { "Phlegma III", "Psyche", "Toxikon II", "Pneuma", "Eukrasian Prognosis II", "Eukrasian Diagnosis", "Kerachole", "Ixochole", "Taurochole", "Druochole", "Physis II", "Holos", "Panhaima", "Philosophia", "Zoe", "Haima", "Kardia", "Lucid Dreaming", "Rhizomata", "Pepsis", "Dyskrasia II" })
                Row(n, sim.Log.Count(e => e.What == n), a.Counts.GetValueOrDefault(n));
            var a8 = Metrics.AllEightCoverage(sim, w);
            Console.WriteLine($"   all-8 moments {a8.N}: players with the rules' shield {a8.Shielded}/{a8.Players}, everyone shielded {a8.All}/{a8.N}");
            // EPROG_LOG=1: Graven Image casts, and every Zoe / Eukrasia / Eukrasian Prognosis II press with its rule (2026-10-27 audit)
            if (Environment.GetEnvironmentVariable("EPROG_LOG") == "1")
            {
                Console.WriteLine("   gicasts " + string.Join(" ", w.CastStarts("Graven Image").Select(SageSim.F)));
                foreach (var e in sim.Log.Where(e => e.What is "Eukrasian Prognosis II" or "Zoe" or "Eukrasia" or "Pneuma" or "Eukrasian Diagnosis" or "Diagnosis" or "Prognosis"))
                    Console.WriteLine($"   press {SageSim.F(e.T)} {e.What} | {e.Rule}");
            }
            if (Environment.GetEnvironmentVariable("SHIELD_AT") is { Length: > 0 } sa)
                foreach (var h in w.HitsOnParty.Where(h => !h.Auto && sa.Split(',').Contains(h.Name)))
                    Console.WriteLine($"   shieldat {h.Name} {SageSim.F(h.T)} {h.Target.Name.Split(' ')[0]} {h.InstPct:0}% rules={(Metrics.SimShieldOn(sim, w, h.Target, h.T) ? 1 : 0)} dist={w.Dist(w.Me, h.Target, h.T):0}");
            Console.WriteLine($"   rules: GCD idle while able {sim.IdleMs / 1000.0:0}s, Addersgall at 3 for {sim.GallCappedMs / 1000.0:0}s, Addersting at 3 for {sim.StingCappedMs / 1000.0:0}s ({sim.StingLost} lost), MP low {sim.MinMp}");
            foreach (var rw in w.Raidwides)
            {
                var t = rw.T;
                bool ActHas(string n) => a.Mine.Any(x => x.Target.Id == meId && x.Name == n && x.Active(t));
                var simCov = Metrics.SimCover(sim, w, t);
                var actCov = Cover(ActHas);
                var key = $"{rw.Name} #{rw.Ordinal}";
                var c = cover.GetValueOrDefault(key);
                cover[key] = (c.N + 1, c.SimKera + (simCov.Contains("Kera") ? 1 : 0), c.ActKera + (actCov.Contains("Kera") ? 1 : 0),
                    c.SimShield + (simCov.Contains("Shield") ? 1 : 0), c.ActShield + (actCov.Contains("Shield") ? 1 : 0),
                    c.SimAny + (simCov.Length > 0 ? 1 : 0), c.ActAny + (actCov.Length > 0 ? 1 : 0));
                if (rw.AvgPct >= 20)
                {
                    bigN++;
                    bigSimShield += simCov.Contains("Shield") ? 1 : 0; bigActShield += actCov.Contains("Shield") ? 1 : 0;
                    bigSimAny += simCov.Length > 0 ? 1 : 0; bigActAny += actCov.Length > 0 ? 1 : 0;
                }
                Console.WriteLine($"   {SageSim.F(t),7} {key,-28} {rw.AvgPct,3:0}% of HP  rules: {simCov,-30} you: {actCov}");
            }
            var slots = Metrics.Slots(w, sim, a.Mine, a.Events, sheet);
            allSlots.AddRange(slots);
            if (slots.Count > 0)
                Console.WriteLine($"   sheet slots (Sage column): rules {slots.Count(x => x.Sim)}/{slots.Count}, you {slots.Count(x => x.Real)}/{slots.Count}");
            if (args.Contains("--slots") && slots.Count > 0) Console.WriteLine("   " + Metrics.PanhaimaCheck(w, a.Mine, slots));
            if (args.Contains("--slotlist"))
                foreach (var x in slots)
                    Console.WriteLine($"   slot {x.Phase} {SageSim.F(x.T),7} {x.Mech,-30} {x.Label,-14} rules {(x.Sim ? "yes" : "NO ")} you {(x.Real ? "yes" : "NO ")}");
            if (args.Contains("--shields"))
            {
                // What took each of the rules' party shields: the first non-auto hit on the Sage after it landed, by that
                // hit's party average (% of HP); "none" when it ran out unused.
                var kinds = new Dictionary<string, int>();
                foreach (var e in sim.Log.Where(e => e.What == "Eukrasian Prognosis II"))
                {
                    var from = e.T + Metrics.LandMs;
                    var next = sim.Log.FirstOrDefault(x => x.What == "Eukrasian Prognosis II" && x.T > e.T)?.T ?? int.MaxValue;
                    var h = w.HitsOnParty.FirstOrDefault(h => h.Target.Id == meId && !h.Auto && h.T >= from && h.T < from + 30000);
                    var k = h.Name is null ? "none" : h.T > next ? "replaced" : h.InstPct >= 20 ? "20%+" : h.InstPct >= 10 ? "10-20%" : "under 10%";
                    kinds[k] = kinds.GetValueOrDefault(k) + 1;
                    if (k is "none" or "under 10%" or "replaced")
                        Console.WriteLine($"     shield {SageSim.F(e.T)} ({e.Rule.Replace("DMU mitigation plan (Sage): ", "")}) -> {k}{(h.Name is null ? "" : $": {h.Name} {SageSim.F(h.T)} ({h.InstPct:0}%)")}");
                }
                Console.WriteLine("   shields taken by: " + string.Join(", ", kinds.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
            }
            if (args.Contains("--slots"))
                foreach (var x in slots)
                    Console.WriteLine($"     {SageSim.F(x.T),8} {x.Phase} {x.Mech,-36} {(x.Carry ? "> " : "")}{x.Label,-12} rules {(x.Sim ? "up" : "--")}  you {(x.Real ? "up" : "--")}");
            foreach (var an in sim.Anomalies)
            {
                Console.WriteLine($"   ! {an}");
                var kind = an[(an.IndexOf(' ') + 1)..];
                kind = kind.Contains("Eukrasia from:") ? "DoT refreshed early, Eukrasia from " + kind[(kind.IndexOf("Eukrasia from:") + 15)..] : new string(kind.TakeWhile(ch => ch != '(' && !char.IsDigit(ch)).ToArray()).Trim();
                anomalyKinds[kind] = anomalyKinds.GetValueOrDefault(kind) + 1;
            }
        }
        // Healing, with HP per potency calibrated on the real Sage's direct heals across these pulls.
        var hpPerPot = calPot >= 3000 ? (double)calRaw / calPot : 95.0;
        int segN = 0, segOk = 0, segCfWorse = 0, cumBehind = 0, pairOk = 0, pairN = 0; double realTot = 0, rulesTot = 0, worstRatio = 99, cfLow = 100, logLow = 100, worstCum = 99;
        foreach (var (s, sim, w, a) in done)
        {
            var segs = Metrics.Healing(w, sim, hpPerPot);
            // Cumulative since the pull, at each segment's end; and each pair of neighbouring segments together.
            double cr = 0, cq = 0;
            for (var i = 0; i < segs.Count; i++)
            {
                cr += segs[i].Real; cq += segs[i].Rules;
                if (cq < cr) cumBehind++;
                if (cr > 0) worstCum = Math.Min(worstCum, cq / cr);
                if (i > 0) { pairN++; if (segs[i].Rules + segs[i - 1].Rules >= segs[i].Real + segs[i - 1].Real) pairOk++; }
            }
            if (args.Contains("--heal")) Console.WriteLine($"   heal sources #{s.Ordinal}: {Metrics.Breakdown(w, sim, hpPerPot)}");
            foreach (var g in segs)
            {
                segN++; realTot += g.Real; rulesTot += g.Rules;
                if (g.Rules >= g.Real) segOk++;
                if (g.Real > 0) worstRatio = Math.Min(worstRatio, g.Rules / g.Real);
                cfLow = Math.Min(cfLow, g.CfMin); logLow = Math.Min(logLow, g.LogMin);
                if (g.CfMin < g.LogMin - 5) segCfWorse++;
                if (args.Contains("--heal"))
                    Console.WriteLine($"   heal #{s.Ordinal} {SageSim.F(g.Start),8}-{SageSim.F(g.End),-8} {g.Label,-30} you {g.Real / 1000,7:0}k  rules {g.Rules / 1000,7:0}k  {(g.Rules >= g.Real ? "ok " : "LOW")}  lowest HP logged {g.LogMin,4:0}%  counterfactual {g.CfMin,4:0}%" + (g.Rules < g.Real || g.CfMin < g.LogMin - 5 ? Environment.NewLine + "        " + g.Detail : ""));
            }
        }
        // 2026-10-28 tank care (TankMetrics.cs): the tanks' HP had the rules played the Sage.
        var tDeaths = new List<(int Pull, TankMetrics.Death D)>(); var tCf = new List<(int Pull, TankMetrics.CfDeath D)>();
        var tPh = new Dictionary<string, double[]>(); double tRulesLow = 0, tRealLow = 0;
        foreach (var (s, sim, w, a) in done)
        {
            var tr = TankMetrics.Run(w, sim, hpPerPot);
            tDeaths.AddRange(tr.Deaths.Select(d => (s.Ordinal, d))); tCf.AddRange(tr.CfDeaths.Select(d => (s.Ordinal, d)));
            tRulesLow += tr.RulesHealOnLowTank; tRealLow += tr.RealHealOnLowTank;
            foreach (var (k, v) in tr.ByPhase)
            {
                var x = tPh.TryGetValue(k, out var o) ? o : tPh[k] = [0, 0, 0, 0, 100, 100, 0, 0];
                x[0] += v.LogLow30; x[1] += v.CfLow30; x[2] += v.LogLow15; x[3] += v.CfLow15; x[4] = Math.Min(x[4], v.CfMin); x[5] = Math.Min(x[5], v.LogMin); x[6]++; x[7] += v.CfDead;
            }
            if (args.Contains("--tanks"))
            {
                foreach (var d in tr.Deaths) Console.WriteLine($"   tank #{s.Ordinal} {SageSim.F(d.T)} {d.Phase} [{d.Segment}] {d.Tank} died to {d.Cause}: rules {(d.Survives ? "SURVIVE" : "die")} ({d.MarginPct:+0;-0}% after the hit)");
                foreach (var d in tr.CfDeaths) Console.WriteLine($"   tank #{s.Ordinal} {SageSim.F(d.T)} {d.Phase} {d.Tank} RULES WOULD LOSE THEM: {d.Hit}");
                foreach (var (k, v) in tr.ByPhase.Where(kv => !kv.Key.Contains(' ')).OrderBy(kv => kv.Key))
                    Console.WriteLine($"   tanksec #{s.Ordinal} {k} u30 {v.LogLow30:0.0} {v.CfLow30:0.0} u15 {v.LogLow15:0.0} {v.CfLow15:0.0} min {v.LogMin:0} {v.CfMin:0} dead {v.CfDead:0.0}");
            }
        }
        var m = act / 60000.0;
        Console.WriteLine($"\n===== {pulls.Count} pulls, {m:0.0} active minutes");
        Console.WriteLine($"   tanks: logged deaths (not in a wipe) {tDeaths.Count}, the rules survive the killing blow in {tDeaths.Count(x => x.D.Survives)}; tanks the rules would lose that lived {tCf.Count}; rules' heals on a tank under 50% {tRulesLow / 1e6:0.00}M vs the real Sage {tRealLow / 1e6:0.00}M");
        foreach (var (k, v) in tPh.OrderBy(x => x.Key))
            Console.WriteLine($"     tank-seconds {k,-40} under 30%: logged {v[0],6:0} rules {v[1],6:0}   under 15%: logged {v[2],6:0} rules {v[3],6:0}   lowest: logged {v[5],3:0}% rules {v[4],3:0}%   rules-dead {v[7],5:0}  (pulls {v[6]})");
        foreach (var (pull, d) in tDeaths) Console.WriteLine($"     death #{pull} {SageSim.F(d.T)} {d.Phase} [{d.Segment}] {d.Tank}: {d.Cause}; rules {(d.Survives ? "SURVIVE" : "die")} ({d.MarginPct:+0;-0}%)");
        foreach (var (pull, d) in tCf) Console.WriteLine($"     RULES LOSE #{pull} {SageSim.F(d.T)} {d.Phase} {d.Tank}: {d.Hit}");
        Console.WriteLine($"   potency per minute: rules {simPot / m:0}, you {actPot / m:0} ({100 * (simPot - actPot) / Math.Max(1, actPot):+0.0;-0.0}%)");
        Console.WriteLine($"   GCDs per minute: rules {simG / m:0.0}, you {actG / m:0.0}; cast cancels: rules {simC}, you {actC}; rules MP low {minMp}");
        Console.WriteLine($"   big hits (20%+ HP): {bigN}; Sage shield up: rules {bigSimShield}, you {bigActShield}; any Sage mitigation: rules {bigSimAny}, you {bigActAny}");
        if (allSlots.Count > 0)
            Console.WriteLine($"   sheet slots: rules {allSlots.Count(x => x.Sim)}/{allSlots.Count}, you {allSlots.Count(x => x.Real)}/{allSlots.Count}  ("
                + string.Join(", ", allSlots.GroupBy(x => x.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count(x => x.Sim)}/{g.Count(x => x.Real)}/{g.Count()}")) + ")");
        Console.WriteLine($"   healing ({hpPerPot:0} HP per potency): segments where the rules heal at least the real Sage's effective healing {segOk}/{segN}; total rules {rulesTot / 1e6:0.00}M vs you {realTot / 1e6:0.00}M; worst segment ratio {worstRatio:0.00}; lowest counterfactual HP {cfLow:0}% (logged {logLow:0}%), segments 5+ points lower than logged {segCfWorse}");
        Console.WriteLine($"   healing, cumulative from the pull: behind the real Sage at {cumBehind}/{segN} segment ends (lowest ratio {worstCum:0.00}); neighbouring pairs at least the real Sage's {pairOk}/{pairN}");
        int ck = 0, cs = 0, ca = 0, ak = 0, ash = 0, aa = 0;
        foreach (var (_, c) in cover) { ck += c.SimKera; cs += c.SimShield; ca += c.SimAny; ak += c.ActKera; ash += c.ActShield; aa += c.ActAny; }
        Console.WriteLine($"   all raidwides: Kerachole rules {ck} / you {ak}; shield rules {cs} / you {ash}; anything rules {ca} / you {aa}");
        Console.WriteLine("   coverage at each raidwide (pulls with it: Kerachole / shield / anything, rules vs you):");
        foreach (var (k, c) in cover.OrderBy(kv => kv.Key))
            if (c.N >= 2) Console.WriteLine($"     {k,-30} n={c.N,2}  Kera {c.SimKera,2}/{c.ActKera,-2}  shield {c.SimShield,2}/{c.ActShield,-2}  any {c.SimAny,2}/{c.ActAny,-2}");
        Console.WriteLine("   anomalies: " + (anomalyKinds.Count == 0 ? "none" : string.Join("; ", anomalyKinds.OrderByDescending(x => x.Value).Select(x => $"{x.Key} x{x.Value}"))));
        Console.WriteLine("   rule fires: " + string.Join("; ", fires.OrderByDescending(x => x.Value).Take(120).Select(x => $"{x.Key} {x.Value}")));
        return 0;
    }
    case "bighits":
    {
        // For each party-wide hit of 20%+ HP: the hits on the Sage in the 12 s before (each breaks a replay shield), the enemy
        // casts running into it, and the real Sage's shields, Zoes and Kerachole presses around it.
        var idx = Index(args[1]);
        var s = Select(idx, args[2]).First();
        var w = new World(PullLoader.Load(s, null, registry), meId);
        var mine = w.R.Actions.Where(x => x.Source.Id == meId).ToList();
        foreach (var rw in w.Raidwides.Where(r => r.AvgPct >= double.Parse(Opt("pct", "20"))))
        {
            var t = rw.T;
            Console.WriteLine($"{SageSim.F(t),8} {rw.Name} #{rw.Ordinal} ({rw.AvgPct:0}%)");
            var prev = w.HitsOnParty.Where(h => h.Target.Id == meId && !h.Auto && h.T < t - 300 && h.T >= t - 12000).ToList();
            foreach (var h in prev)
            {
                var a = w.R.Actions.FirstOrDefault(x => x.Hits.Any(hh => hh.T == h.T && hh.Target.Id == meId));
                var hit = a?.Hits.First(hh => hh.T == h.T && hh.Target.Id == meId);
                Console.WriteLine($"           hit on Sage {SageSim.F(h.T)} ({(t - h.T) / 1000.0:0.0}s before) {a?.Name} {(hit is null || hit.MaxHp == 0 ? 0 : 100.0 * hit.Damage / hit.MaxHp):0}%");
            }
            var seen = new HashSet<string>();
            foreach (var c in w.R.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Helper or ActorKind.Enemy && c.StartMs <= t + 500 && c.StartMs >= t - 15000).OrderBy(c => c.StartMs))
                if (seen.Add(c.Name)) Console.WriteLine($"           cast {c.Name,-28} {SageSim.F(c.StartMs)} -> {SageSim.F(c.StartMs + c.DurationMs)} ({(c.StartMs + c.DurationMs - t) / 1000.0:+0.0;-0.0}s vs hit)");
            foreach (var x in mine.Where(x => x.T >= t - 15000 && x.T <= t + 1000 && x.Name is "Eukrasian Prognosis II" or "Zoe" or "Kerachole" or "Holos" or "Panhaima" or "Philosophia" or "Physis II" or "Pepsis" or "Pneuma"))
                Console.WriteLine($"           you {x.Name} {SageSim.F(x.T)} ({(x.T - t) / 1000.0:+0.0;-0.0}s)");
        }
        return 0;
    }
    case "tankaudit":
    {
        // 2026-10-28: tank low points and deaths in the logged pulls, with what the real Sage had available (TankAudit.cs).
        var idx = Index(args[1]);
        var all = new List<TankAudit.Episode>();
        foreach (var s in Select(idx, args[2]))
        {
            var r = PullLoader.Load(s, null, registry);
            var w = new World(r, meId);
            all.AddRange(TankAudit.Run(s, r, w, meId));
        }
        foreach (var e in all)
            Console.WriteLine($"#{e.Pull} {e.Date} {e.Phase} {SageSim.F(e.T),7} [{e.Segment}] {e.Tank} {e.Job} min {e.MinPct:0}%{(e.Died ? (e.OneShot ? " ONE-SHOT" : " DIED") : "")}{(e.WipeDeath ? " (wipe)" : "")} | 10s dmg autos {e.AutoK:0}k other {e.OtherK:0}k | own: {e.OwnMit} | Kardia {e.Kardia} gall {e.Gall} ready {e.Ready} | GCDs {e.SageGcds} | oGCDs on tank {e.SageOgcdsOnTank} | heals 10s: Sage {e.SageHealK:0}k co {e.CoHealK:0}k | hits {e.Hits}{(e.KillingBlow.Length > 0 ? " | KB " + e.KillingBlow : "")}");
        var path = Opt("csv", "");
        if (path.Length > 0)
            File.WriteAllLines(path, all.Select(e => string.Join("	", e.Pull, e.Date, e.Phase, e.Segment, e.T, e.Tank, e.Job, e.MinPct.ToString("0.0"), e.Died, e.OneShot, e.WipeDeath, e.AutoK.ToString("0"), e.OtherK.ToString("0"), e.OwnMit, e.Kardia, e.Gall, e.Ready, e.SageGcds, e.SageOgcdsOnTank, e.SageHealK.ToString("0"), e.CoHealK.ToString("0"), e.Hits, e.KillingBlow)));
        return 0;
    }
    case "tankprobe":
    {
        // 2026-10-28 tank-care audit: enemy actions on tanks in a window, with the model's member names.
        var idx = Index(args[1]);
        var s = Select(idx, args[2]).First();
        double from = double.Parse(Opt("from", "-5")), to = double.Parse(Opt("to", "9999"));
        var r = PullLoader.Load(s, null, registry);
        foreach (var ty in new[] { typeof(HitEvent), typeof(ActionEvent), typeof(Actor), typeof(StatusInterval), typeof(PullReplay) })
            Console.WriteLine(ty.Name + ": " + string.Join(", ", ty.GetProperties().Select(p => p.Name).Concat(ty.GetFields().Select(f => f.Name))));
        var tanks = (r.Party.Count > 0 ? r.Party : r.Players).Where(World.IsTank).Select(p => p.Id).ToHashSet();
        foreach (var a in r.Actions.Where(a => a.T >= from * 1000 && a.T <= to * 1000 && !a.Source.IsPlayer).OrderBy(a => a.T))
            foreach (var h in a.Hits.Where(h => tanks.Contains(h.Target.Id)))
                Console.WriteLine($"{SageSim.F(a.T),8} {a.Name,-22} id {a.ActionId:X} src {a.Source.Name}({a.Source.Kind}) -> {h.Target.Name} {h.Kind} dmg {h.Damage} hpBefore {h.HpBefore}/{h.MaxHp} after {h.HpAfter} shields[{string.Join(",", r.Statuses.Where(st => st.Target.Id == h.Target.Id && st.StartMs <= a.T && st.EndMs >= a.T && (st.Name.Contains("Diagnosis") || st.Name.Contains("Prognosis") || st.Name.Contains("Haima") || st.Name.Contains("aimatinon") || st.Name.Contains("Holos") || st.Name.Contains("Galvan") || st.Name.Contains("Benison") || st.Name.Contains("Blackest") || st.Name.Contains("Heart of") || st.Name.Contains("Corundum") || st.Name.Contains("Caress"))).Select(st => st.Name))}]");
        foreach (var d in r.Deaths) Console.WriteLine($"DEATH {SageSim.F(d.T)} {d.Victim.Name}");
        if (args.Contains("--target"))
        {
            var w0 = new World(r, meId);
            for (var t = (int)(from * 1000); t <= (int)(to * 1000); t += 1000)
            {
                var tg = w0.Target(t);
                Console.WriteLine($"target {SageSim.F(t)} {tg?.Name ?? "-"} ({tg?.Kind}) tt {w0.BossTarget(tg, t)?.Name ?? "-"}");
            }
            foreach (var a in r.Actions.Where(a => a.Source.Id == meId && a.T >= from * 1000 && a.T <= to * 1000 && a.Name.Contains("Dosis")))
                Console.WriteLine($"  {SageSim.F(a.T)} {a.Name} hits {string.Join(",", a.Hits.Select(h => $"{h.Target.Name}:{h.Kind}"))}");
        }
        if (args.Contains("--mystatus"))
        {
            foreach (var st in r.Statuses.Where(st => st.Source?.Id == meId && st.EndMs >= from * 1000 && st.StartMs <= to * 1000 && World.IsTank(st.Target)).OrderBy(st => st.StartMs))
                Console.WriteLine($"st {SageSim.F(st.StartMs)}-{SageSim.F(st.EndMs)} {st.StatusId} {st.Name} on {st.Target.Name}");
            foreach (var tk in r.Ticks.Where(tk => tk.IsHeal && tk.T >= from * 1000 && tk.T <= to * 1000 && World.IsTank(tk.Target)))
                Console.WriteLine($"tick {SageSim.F(tk.T)} {tk.Target.Name} {tk.Amount} src {tk.Source?.Name} eff {tk.EffectId}");
        }
        if (args.Contains("--kardion"))
        {
            foreach (var st in r.Statuses.Where(st => st.Source?.Id == meId && st.Name == "Kardion").OrderBy(st => st.StartMs))
                Console.WriteLine($"Kardion {SageSim.F(st.StartMs)}-{SageSim.F(st.EndMs)} on {st.Target.Name}");
            foreach (var a in r.Actions.Where(a => a.Source.Id == meId && a.T >= from * 1000 && a.T <= to * 1000).GroupBy(a => a.Sequence).Select(g => g.First()))
                Console.WriteLine($"  me {SageSim.F(a.T)} {a.Name} -> {a.PrimaryTarget?.Name ?? "-"} (hits: {string.Join(",", a.Hits.Select(h => h.Target.Name).Distinct())})");
        }
        if (args.Contains("--types"))
        {
            object? ph = r.Phases is System.Collections.IEnumerable e1 ? e1.Cast<object>().FirstOrDefault() : null;
            object? tk = r.Ticks.FirstOrDefault();
            object? dd = r.Deaths.FirstOrDefault();
            object? pp = r.ProgPoints is System.Collections.IEnumerable e2 ? e2.Cast<object>().FirstOrDefault() : null;
            foreach (var o in new[] { ph, tk, dd, pp })
                if (o is not null) Console.WriteLine(o.GetType().Name + ": " + string.Join(", ", o.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(o)}")));
            if (r.Phases is System.Collections.IEnumerable e3) foreach (var o in e3) Console.WriteLine("phase " + string.Join(", ", o.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(o)}")));
            if (r.ProgPoints is System.Collections.IEnumerable e4) foreach (var o in e4) Console.WriteLine("prog " + string.Join(", ", o.GetType().GetProperties().Select(p => $"{p.Name}={p.GetValue(o)}")));
        }
        return 0;
    }
    case "timeline":
    {
        var idx = Index(args[1]);
        var s = Select(idx, args[2]).First();
        double from = double.Parse(Opt("from", "-5")), to = double.Parse(Opt("to", "9999"));
        var (sim, w, _) = Simulate(s);
        var a = ActualOf(w);
        var ev = new List<(int T, int Order, string Line)>();
        foreach (var e in sim.Log) ev.Add((e.T, 2, $"RULES {e.What}{(e.Target.Length > 0 ? " -> " + e.Target : "")}{(e.Note.Length > 0 ? $"  [{e.Note}]" : "")}   ({e.Rule})"));
        foreach (var e in a.Events) ev.Add((e.T, 3, $"                                                            you: {e.Name}{(e.Target.Length > 0 ? " -> " + e.Target : "")}"));
        var seen = new Dictionary<string, int>();
        foreach (var c in w.R.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Helper or ActorKind.Enemy))
        {
            if (seen.TryGetValue(c.Name, out var last) && Math.Abs(c.StartMs - last) <= 1000) continue;
            seen[c.Name] = c.StartMs;
            ev.Add((c.StartMs, 0, $"      boss cast: {c.Name} ({c.DurationMs / 1000.0:0.0}s)"));
        }
        foreach (var rw in w.Raidwides) ev.Add((rw.T, 1, $"      HIT {rw.Name} #{rw.Ordinal} ({rw.AvgPct:0}% HP) rules: {Metrics.SimCover(sim, w, rw.T)}"));
        foreach (var d in w.R.Deaths) ev.Add((d.T, 1, $"      DEATH {d.Victim.Name}"));
        foreach (var e in ev.Where(e => e.T >= from * 1000 && e.T <= to * 1000).OrderBy(e => e.T).ThenBy(e => e.Order))
            Console.WriteLine($"{SageSim.F(e.T),8} {e.Line}");
        return 0;
    }
    default:
        Console.WriteLine("usage: RuleSim pulls|calibrate|run|timeline <log> <pulls> [--rules=DIR] [--me=HEX] [--move=y/s] [--from=s --to=s]");
        return 1;
}

internal sealed class Actual
{
    public readonly Dictionary<string, int> Counts = new();
    public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
    public readonly List<(int T, string Name, string Target)> Events = new();
    public readonly List<int> DotApps = new();
    public int Gcds, Cancels;
    public List<StatusInterval> Mine = new();
}

