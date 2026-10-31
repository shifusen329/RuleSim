using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The White Mage replay's commands (2026-10-21). Program.cs hands over when the pulls spec is "whm" (the --me player's
///     White Mage pulls in Dancing Mad) or --job=whm is given (any pulls, "dmu" = every Dancing Mad pull with --me in it,
///     with --me as the stand-in):
///
///   run       &lt;log&gt; whm|dmu|N,M-K [--job=whm] [--me=HEX] [--rules=DIR] [--select=FILE|none] [--set=a=b,...] [--gcd=ms|measure]
///             [--step=0.25 --slide=700] [--detail] [--slots] [--heal]
///   timeline  &lt;log&gt; &lt;pull&gt; --job=whm ... [--from=s --to=s] [--trace]
///   calibrate &lt;log&gt; whm|dmu --job=whm [--me=HEX]       movement model vs the player's real cast cancels
///   pulls     &lt;log&gt; whm|dmu --job=whm [--me=HEX]       the pulls, with the first raid window and the player's opener
/// </summary>
internal static class WhmCommands
{
    public const byte WhmJob = 24;

    public static bool Handles(string[] args) =>
        args.Length > 2 && (args[0] is "party" or "whmaudit" or "whmp3" || args[0] is "run" or "timeline" or "calibrate" or "pulls" && (args[2] == "whm" || args.Contains("--job=whm")));

    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private const string RepoRules = @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples";

    public static int Main(string[] args, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        A = args;
        var meId = Convert.ToUInt32(Opt("me", "1001A4A8"), 16);
        switch (args[0])
        {
            case "pulls": return Pulls(args[1], args[2], meId, registry, index);
            case "calibrate": return Calibrate(args[1], args[2], meId, registry, index);
            case "run": return Run(args[1], args[2], meId, registry, index);
            case "timeline": return Timeline(args[1], args[2], meId, registry, index);
            case "party": return Party(args[1], args[2], registry, index);
            case "whmp3": return WhmTankAudit.P3(args[1], args[2], meId, registry, index);
            case "whmaudit": return WhmTankAudit.Main(args[1], args[2], meId, registry, index, Opt("csv", ""));
        }
        return 1;
    }

    // ---------------------------------------------------------------- pulls and files

    internal static List<PullSummary> Select(FileIndex idx, string spec, uint meId)
    {
        var all = idx.Pulls.Where(p => p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond).ToList();
        if (spec == "whm") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId && m.Job == WhmJob)).ToList();
        // 2026-10-29: every Dancing Mad pull that reached Phase 3 or later (for "party").
        if (spec == "p3") return all.Where(p => p.ZoneId == 1363 && p.FurthestPhase is { } f && (f.StartsWith("P3") || f.StartsWith("P4") || f.StartsWith("P5"))).ToList();
        if (spec == "dmu") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId)).ToList();
        var want = new HashSet<int>();
        foreach (var part in spec.Split(','))
            if (part.Contains('-')) { var x = part.Split('-'); for (var i = int.Parse(x[0]); i <= int.Parse(x[1]); i++) want.Add(i); }
            else want.Add(int.Parse(part));
        return all.Where(p => want.Contains(p.Ordinal)).ToList();
    }

    private static string SymbolsPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "whm_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "whm_symbols.json");
    }

    /// <summary>
    ///     RuleEngine.FilesFor for WHM_ST_CustomRules (buttons Stone, Stone II-IV, Glare, Glare III): the job's layers for the
    ///     button in name order, then the selected file (--select, default WHM_ST_DPS.json), or with --select=none every other
    ///     file for the button in name order. Every WHM file is compiled (a broken one is reported); "enabled" is honoured at
    ///     evaluation, as EvaluateForPreset does.
    /// </summary>
    public static List<CompiledRuleSet> Compile(WhmSim sim, string rulesDir, List<string> describe)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var buttons = WA.StoneGlare.ToHashSet();
        var selected = Opt("select", "WHM_ST_DPS.json");
        var layers = new List<CompiledRuleSet>();
        var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            RuleSetFile? file;
            try { file = JsonConvert.DeserializeObject<RuleSetFile>(File.ReadAllText(path)); }
            catch (JsonException e) { describe.Add($"{Path.GetFileName(path)}: JSON error {e.Message}"); continue; }
            if (file is null || !string.Equals(file.Job, "WHM", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in Opt("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var set = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, WA.Glare3);
            if (set is null)
            {
                Console.WriteLine($"  COMPILE ERRORS in {Path.GetFileName(path)}:\n    " + string.Join("\n    ", errors));
                var wanted = file.Layer || (selected != "none" ? Path.GetFileName(path).Equals(selected, StringComparison.OrdinalIgnoreCase) : true);
                if (wanted) throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't compile");
                describe.Add($"{Path.GetFileName(path)} (compile errors, not run)");
                continue;
            }
            var forButton = set.Replaces.Count == 0 || set.Replaces.Overlaps(buttons);
            if (set.Layer && forButton) { layers.Add(set); describe.Add($"{Path.GetFileName(path)} (layer{(set.Enabled ? "" : ", disabled")})"); }
            else if (!set.Layer && (selected != "none" ? Path.GetFileName(path).Equals(selected, StringComparison.OrdinalIgnoreCase) : forButton))
            { others.Add(set); describe.Add($"{Path.GetFileName(path)} ({(selected != "none" ? "selected" : "for the button")}{(set.Enabled ? "" : ", disabled")})"); }
            else describe.Add($"{Path.GetFileName(path)} (compiles; not for this button)");
        }
        if (selected != "none" && others.Count == 0) describe.Add($"WARNING: the selected file {selected} isn't in {rulesDir}");
        return layers.Concat(others).ToList();
    }

    private static readonly string[] Fillers = ["Glare III", "Fall Malefic", "Dosis III", "Broil IV"];

    /// <summary> Median start-to-start of the player's filler casts (Glare III; an Astrologian stand-in's Fall Malefic), 2.2-2.7 s; else 2500. </summary>
    public static int MeasureGcd(World w, uint meId)
    {
        var iv = GcdIntervals(w, meId);
        iv.Sort();
        return iv.Count >= 5 ? iv[iv.Count / 2] : 2500;
    }

    public static List<int> GcdIntervals(World w, uint meId)
    {
        var starts = w.R.Casts.Where(c => c.Source.Id == meId && Fillers.Contains(c.Name)).Select(c => c.StartMs).OrderBy(x => x).ToList();
        return starts.Zip(starts.Skip(1), (a, b) => b - a).Where(d => d is >= 2200 and <= 2700).ToList();
    }

    /// <summary> --gcd=ms, or the median over every selected pull (one player's gear doesn't change within a night; short pulls alone have too few intervals). </summary>
    public static int PooledGcd(IEnumerable<World> worlds, uint meId)
    {
        if (Opt("gcd", "measure") is var g && g != "measure") return int.Parse(g);
        var all = worlds.SelectMany(w => GcdIntervals(w, meId)).OrderBy(x => x).ToList();
        return all.Count >= 5 ? all[all.Count / 2] : 2500;
    }

    // ---------------------------------------------------------------- simulate

    public sealed record Result(PullSummary S, World W, WhmSim Sim, List<string> Files, int EndMs);

    private static World Load(PullSummary s, uint meId, EncounterRegistry registry) =>
        new(PullLoader.Load(s, null, registry), meId) { MinStep = double.Parse(Opt("step", "0.25")), RunSpeed = double.Parse(Opt("run", "5")) };

    private static Result Simulate(PullSummary s, World w, int gcd, string rulesDir, double hpPerPot = 0)
    {
        var r = w.R;
        var sim = new WhmSim(w, gcd)
        {
            T = -16000, SlideMs = int.Parse(Opt("slide", "700")),
            Trace = A.Contains("--trace"), TraceFrom = (int)(double.Parse(Opt("from", "-16")) * 1000), TraceTo = (int)(double.Parse(Opt("to", "9999")) * 1000),
        };
        var describe = new List<string>();
        var files = Compile(sim, rulesDir, describe);
        // 2026-10-29 --cfhp: the rules see the tanks' counterfactual HP (WhmTankModel, run as the replay goes), not the logged.
        WhmTankModel? cfm = null;
        if (A.Contains("--cfhp") && hpPerPot > 0)
        {
            var m = new WhmTankModel(w, sim, hpPerPot);
            cfm = m;
            sim.HpView = (p, t) => m.CfPct(p, t);
        }
        while (sim.T < r.EndMs)
        {
            sim.Advance(20);
            if (cfm is not null && sim.T >= 0) cfm.AdvanceTo(sim.T);
            sim.Press(files);
        }
        sim.Finish();
        return new Result(s, w, sim, describe, r.EndMs);
    }

    // ---------------------------------------------------------------- the logged player

    public sealed class Actual
    {
        public readonly Dictionary<string, int> Counts = new();
        public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
        public readonly List<(int T, string Name, string Target)> Events = new();
        public int Gcds, Cancels, MiseryInBuffs, DiaApps;
        public double DiaUpMs;
        public List<StatusInterval> Mine = new();
    }

    private static readonly Dictionary<string, (int Pot, double Others)> Pot = new()
    {
        ["Glare III"] = (310, 0), ["Glare IV"] = (640, 0.6), ["Afflatus Misery"] = (1240, 0.5), ["Assize"] = (400, 1), ["Dia"] = (75, 0), ["Holy III"] = (150, 1),
    };

    public static Actual ActualOf(Result x, uint meId)
    {
        var w = x.W;
        var a = new Actual();
        var mine = w.R.Actions.Where(e => e.Source.Id == meId).GroupBy(e => e.Sequence != 0 ? (long)e.Sequence : ((long)e.ActionId << 32) + e.T).Select(g => g.First()).OrderBy(e => e.T).ToList();
        foreach (var e in mine)
        {
            var tgt = e.PrimaryTarget ?? e.Hits.FirstOrDefault()?.Target;
            a.Events.Add((e.Cast?.StartMs ?? e.T, e.Name, tgt is { IsPlayer: true } && tgt.Id != meId ? tgt.Name : ""));
            if (e.T < -3000) continue;
            if (e.T >= 0)
            {
                a.Counts[e.Name] = a.Counts.GetValueOrDefault(e.Name) + 1;
                if (WhmSim.GcdNames.Contains(e.Name)) a.Gcds++;
                if (e.Name == "Afflatus Misery" && w.RaidBuffs(tgt is { IsPlayer: false } ? tgt : SamSim.TargetAt(w, e.T), e.T).Count > 0) a.MiseryInBuffs++;
            }
            if (!Pot.TryGetValue(e.Name, out var p)) continue;
            // the primary target at full potency; other bosses hit at the action's reduced potency
            var hitTargets = e.Hits.Where(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage).Select(h => h.Target).Distinct().ToList();
            if (hitTargets.Count == 0) continue;
            var first = tgt is { IsPlayer: false } && hitTargets.Contains(tgt) ? tgt : hitTargets[0];
            a.Damage.Add((Math.Max(0, e.T), e.Name, p.Pot, w.RaidBuffs(first, e.T).Mult));
            foreach (var o in hitTargets.Where(o => o != first && o.Kind == ActorKind.Boss))
                if (p.Others > 0) a.Damage.Add((Math.Max(0, e.T), e.Name + " (2nd target)", p.Pot * p.Others, w.RaidBuffs(o, e.T).Mult));
        }
        var dias = w.R.Statuses.Where(s => s.Source?.Id == meId && s.Name == "Dia" && !s.Target.IsPlayer).ToList();
        foreach (var st in dias)
        {
            var mult = w.RaidBuffs(st.Target, st.StartMs).Mult;
            a.DiaApps++;
            for (var t = st.StartMs + 1500; t < Math.Min(st.EndMs, w.R.EndMs); t += 3000)
                if (st.Target.IsPresent(t) && st.Target.IsTargetable(t) && t >= 0) a.Damage.Add((t, "Dia (tick)", 75, mult));
        }
        a.Cancels = w.R.Casts.Count(c => c.Source.Id == meId && c.Outcome == CastOutcome.Cancelled && c.StartMs >= 0);
        a.Mine = w.R.Statuses.Where(s => s.Source?.Id == meId).ToList();
        for (var t = 0; t < x.EndMs; t += 100)
        {
            if (w.Dead(w.Me, t) || SamSim.TargetAt(w, t) is not { } tg || SamSim.Incapacitated(w, t) is not null) continue;
            if (dias.Any(s => s.Target.Id == tg.Id && s.Active(t))) a.DiaUpMs += 100;
        }
        return a;
    }

    // ---------------------------------------------------------------- coverage (the rules' and the logged player's)

    private static IEnumerable<int> Presses(WhmSim sim, string name) => sim.Log.Where(e => e.What == name).Select(e => e.T);
    private static bool Up(WhmSim sim, string name, int durMs, int t) => Presses(sim, name).Any(p => p + Metrics.LandMs <= t && t < p + Metrics.LandMs + durMs);

    /// <summary> A shield (Divine Caress, Divine Benison) from the rules on this player at t: landed, not broken before t (the hit at t may break it). </summary>
    public static bool ShieldOn(WhmSim sim, Actor who, uint status, int t) =>
        sim.ShieldLog.Any(s => s.Target.Id == who.Id && s.Status == status && s.From + Metrics.LandMs <= t && t <= s.To + 50);

    public static bool SimTemperance(WhmSim sim, int t) => Up(sim, "Temperance", 20000, t);
    public static bool SimPlenary(WhmSim sim, int t) => Up(sim, "Plenary Indulgence", 10000, t);
    public static bool SimCaressParty(WhmSim sim, World w, int t) => w.Party.Count(p => ShieldOn(sim, p, WS.CaressShield, t)) >= 3;
    public static bool SimBell(WhmSim sim, int t) => sim.BellLog.Any(b => b.From + Metrics.LandMs <= t && t < b.To);
    public static bool SimTankAssist(WhmSim sim, World w, int t) =>
        sim.Log.Any(e => e.What == "Aquaveil" && e.T + Metrics.LandMs <= t && t < e.T + Metrics.LandMs + 8000 && w.Party.Any(p => p.Name == e.Target && World.IsTank(p)))
        || w.Party.Any(p => World.IsTank(p) && ShieldOn(sim, p, WS.Benison, t));

    /// <summary> The rules' White Mage mitigation on the replayed player at t (for the big-hit coverage). </summary>
    public static string SimCover(WhmSim sim, World w, int t)
    {
        var parts = new List<string>();
        if (SimTemperance(sim, t)) parts.Add("Temp");
        if (SimPlenary(sim, t)) parts.Add("Plen");
        if (ShieldOn(sim, w.Me, WS.CaressShield, t)) parts.Add("Caress");
        if (SimBell(sim, t)) parts.Add("Bell");
        return string.Join(" ", parts);
    }

    public static string RealCover(World w, Actual a, int t)
    {
        bool Has(string n) => a.Mine.Any(x => x.Target.Id == w.Me.Id && x.Name == n && x.StartMs <= t + 20 && x.EndMs >= t - 50);
        var parts = new List<string>();
        if (Has("Temperance")) parts.Add("Temp");
        if (Has("Confession")) parts.Add("Plen");
        if (Has("Divine Caress")) parts.Add("Caress");
        if (Has("Liturgy of the Bell")) parts.Add("Bell");
        return string.Join(" ", parts);
    }

    private static bool Mitigates(string cover) => cover.Contains("Temp") || cover.Contains("Plen") || cover.Contains("Caress");

    public static List<Metrics.Slot> Slots(Result x, Actual a, List<Metrics.SheetRow> sheet)
    {
        var (w, sim) = (x.W, x.Sim);
        bool RealTankAssist(int t) => a.Mine.Any(s => (s.Name is "Aquaveil" or "Divine Benison") && World.IsTank(s.Target) && s.StartMs <= t + 20 && s.EndMs >= t - 50);
        return Metrics.SlotsWith(w, sheet, (label, t) => label switch
        {
            "Temperance" => (SimTemperance(sim, t), Metrics.RealUp(w, a.Mine, "Temperance", t)),
            "Plenary Indulgence" => (SimPlenary(sim, t), Metrics.RealUp(w, a.Mine, "Confession", t)),
            "Divine Caress" => (SimCaressParty(sim, w, t), Metrics.RealUp(w, a.Mine, "Divine Caress", t)),
            "Liturgy of the Bell" => (SimBell(sim, t), Metrics.RealUp(w, a.Mine, "Liturgy of the Bell", t, 1)),
            "Assist Tanks" => (SimTankAssist(sim, w, t), RealTankAssist(t)),
            _ => null,
        });
    }

    private static readonly Dictionary<string, string> LabelAction = new()
    {
        ["Temperance"] = "Temperance", ["Plenary Indulgence"] = "Plenary Indulgence", ["Divine Caress"] = "Divine Caress",
        ["Liturgy of the Bell"] = "Liturgy of the Bell", ["Assist Tanks"] = "Aquaveil",
    };

    /// <summary> Why a sheet slot isn't covered by the rules: the nearest press of its action, relative to the hit. </summary>
    public static string SlotWhy(WhmSim sim, Metrics.Slot s)
    {
        var name = LabelAction.GetValueOrDefault(s.Label, s.Label);
        var near = sim.Log.Where(e => e.What == name && Math.Abs(e.T - s.T) <= 60000).OrderBy(e => Math.Abs(e.T - s.T)).FirstOrDefault();
        if (near is null) return "no press within 60 s";
        var dt = (near.T - s.T) / 1000.0;
        return $"nearest press {dt:+0.0;-0.0}s [{near.Rule.Replace("DMU mitigation plan (White Mage): ", "plan: ").Replace("White Mage single target: ", "")}]";
    }

    // HP per potency from direct heals of known potency (White Mage, and the Astrologian stand-in's).
    private static readonly Dictionary<string, int> HealTable = new()
    {
        ["Afflatus Rapture"] = 400, ["Afflatus Solace"] = 800, ["Cure II"] = 800, ["Cure III"] = 600, ["Medica"] = 400, ["Tetragrammaton"] = 700, ["Cure"] = 500,
        ["Benefic II"] = 800, ["Benefic"] = 500, ["Helios"] = 400, ["Celestial Opposition"] = 200, ["Lady of Crowns"] = 400, ["Celestial Intersection"] = 200,
    };

    // ---------------------------------------------------------------- run

    private sealed record PullRow(Result X, Actual Act, bool IsWhm, List<Metrics.Slot> Slots, int BigN, int BigSim, int BigReal, List<Metrics.Segment> Segs);

    private static int Run(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", RepoRules);
        var pulls = Select(index(log), spec, meId);
        var sheet = Metrics.LoadSheet(Opt("sheet", @"C:\Users\quanh\Workspace\WrathCombo\docs\guides\DMU"), 9);
        var detail = A.Contains("--detail");
        Console.WriteLine($"rules {rulesDir}  select {Opt("select", "WHM_ST_DPS.json")}  set [{Opt("set", "")}]  step {Opt("step", "0.25")} slide {Opt("slide", "700")}  gcd {Opt("gcd", "measure")}");
        var rows = new List<PullRow>();
        long calRaw = 0, calPot = 0;
        var results = new List<(Result X, Actual A, bool IsWhm)>();
        var worlds = pulls.Select(s => Load(s, meId, registry)).ToList();
        var gcd = PooledGcd(worlds, meId);
        Console.WriteLine($"GCD {gcd} ms ({(Opt("gcd", "measure") == "measure" ? "median Glare III start-to-start over these pulls" : "--gcd")})");
        // HP per potency from the logged player's heals (log only), before the replay so --cfhp can use it.
        foreach (var w0 in worlds) { var cal = Metrics.CalibrationSample(w0, HealTable); calRaw += cal.Raw; calPot += cal.Pot; }
        var hpPerPot = calPot >= 3000 ? (double)calRaw / calPot : 95.0;
        if (A.Contains("--cfhp")) Console.WriteLine($"--cfhp: the rules see the tanks' counterfactual HP ({hpPerPot:0} HP per potency)");
        for (var i = 0; i < pulls.Count; i++)
        {
            var s = pulls[i];
            var x = Simulate(s, worlds[i], gcd, rulesDir, hpPerPot);
            var act = ActualOf(x, meId);
            results.Add((x, act, s.Party.Any(m => m.Id == meId && m.Job == WhmJob)));
        }
        foreach (var (x, act, isWhm) in results)
        {
            var slots = Slots(x, act, sheet);
            int bn = 0, bs = 0, br = 0;
            foreach (var rw in x.W.Raidwides.Where(r => r.AvgPct >= 20))
            {
                bn++;
                if (Mitigates(SimCover(x.Sim, x.W, rw.T))) bs++;
                if (Mitigates(RealCover(x.W, act, rw.T))) br++;
            }
            var segs = Metrics.Healing(x.W, x.Sim.Heals, hpPerPot);
            var row = new PullRow(x, act, isWhm, slots, bn, bs, br, segs);
            rows.Add(row);
            if (pulls.Count == 1 || detail) PrintPull(row, hpPerPot);
            else PrintShort(row);
            if (A.Contains("--counts"))
            {
                // the logged player's own play, for the cross-check with report.py: counts from 3 s before the pull (as report.py) and from the pull
                var pre = x.W.R.Actions.Where(e => e.Source.Id == x.W.Me.Id && e.T >= -3000).GroupBy(e => e.Sequence != 0 ? (long)e.Sequence : ((long)e.ActionId << 32) + e.T).Select(gg => gg.First()).ToList();
                Console.WriteLine($"REAL #{x.S.Ordinal} {new DateTime(x.S.StartTicks).ToLocalTime():yyyy-MM-ddTHH:mm:ss} up={x.Sim.AvailMs / 1000.0:0.0} dead={x.Sim.DeadMs / 1000.0:0.0} gcds={act.Gcds} gcdsPre={pre.Count(e => WhmSim.GcdNames.Contains(e.Name))} cancels={act.Cancels} diaUp={100.0 * act.DiaUpMs / Math.Max(1, x.Sim.AvailMs):0.0} miseryInBuffs={act.MiseryInBuffs} " +
                                  string.Join(" ", Counted.Select(n => $"{n.Replace(" ", "_")}={pre.Count(e => e.Name == n)}")));
            }
        }
        PrintTotals(rows, hpPerPot);
        if (A.Contains("--tanks")) PrintTanks(rows, hpPerPot);
        return 0;
    }

    // ---------------------------------------------------------------- 2026-10-29 tank care (WhmTankMetrics.cs)

    private static readonly string[] P3Window = ["Bowels of Agony", "Implosion", "Umbra Smash + Ultima Blaster"];
    private static readonly string[] HealGcds = ["Cure II", "Cure III", "Cure", "Afflatus Solace", "Afflatus Rapture", "Regen", "Medica III", "Medica"];
    private static readonly string[] TankOgcds = ["Divine Benison", "Aquaveil", "Tetragrammaton", "Benediction"];

    private static void PrintTanks(List<PullRow> rows, double h)
    {
        Console.WriteLine("\n===== tank care (counterfactual tank HP, WhmTankMetrics)");
        var tDeaths = new List<(int Pull, TankMetrics.Death D)>(); var tCf = new List<(int Pull, TankMetrics.CfDeath D)>();
        var tPh = new Dictionary<string, double[]>(); double tRulesLow = 0, tRealLow = 0;
        var eps = new List<(int Pull, WhmTankMetrics.Episode E)>();
        double p3Alive = 0, p3Rules = 0, p3Real = 0; var p3Heals = new Dictionary<string, int>(); var p3Og = new Dictionary<string, int>();
        var phPot = new Dictionary<string, (double Avail, double Pot)>();
        foreach (var row in rows)
        {
            var (x, act) = (row.X, row.Act);
            var (w, sim) = (x.W, x.Sim);
            var (tr, ep) = WhmTankMetrics.Run(w, sim, h);
            tDeaths.AddRange(tr.Deaths.Select(d => (x.S.Ordinal, d))); tCf.AddRange(tr.CfDeaths.Select(d => (x.S.Ordinal, d)));
            eps.AddRange(ep.Select(e => (x.S.Ordinal, e)));
            tRulesLow += tr.RulesHealOnLowTank; tRealLow += tr.RealHealOnLowTank;
            foreach (var (k, v) in tr.ByPhase)
            {
                var a = tPh.TryGetValue(k, out var o) ? o : tPh[k] = [0, 0, 0, 0, 100, 100, 0, 0];
                a[0] += v.LogLow30; a[1] += v.CfLow30; a[2] += v.LogLow15; a[3] += v.CfLow15; a[4] = Math.Min(a[4], v.CfMin); a[5] = Math.Min(a[5], v.LogMin); a[6]++; a[7] += v.CfDead;
            }
            foreach (var d in tr.Deaths) Console.WriteLine($"   tank #{x.S.Ordinal} {F(d.T)} {d.Phase} [{d.Segment}] {d.Tank} died to {d.Cause}: rules {(d.Survives ? "SURVIVE" : "die")} ({d.MarginPct:+0;-0}% after the hit)");
            foreach (var d in tr.CfDeaths) Console.WriteLine($"   tank #{x.S.Ordinal} {F(d.T)} {d.Phase} {d.Tank} RULES WOULD LOSE THEM: {d.Hit}");
            foreach (var (k, v) in tr.ByPhase.Where(kv => !kv.Key.Contains(' ')).OrderBy(kv => kv.Key))
                Console.WriteLine($"   tanksec #{x.S.Ordinal} {k} u30 {v.LogLow30:0.0} {v.CfLow30:0.0} u15 {v.LogLow15:0.0} {v.CfLow15:0.0} min {v.LogMin:0} {v.CfMin:0} dead {v.CfDead:0.0}");
            foreach (var (k, v) in tr.ByPhase.Where(kv => kv.Key.StartsWith("p3 ")).OrderBy(kv => kv.Key))
                Console.WriteLine($"   tanksegp3 #{x.S.Ordinal} {k[3..]} | u30 {v.LogLow30:0.0} {v.CfLow30:0.0} u15 {v.LogLow15:0.0} {v.CfLow15:0.0} dead {v.CfDead:0.0}");
            // Phase 3's two-boss stretch (Bowels of Agony, Implosion, Umbra Smash segments), as the Sage's p3window.
            var segs = w.R.Phases.Where(ph => ph.IsSegment && P3Window.Contains(ph.Name)).ToList();
            if (segs.Count > 0)
            {
                int s0 = segs.Min(v => v.StartMs), s1 = Math.Min(segs.Max(v => v.EndMs), w.R.EndMs);
                var aliveMs = 0; for (var t = s0; t < s1; t += 100) if (!w.Dead(w.Me, t)) aliveMs += 100;
                var ps = sim.Damage.Where(d => d.T >= s0 && d.T < s1).Sum(d => d.Potency * d.Mult);
                var pa = act.Damage.Where(d => d.T >= s0 && d.T < s1).Sum(d => d.Potency * d.Mult);
                p3Alive += aliveMs; p3Rules += ps; p3Real += pa;
                var hl = sim.Log.Where(e => e.T >= s0 && e.T < s1 && HealGcds.Contains(e.What)).GroupBy(e => e.What).ToDictionary(g => g.Key, g => g.Count());
                var ol = sim.Log.Where(e => e.T >= s0 && e.T < s1 && TankOgcds.Contains(e.What)).GroupBy(e => e.What).ToDictionary(g => g.Key, g => g.Count());
                foreach (var (k, v) in hl) p3Heals[k] = p3Heals.GetValueOrDefault(k) + v;
                foreach (var (k, v) in ol) p3Og[k] = p3Og.GetValueOrDefault(k) + v;
                Console.WriteLine($"   p3window #{x.S.Ordinal} {F(s0)}-{F(s1)} alive {aliveMs / 1000.0:0} s potency rules {ps:0} logged {pa:0}; GCD heals {string.Join(", ", hl.Select(kv => $"{kv.Key} {kv.Value}"))}; oGCDs {string.Join(", ", ol.Select(kv => $"{kv.Key} {kv.Value}"))}");
            }
            // Phase 3's first Earthquake: what the rules had for the Accretions (7.4 s after the cast) and the plan's presses for them.
            var eq = w.R.Casts.Where(c => c.Name == "Earthquake").OrderBy(c => c.StartMs).FirstOrDefault();
            if (eq is not null && eq.StartMs + 7400 < w.R.EndMs)
            {
                var at = eq.StartMs + 7400;
                var gg = sim.GaugeAt(at);
                var eqp = sim.Log.Where(e => e.Rule.Contains("Earthquake:")).Take(6).Select(e => $"{F(e.T)} {e.What}{(e.Target.Length > 0 ? "->" + e.Target.Split(' ')[0] : "")}");
                Console.WriteLine($"   eqpress #{x.S.Ordinal} accretions {F(at)}: ready {WhmTankMetrics.ReadyAt(sim.Log.Select(e => (e.T, e.What)), at)},Lily{gg.Lily},Blood{gg.Blood}{(w.Dead(w.Me, at) ? ",WHM DEAD" : "")} | presses {string.Join(", ", eqp)}");
            }
            foreach (var (phase, p) in sim.PhaseMs)
            {
                var pot = sim.Damage.Where(d => w.R.PhaseAt(d.T) == phase).Sum(d => d.Potency * d.Mult);
                var o = phPot.GetValueOrDefault(phase);
                phPot[phase] = (o.Avail + p.Avail, o.Pot + pot);
            }
        }
        var n = rows.Count;
        Console.WriteLine($"\n   tanks: logged deaths (not in a wipe) {tDeaths.Count}, the rules survive the killing blow in {tDeaths.Count(v => v.D.Survives)}; tanks the rules would lose that lived {tCf.Count}; rules' heals on a tank under 50% {tRulesLow / 1e6:0.00}M vs the real player {tRealLow / 1e6:0.00}M");
        foreach (var (k, v) in tPh.OrderBy(v => v.Key))
            Console.WriteLine($"     tank-seconds {k,-40} under 30%: logged {v[0],6:0} rules {v[1],6:0}   under 15%: logged {v[2],6:0} rules {v[3],6:0}   lowest: logged {v[5],3:0}% rules {v[4],3:0}%   rules-dead {v[7],5:0}  (pulls {v[6]})");
        foreach (var (pull, d) in tDeaths) Console.WriteLine($"     death #{pull} {F(d.T)} {d.Phase} [{d.Segment}] {d.Tank}: {d.Cause}; rules {(d.Survives ? "SURVIVE" : "die")} ({d.MarginPct:+0;-0}%)");
        foreach (var (pull, d) in tCf) Console.WriteLine($"     RULES LOSE #{pull} {F(d.T)} {d.Phase} {d.Tank}: {d.Hit}");
        if (p3Alive > 0)
            Console.WriteLine($"   P3 two-boss window: alive {p3Alive / 60000:0.00} min, potency/min rules {p3Rules / (p3Alive / 60000):0} logged {p3Real / (p3Alive / 60000):0}; GCD heals {string.Join(", ", p3Heals.Select(kv => $"{kv.Key} {kv.Value}"))}; oGCDs {string.Join(", ", p3Og.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Console.WriteLine("   potency/min by phase: " + string.Join("; ", phPot.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value.Pot / Math.Max(0.01, kv.Value.Avail / 60000.0):0} ({kv.Value.Avail / 60000.0:0.0} min)")));
        if (A.Contains("--episodes"))
            foreach (var (pull, e) in eps)
                Console.WriteLine($"   episode #{pull} {e.Phase} {F(e.Start)}-{F(e.End)} min@{F(e.MinT)} [{e.Segment}] {e.Tank} cf {e.CfMin:0}% (logged min {e.LogMin:0}%){(e.CfDied ? " CF-DEAD" : "")} | 10s autos {e.AutoK:0}k other {e.OtherK:0}k | ready {e.Ready} | GCDs {e.Gcds} | oGCDs {e.Ogcds} | hits {e.Hits}");
    }

    private static int Party(string log, string spec, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        foreach (var s in Select(index(log), spec, 0))
            Console.WriteLine($"#{s.Ordinal,-4} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm:ss} {s.Outcome,-8} {(s.EndTicks - s.StartTicks) / 1e7,6:N1}s {s.FurthestPhase,-20} " +
                              string.Join("  ", s.Party.Select(m => $"{m.Name}({m.Id:X8},{m.Job})")));
        return 0;
    }

    private static string F(int ms) => SageSim.F(ms);
    private static double Eff(List<(int T, string Src, double Potency, double Mult)> d) => d.Sum(x => x.Potency * x.Mult);

    private static void PrintShort(PullRow r)
    {
        var (x, act) = (r.X, r.Act);
        var sim = x.Sim;
        var mins = Math.Max(0.01, sim.AvailMs / 60000.0);
        Console.WriteLine($"#{x.S.Ordinal,-3} {new DateTime(x.S.StartTicks).ToLocalTime():MM-dd HH:mm} {F(x.EndMs),8} {x.W.R.PhaseAt(x.EndMs - 1),-20} up {sim.AvailMs / 1000,4}s GCD {sim.GcdMs}  " +
                          $"pot/min {Eff(sim.Damage) / mins,5:0} vs {Eff(act.Damage) / mins,5:0}  GCD/min {sim.Gcds / mins,4:0.0} vs {act.Gcds / mins,4:0.0}  cancels {sim.Cancels,2} vs {act.Cancels,2}  " +
                          $"Dia {100.0 * sim.DiaUpMs / Math.Max(1, sim.AvailMs),3:0}% vs {100.0 * act.DiaUpMs / Math.Max(1, sim.AvailMs),3:0}%  MP low {sim.MinMp,5}  slots {r.Slots.Count(s => s.Sim)}/{r.Slots.Count} vs {r.Slots.Count(s => s.Real)}  " +
                          $"big {r.BigSim}/{r.BigN} vs {r.BigReal}  cfLow {r.Segs.Select(g => g.CfMin).DefaultIfEmpty(100).Min(),3:0}%  clip {sim.ClipMs / 1000.0:0.0}s");
    }

    public static readonly string[] Counted = ["Glare III", "Glare IV", "Dia", "Afflatus Misery", "Afflatus Rapture", "Afflatus Solace", "Presence of Mind", "Assize",
        "Thin Air", "Lucid Dreaming", "Swiftcast", "Temperance", "Divine Caress", "Plenary Indulgence", "Liturgy of the Bell", "Asylum", "Aquaveil", "Divine Benison",
        "Tetragrammaton", "Benediction", "Medica III", "Cure II", "Cure III", "Regen", "Raise"];

    private static void PrintPull(PullRow r, double hpPerPot)
    {
        var (x, act) = (r.X, r.Act);
        var (s, w, sim) = (x.S, x.W, x.Sim);
        var mins = Math.Max(0.01, sim.AvailMs / 60000.0);
        Console.WriteLine($"\n== pull #{s.Ordinal} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm} {F(x.EndMs)} {s.Outcome}, furthest {w.R.PhaseAt(x.EndMs - 1)}; target up and alive and able to act {sim.AvailMs / 1000.0:0}s; dead {sim.DeadMs / 1000.0:0}s, incapacitated {sim.IncapMs / 1000.0:0}s");
        Console.WriteLine($"   GCD {sim.GcdMs} ms ({(int)Math.Round(sim.GcdMs * 0.8)} under Presence of Mind), Glare III cast {sim.CastTime(WhmSim.Defs[WA.Glare3])} ms; {(r.IsWhm ? "the player" : "stand-in")} {w.Me.Name} (job {w.Me.Job})");
        Console.WriteLine($"   files: {string.Join(", ", x.Files)}");
        var colB = r.IsWhm ? "logged" : "stand-in";
        Console.WriteLine($"   {"",-40}{"rules",10}{colB,10}");
        void Row(string n, double a, double? b, string fmt = "0") => Console.WriteLine($"   {n,-40}{a.ToString(fmt),10}{(b is { } bb ? bb.ToString(fmt) : ""),10}");
        Row("potency per minute", Eff(sim.Damage) / mins, Eff(act.Damage) / mins);
        Row("GCDs per minute", sim.Gcds / mins, act.Gcds / mins, "0.00");
        Row("GCD uptime while a target was up (%)", 100.0 * sim.BusyMs / Math.Max(1, sim.AvailMs), null, "0.0");
        Row("Dia uptime (%)", 100.0 * sim.DiaUpMs / Math.Max(1, sim.AvailMs), 100.0 * act.DiaUpMs / Math.Max(1, sim.AvailMs), "0.0");
        Row("Dia ticks", sim.Damage.Count(d => d.Src == "Dia (tick)"), act.Damage.Count(d => d.Src == "Dia (tick)"));
        Row("cast cancels", sim.Cancels, act.Cancels);
        Row("casts lost (target gone)", sim.CastsLostTarget, null);
        Row("Misery in raid buffs", sim.Damage.Count(d => d.Src == "Afflatus Misery" && d.Mult > 1.0001), act.MiseryInBuffs);
        Row("Lilies at 3 (s)", sim.LilyCappedMs / 1000.0, null, "0.0");
        Row("Blood Lily wasted", sim.BloodWasted, null);
        Row("MP low", sim.MinMp, null);
        Row("GCD clipped by weaves (s)", sim.ClipMs / 1000.0, null, "0.0");
        foreach (var n in Counted) Row(n, sim.Log.Count(e => e.What == n && e.T >= 0), act.Counts.GetValueOrDefault(n));
        // raid windows and burst
        var wins = SamCommands.RaidWindows(w, x.EndMs);
        Console.WriteLine("   raid windows: " + string.Join(" | ", wins.Select(v =>
        {
            var inW = sim.Log.Where(e => e.T >= v.Start && e.T < v.End).Select(e => e.What).ToList();
            return $"{F(v.Start)}-{F(v.End)} ({v.Max}): Misery {inW.Count(n => n == "Afflatus Misery")}, Glare IV {inW.Count(n => n == "Glare IV")}, Assize {inW.Count(n => n == "Assize")}";
        })));
        // big hits
        foreach (var rw in w.Raidwides.Where(rr => rr.AvgPct >= 20))
            Console.WriteLine($"   {F(rw.T),8} {rw.Name} #{rw.Ordinal,-3} {rw.AvgPct,3:0}% of HP  rules: {SimCover(sim, w, rw.T),-24} {(r.IsWhm ? "logged" : "stand-in")}: {RealCover(w, act, rw.T)}");
        // sheet
        if (r.Slots.Count > 0)
        {
            Console.WriteLine($"   sheet slots (White Mage column): rules {r.Slots.Count(v => v.Sim)}/{r.Slots.Count}, logged {r.Slots.Count(v => v.Real)}/{r.Slots.Count}  (" +
                              string.Join(", ", r.Slots.GroupBy(v => v.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count(v => v.Sim)}/{g.Count(v => v.Real)}/{g.Count()}")) + ")");
            if (A.Contains("--slots") || A.Contains("--detail"))
                foreach (var v in r.Slots)
                    Console.WriteLine($"     {F(v.T),8} {v.Phase} {v.Mech,-36} {(v.Carry ? "> " : "")}{v.Label,-20} rules {(v.Sim ? "up" : "--")}  logged {(v.Real ? "up" : "--")}{(v.Sim ? "" : "   " + SlotWhy(sim, v))}");
        }
        // healing
        var segs = r.Segs;
        Console.WriteLine($"   healing ({hpPerPot:0} HP/potency): lowest counterfactual HP {segs.Select(g => g.CfMin).DefaultIfEmpty(100).Min():0}% (logged {segs.Select(g => g.LogMin).DefaultIfEmpty(100).Min():0}%); rules {segs.Sum(g => g.Rules) / 1e6:0.00}M vs logged {segs.Sum(g => g.Real) / 1e6:0.00}M");
        if (A.Contains("--heal"))
        {
            Console.WriteLine("   heal sources: " + Metrics.Breakdown(w, sim.Heals, hpPerPot));
            foreach (var g in segs)
                Console.WriteLine($"   heal {F(g.Start),8}-{F(g.End),-8} {g.Label,-30} logged {g.Real / 1000,7:0}k  rules {g.Rules / 1000,7:0}k  lowest HP logged {g.LogMin,4:0}%  counterfactual {g.CfMin,4:0}%" + (g.CfMin < g.LogMin - 5 ? Environment.NewLine + "        " + g.Detail : ""));
        }
        // phases
        Console.WriteLine("   by phase:          up(s)  GCD up%   Dia%  GCDs cancels  pot/min  idle(s) top reasons");
        foreach (var (phase, p) in sim.PhaseMs.OrderBy(k => w.R.Phases.FindIndex(ph => ph.Name == k.Key)))
        {
            bool In(int t) => w.R.PhaseAt(t) == phase;
            var pot = sim.Damage.Where(d => In(d.T)).Sum(d => d.Potency * d.Mult);
            var idle = sim.Idle.Where(i => i.Phase == phase).GroupBy(i => i.Reason).Select(g => (g.Key, Ms: g.Sum(i => i.Ms))).OrderByDescending(g => g.Ms).ToList();
            Console.WriteLine($"   {phase,-18} {p.Avail / 1000.0,6:0} {100.0 * p.Busy / Math.Max(1, p.Avail),7:0.0} {100.0 * p.Dia / Math.Max(1, p.Avail),6:0} {sim.Log.Count(e => In(e.T) && WhmSim.GcdNames.Contains(e.What)),5} {sim.CancelLog.Count(c => In(c.T)),7} {pot / Math.Max(0.01, p.Avail / 60000.0),8:0} {idle.Sum(i => i.Ms) / 1000.0,7:0.0} " +
                              string.Join("; ", idle.Take(3).Select(i => $"{i.Key} {i.Ms / 1000.0:0.0}")));
            // The logged player's damage in the same phase, by source, against the rules'.
            var real = act.Damage.Where(d => In(d.T)).GroupBy(d => d.Src).ToDictionary(g => g.Key, g => g.Sum(d => d.Potency * d.Mult));
            var mine = sim.Damage.Where(d => In(d.T)).GroupBy(d => d.Src).ToDictionary(g => g.Key, g => g.Sum(d => d.Potency * d.Mult));
            Console.WriteLine($"      logged pot/min {real.Values.Sum() / Math.Max(0.01, p.Avail / 60000.0),6:0}; by source rules/logged: " +
                              string.Join(", ", mine.Keys.Union(real.Keys).OrderByDescending(k => Math.Abs(mine.GetValueOrDefault(k) - real.GetValueOrDefault(k))).Take(6)
                                  .Select(k => $"{k} {mine.GetValueOrDefault(k):0}/{real.GetValueOrDefault(k):0}")));
        }
        Console.WriteLine("   GCD idle while a target was up, by reason and rule (s):");
        foreach (var g in sim.Idle.GroupBy(i => (i.Reason, i.Rule)).Select(g => (g.Key, Ms: g.Sum(i => i.Ms), N: g.Count())).OrderByDescending(g => g.Ms).Take(12))
            Console.WriteLine($"      {g.Ms / 1000.0,6:0.0}s  x{g.N,-3} {g.Key.Reason}  [{g.Key.Rule}]");
        if (sim.CancelLog.Count > 0)
            Console.WriteLine("   cancels by rule: " + string.Join(", ", sim.CancelLog.GroupBy(c => c.Rule).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine("   rule fires: " + string.Join("; ", sim.RuleFires.OrderByDescending(k => k.Value).Select(k => $"{Short(k.Key)} {k.Value}")));
        if (sim.Anomalies.Count > 0) Console.WriteLine("   anomalies: " + string.Join("; ", sim.Anomalies.Take(10)));
        if (sim.Unmodelled > 0) Console.WriteLine($"   ! unmodelled actions returned {sim.Unmodelled} times");
        if (A.Contains("--detail"))
        {
            Console.WriteLine("   plan presses (layer):");
            foreach (var o in sim.Ogcds.Where(o => o.Rule.StartsWith("DMU mitigation plan")))
                Console.WriteLine($"      {F(o.T),8} {o.Name,-22} GCD left {o.GcdLeft / 1000.0:0.00}s  [{Short(o.Rule)}]");
            Console.WriteLine("   Divine Caress shields (who: landed-ended how):");
            foreach (var g in sim.ShieldLog.Where(v => v.Status == WS.CaressShield).GroupBy(v => v.From))
                Console.WriteLine($"      {F(g.Key),8} " + string.Join(", ", g.Select(v => $"{v.Target.Name.Split(' ')[0]} {F(v.To)} {v.How}")));
            Console.WriteLine("   idle segments of 1 s or more:");
            foreach (var i in sim.Idle.Where(i => i.Ms >= 1000))
                Console.WriteLine($"      {F(i.T),8} {i.Ms / 1000.0,5:0.0}s {i.Phase,-18} {i.Reason} [{Short(i.Rule)}]");
            Console.WriteLine("   cancels:");
            foreach (var c in sim.CancelLog) Console.WriteLine($"      {F(c.T),8} {c.Name} moved {c.MovedInMs / 1000.0:0.00}s in [{Short(c.Rule)}]");
        }
    }

    public static string Short(string rule) => rule.Replace("DMU mitigation plan (White Mage): ", "plan: ").Replace("White Mage single target: ", "");

    private static void PrintTotals(List<PullRow> rows, double hpPerPot)
    {
        if (rows.Count == 0) { Console.WriteLine("no pulls"); return; }
        double mins = rows.Sum(r => r.X.Sim.AvailMs) / 60000.0;
        double eff = rows.Sum(r => Eff(r.X.Sim.Damage)), aeff = rows.Sum(r => Eff(r.Act.Damage));
        int g = rows.Sum(r => r.X.Sim.Gcds), ag = rows.Sum(r => r.Act.Gcds);
        var slots = rows.SelectMany(r => r.Slots).ToList();
        Console.WriteLine($"\n===== {rows.Count} pulls, {mins:0.0} minutes with a target up and the player alive");
        void Row(string n, double a, double b, string fmt = "0") => Console.WriteLine($"   {n,-44}{a.ToString(fmt),10}{b.ToString(fmt),10}");
        // Party mitigations' reach: how many presses reached every living party member (Caress and Plenary 30 y, the Bell 20 y).
        Console.WriteLine("   reach of the party mitigations (presses, reaching everyone, average missed):");
        foreach (var gr in rows.SelectMany(r => r.X.Sim.ReachLog).GroupBy(x => (x.What, Rule: Short(x.Rule))).OrderBy(gg => gg.Key.What))
            Console.WriteLine($"      {gr.Key.What,-20} {gr.Count(),4}  all {gr.Count(x => x.Reached >= x.Alive),4}  missed {gr.Average(x => x.Alive - x.Reached),4:0.00}  [{gr.Key.Rule}]" +
                              (gr.Any(x => x.Reached < x.Alive) ? "  short at " + string.Join(" ", gr.Where(x => x.Reached < x.Alive).Select(x => $"{F(x.T)}({x.Reached}/{x.Alive})")) : ""));
        // Tankbusters: the rules' Aquaveil and Divine Benison on the tank at each hit.
        var busterNames = new HashSet<string> { "Revolting Ruin III", "Hyperdrive", "Thunder III", "Ultimate Embrace", "Wings of Destruction", "Flare", "Chaotic Flare", "Flare Diffusion" };
        var bust = new List<(string Key, bool Veil, bool Ben)>();
        foreach (var r in rows)
        {
            var (w, sim) = (r.X.W, r.X.Sim);
            foreach (var h in w.HitsOnParty.Where(h => !h.Auto && World.IsTank(h.Target) && busterNames.Contains(h.Name) && h.InstPct >= 15).OrderBy(h => h.T))
            {
                bool veil = sim.Log.Any(e => e.What == "Aquaveil" && e.Target.StartsWith(h.Target.Name) && e.T + Metrics.LandMs <= h.T && h.T < e.T + Metrics.LandMs + 8000);
                bool ben = ShieldOn(sim, h.Target, WS.Benison, h.T);
                bust.Add(($"{w.R.PhaseAt(h.T) ?? "",-18} {h.Name}", veil, ben));
            }
        }
        Console.WriteLine("   tankbuster hits on a tank (15%+ of the party's HP): hits, with the rules' Aquaveil, with their Divine Benison");
        foreach (var gr in bust.GroupBy(b => b.Key))
            Console.WriteLine($"      {gr.Key,-44} {gr.Count(),4}  veil {gr.Count(b => b.Veil),4}  benison {gr.Count(b => b.Ben),4}");
        Console.WriteLine($"   {"",-44}{"rules",10}{"logged",10}");
        Row("potency per minute", eff / mins, aeff / mins);
        Row("GCDs per minute", g / mins, ag / mins, "0.00");
        Row("GCD uptime while a target was up (%)", 100.0 * rows.Sum(r => r.X.Sim.BusyMs) / rows.Sum(r => r.X.Sim.AvailMs), 0, "0.0");
        Row("Dia uptime (%)", 100.0 * rows.Sum(r => r.X.Sim.DiaUpMs) / rows.Sum(r => r.X.Sim.AvailMs), 100.0 * rows.Sum(r => r.Act.DiaUpMs) / rows.Sum(r => r.X.Sim.AvailMs), "0.0");
        Row("cast cancels", rows.Sum(r => r.X.Sim.Cancels), rows.Sum(r => r.Act.Cancels));
        Row("Misery in raid buffs", rows.Sum(r => r.X.Sim.Damage.Count(d => d.Src == "Afflatus Misery" && d.Mult > 1.0001)), rows.Sum(r => r.Act.MiseryInBuffs));
        Row("Blood Lily wasted", rows.Sum(r => r.X.Sim.BloodWasted), 0);
        Row("GCD clipped by weaves (s)", rows.Sum(r => r.X.Sim.ClipMs) / 1000.0, 0, "0.0");
        foreach (var n in Counted)
            Row($"{n} (count)", rows.Sum(r => r.X.Sim.Log.Count(e => e.What == n && e.T >= 0)), rows.Sum(r => r.Act.Counts.GetValueOrDefault(n)));
        var bigN = rows.Sum(r => r.BigN);
        Console.WriteLine($"   big hits (20%+ HP): {bigN}; White Mage mitigation (Temperance, Plenary, Caress) on the player: rules {rows.Sum(r => r.BigSim)}, logged {rows.Sum(r => r.BigReal)}");
        if (slots.Count > 0)
            Console.WriteLine($"   sheet slots: rules {slots.Count(x => x.Sim)}/{slots.Count}, logged {slots.Count(x => x.Real)}/{slots.Count}  (" +
                              string.Join(", ", slots.GroupBy(x => x.Label).OrderBy(gg => gg.Key).Select(gg => $"{gg.Key} {gg.Count(x => x.Sim)}/{gg.Count(x => x.Real)}/{gg.Count()}")) + ")");
        var segs = rows.SelectMany(r => r.Segs).ToList();
        var cfLow = segs.Select(s => s.CfMin).DefaultIfEmpty(100).Min();
        Console.WriteLine($"   healing ({hpPerPot:0} HP per potency): rules {segs.Sum(s => s.Rules) / 1e6:0.00}M vs logged {segs.Sum(s => s.Real) / 1e6:0.00}M; lowest counterfactual HP {cfLow:0}% (logged {segs.Select(s => s.LogMin).DefaultIfEmpty(100).Min():0}%); segments 5+ points under logged {segs.Count(s => s.CfMin < s.LogMin - 5)}/{segs.Count}");
        var reasons = rows.SelectMany(r => r.X.Sim.Idle).GroupBy(i => (i.Reason, i.Rule)).Select(gg => (gg.Key, Ms: gg.Sum(i => i.Ms))).OrderByDescending(x => x.Ms).Take(10);
        Console.WriteLine("   rules' GCD idle by reason (s): " + string.Join("; ", reasons.Select(r => $"{r.Key.Reason} [{Short(r.Key.Rule)}] {r.Ms / 1000.0:0}")));
        var fires = rows.SelectMany(r => r.X.Sim.RuleFires).GroupBy(k => k.Key).Select(gg => (gg.Key, N: gg.Sum(k => k.Value))).OrderByDescending(k => k.N);
        Console.WriteLine("   rule fires: " + string.Join("; ", fires.Select(k => $"{Short(k.Key)} {k.N}")));
        var minMp = rows.Min(r => r.X.Sim.MinMp);
        Console.WriteLine($"SUMMARY pulls={rows.Count} mins={mins:0.0} pot={eff / mins:0} real={aeff / mins:0} gcd={g / mins:0.00} realgcd={ag / mins:0.00} canc={rows.Sum(r => r.X.Sim.Cancels)} mp={minMp} " +
                          $"slots={slots.Count(x => x.Sim)}/{slots.Count} realslots={slots.Count(x => x.Real)} big={rows.Sum(r => r.BigSim)}/{bigN} realbig={rows.Sum(r => r.BigReal)} cf={cfLow:0} clip={rows.Sum(r => r.X.Sim.ClipMs) / 1000.0:0.0} " +
                          $"dia={100.0 * rows.Sum(r => r.X.Sim.DiaUpMs) / rows.Sum(r => r.X.Sim.AvailMs):0.0} blood={rows.Sum(r => r.X.Sim.BloodWasted)} " +
                          $"cfmean={segs.Select(s => s.CfMin).DefaultIfEmpty(100).Average():0.0} cfneg={segs.Count(s => s.CfMin < 0)} heal={segs.Sum(s => s.Rules) / 1e6:0.00} " +
                          $"mib={rows.Sum(r => r.X.Sim.Damage.Count(d => d.Src == "Afflatus Misery" && d.Mult > 1.0001))}/{rows.Sum(r => r.X.Sim.Damage.Count(d => d.Src == "Afflatus Misery"))} " +
                          $"g4ib={rows.Sum(r => r.X.Sim.Damage.Count(d => d.Src == "Glare IV" && d.Mult > 1.0001))}/{rows.Sum(r => r.X.Sim.Damage.Count(d => d.Src == "Glare IV"))}");
    }

    // ---------------------------------------------------------------- pulls, calibrate, timeline

    private static int Pulls(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        foreach (var s in Select(index(log), spec, meId))
        {
            var w = new World(PullLoader.Load(s, null, registry), meId);
            var wins = SamCommands.RaidWindows(w, w.R.EndMs);
            var buffs = wins.Count > 0
                ? string.Join(",", w.R.Statuses.Where(st => st.Target.Id == meId && World.RaidSelfBuffs.Contains(st.Name) && st.StartMs >= wins[0].Start - 100 && st.StartMs <= wins[0].Start + 8000)
                    .OrderBy(st => st.StartMs).Select(st => $"{st.Name} {st.StartMs / 1000.0:0.0}").Distinct())
                : "";
            var opener = string.Join(" ", w.R.Actions.Where(a => a.Source.Id == meId && a.T < 12000 && a.T > -5000).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : a.T).Select(gg => gg.First())
                .OrderBy(a => a.T).Select(a => $"{a.T / 1000.0:0.0}:{a.Name}"));
            Console.WriteLine($"#{s.Ordinal,-4} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm:ss} {s.Outcome,-8} {(s.EndTicks - s.StartTicks) / 1e7,6:N1}s {s.FurthestPhase,-20} job {s.Party.First(m => m.Id == meId).Job} GCD {MeasureGcd(w, meId)}  first raid window {(wins.Count > 0 ? $"{F(wins[0].Start)}-{F(wins[0].End)} [{buffs}]" : "none")}  windows {wins.Count}");
            if (A.Contains("--opener")) Console.WriteLine("       " + opener);
        }
        return 0;
    }

    private static int Calibrate(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var casts = new List<(World W, CastEvent C)>();
        foreach (var s in Select(index(log), spec, meId))
        {
            var w = new World(PullLoader.Load(s, null, registry), meId);
            foreach (var c in w.R.Casts.Where(c => c.Source.Id == meId && c.DurationMs >= 1000 && c.StartMs > 0))
                casts.Add((w, c));
        }
        var done = casts.Count(x => x.C.Outcome != CastOutcome.Cancelled);
        var canc = casts.Count - done;
        Console.WriteLine($"{done} completed casts, {canc} cancelled; columns: false cancels of completed casts / real cancels seen");
        foreach (var run in new[] { 5.0 })
            foreach (var step in new[] { 0.25, 0.5 })
            {
                var line = $"  burst {run} y/s, step {step} y";
                foreach (var slide in new[] { 500, 700, 900 })
                {
                    int fp = 0, tp = 0;
                    foreach (var (w, c) in casts)
                    {
                        w.RunSpeed = run; w.MinStep = step;
                        var cancelled = c.Outcome == CastOutcome.Cancelled;
                        var stop = cancelled ? Math.Min(c.EndMs + 300, c.StartMs + c.DurationMs - slide) : c.EndMs - slide;
                        var any = false;
                        for (var t = c.StartMs + 100; t <= stop && !any; t += 25) any = w.MovingBurst(w.Me, t);
                        if (cancelled && any) tp++;
                        if (!cancelled && any) fp++;
                    }
                    line += $"  slide {slide}: {100.0 * fp / Math.Max(1, done),4:0.0}% / {100.0 * tp / Math.Max(1, canc),3:0}%";
                }
                Console.WriteLine(line);
            }
        return 0;
    }

    private static int Timeline(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", RepoRules);
        var s = Select(index(log), spec, meId).First();
        double from = double.Parse(Opt("from", "-16")), to = double.Parse(Opt("to", "9999"));
        var w0 = Load(s, meId, registry);
        // --cfhp: HP per potency from --hpp=, else this pull's own heals (a "run" over several pulls pools them, so pass its value).
        var cal0 = Metrics.CalibrationSample(w0, HealTable);
        var hpp = Opt("hpp", "") is { Length: > 0 } hv ? double.Parse(hv) : cal0.Pot >= 3000 ? (double)cal0.Raw / cal0.Pot : 95.0;
        var x = Simulate(s, w0, PooledGcd([w0], meId), rulesDir, hpp);
        var act = ActualOf(x, meId);
        var ev = new List<(int T, int Order, string Line)>();
        foreach (var e in x.Sim.Log)
            ev.Add((e.T, 2, $"RULES {e.What}{(e.Target.Length > 0 ? " -> " + e.Target : "")}{(e.Note.Length > 0 ? $"  [{e.Note}]" : "")}   ({Short(e.Rule)})"));
        foreach (var e in act.Events) ev.Add((e.T, 3, $"{"",60}{x.W.Me.Name.Split(' ')[0]}: {e.Name}{(e.Target.Length > 0 ? " -> " + e.Target : "")}"));
        foreach (var i in x.Sim.Idle.Where(i => i.Ms >= 500)) ev.Add((i.T, 1, $"      idle {i.Ms / 1000.0:0.0}s: {i.Reason} [{Short(i.Rule)}]"));
        var seen = new Dictionary<string, int>();
        foreach (var c in x.W.R.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Helper or ActorKind.Enemy))
        {
            if (seen.TryGetValue(c.Name, out var last) && Math.Abs(c.StartMs - last) <= 1000) continue;
            seen[c.Name] = c.StartMs;
            ev.Add((c.StartMs, 0, $"      boss cast: {c.Name} ({c.DurationMs / 1000.0:0.0}s)"));
        }
        foreach (var rw in x.W.Raidwides) ev.Add((rw.T, 1, $"      HIT {rw.Name} #{rw.Ordinal} ({rw.AvgPct:0}% HP) rules: {SimCover(x.Sim, x.W, rw.T)}"));
        foreach (var d in x.W.R.Deaths) ev.Add((d.T, 1, $"      DEATH {d.Victim.Name}"));
        foreach (var p in x.W.R.Phases.Where(p => !p.IsSegment)) ev.Add((p.StartMs, 0, $"=== {p.Name}"));
        var wins = SamCommands.RaidWindows(x.W, x.EndMs);
        foreach (var wv in wins) { ev.Add((wv.Start, 0, $"      [raid window starts, up to {wv.Max} buffs]")); ev.Add((wv.End, 0, "      [raid window ends]")); }
        Actor? lastT = null;
        for (var t = (int)(from * 1000); t <= Math.Min(to * 1000, x.EndMs); t += 500)
        {
            var tg = SamSim.TargetAt(x.W, t);
            if (tg != lastT) ev.Add((t, 0, $"      [target {tg?.Name ?? "none"}]"));
            lastT = tg;
        }
        foreach (var e in ev.Where(e => e.T >= from * 1000 && e.T <= to * 1000).OrderBy(e => e.T).ThenBy(e => e.Order))
            Console.WriteLine($"{F(e.T),8} {e.Line}");
        return 0;
    }
}
