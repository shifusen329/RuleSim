using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The Astrologian replay's commands (2026-10-30). Program.cs hands over when the pulls spec is "ast" (the --me player's
///     Astrologian pulls in Dancing Mad) or --job=ast is given (any pulls; "dmu" = every Dancing Mad pull with --me in it, as a
///     stand-in). &lt;log&gt; is a file, a name in the ACT log folder (RR_LOGS), or "all" (every log from --from=YYYYMMDD on).
///
///   run       &lt;log|all&gt; ast|dmu|N,M-K [--job=ast] [--me=HEX] [--rules=DIR] [--select=FILE|none] [--set=a=b,...] [--gcd=ms|measure]
///             [--precards=auto|0|1] [--stillcasts=1|0] [--step=0.25 --slide=700] [--detail] [--slots|--slotlist] [--heal] [--tanks] [--cfhp] [--episodes]
///             [--trace --from=s --to=s]
///   timeline  &lt;log&gt; &lt;pull&gt; --job=ast ... [--from=s --to=s] [--trace]
///   calibrate &lt;log&gt; ast|dmu --job=ast [--me=HEX]       movement model vs the player's real cast cancels
///   pulls     &lt;log|all&gt; ast|dmu --job=ast [--me=HEX] [--opener]
///
/// Rules' damage: potency x the raid buffs on you (everyone else's from the log, plus the rules' own Divination, 6%). The
/// party damage Divination and the Balance/Spear add (6%, 3% on the wrong role) is scored from the party's logged damage in
/// each buff's window, for the rules and for the logged Astrologian alike.
/// </summary>
internal static class AstCommands
{
    public const byte AstJob = 33;

    public static bool Handles(string[] args) =>
        args.Length > 2 && args[0] is "run" or "timeline" or "calibrate" or "pulls" && (args[2] == "ast" || args.Contains("--job=ast"));

    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private const string RepoRules = @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples";
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rr ? rr
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");

    public static int Main(string[] args, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        A = args;
        var meId = Convert.ToUInt32(Opt("me", "1001A4A8"), 16);
        return args[0] switch
        {
            "pulls" => Pulls(args[1], args[2], meId, registry, index),
            "calibrate" => Calibrate(args[1], args[2], meId, registry, index),
            "run" => Run(args[1], args[2], meId, registry, index),
            "timeline" => Timeline(args[1], args[2], meId, registry, index),
            _ => 1,
        };
    }

    // ---------------------------------------------------------------- pulls and files

    private static List<string> Logs(string spec)
    {
        if (spec != "all") return [File.Exists(spec) ? spec : Path.Combine(LogsDir, spec)];
        var from = Opt("from", "20260901");
        return Directory.GetFiles(LogsDir, "Network_*.log")
            .Where(f => string.Compare(Path.GetFileNameWithoutExtension(f)[^8..], from, StringComparison.Ordinal) >= 0)
            .OrderBy(f => Path.GetFileNameWithoutExtension(f)[^8..]).ThenBy(f => f).ToList();
    }

    internal static List<PullSummary> Select(FileIndex idx, string spec, uint meId)
    {
        var all = idx.Pulls.Where(p => p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond).ToList();
        if (spec == "ast") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId && m.Job == AstJob)).ToList();
        if (spec == "dmu") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId)).ToList();
        var want = new HashSet<int>();
        foreach (var part in spec.Split(','))
            if (part.Contains('-')) { var x = part.Split('-'); for (var i = int.Parse(x[0]); i <= int.Parse(x[1]); i++) want.Add(i); }
            else want.Add(int.Parse(part));
        return all.Where(p => want.Contains(p.Ordinal)).ToList();
    }

    private static string SymbolsPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "ast_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "ast_symbols.json");
    }

    /// <summary> RuleEngine.FilesFor for AST_ST_CustomRules (Malefic I-IV, Fall Malefic): the job's layers for the button in name
    /// order, then the selected file (--select, default AST_ST_DPS.json), or with --select=none every other file for it. </summary>
    public static List<CompiledRuleSet> Compile(AstSim sim, string rulesDir, List<string> describe)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var buttons = AA.MaleficButtons.ToHashSet();
        var selected = Opt("select", "AST_ST_DPS.json");
        var layers = new List<CompiledRuleSet>();
        var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            RuleSetFile? file;
            try { file = JsonConvert.DeserializeObject<RuleSetFile>(File.ReadAllText(path)); }
            catch (JsonException e) { describe.Add($"{Path.GetFileName(path)}: JSON error {e.Message}"); continue; }
            if (file is null || !string.Equals(file.Job, "AST", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in Opt("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var set = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, AA.FallMalefic);
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

    public static List<int> GcdIntervals(World w, uint meId)
    {
        var starts = w.R.Casts.Where(c => c.Source.Id == meId && c.Name == "Fall Malefic").Select(c => c.StartMs).OrderBy(x => x).ToList();
        return starts.Zip(starts.Skip(1), (a, b) => b - a).Where(d => d is >= 2200 and <= 2700).ToList();
    }

    public static int PooledGcd(IEnumerable<World> worlds, uint meId)
    {
        if (Opt("gcd", "measure") is var g && g != "measure") return int.Parse(g);
        var all = worlds.SelectMany(w => GcdIntervals(w, meId)).OrderBy(x => x).ToList();
        return all.Count >= 5 ? all[all.Count / 2] : 2500;
    }

    /// <summary> Did the logged Astrologian hold cards at the pull? Its first draw in the pull is Umbral Draw, or it played a card
    /// before its first draw (from 30 s before the pull). --precards=0|1 overrides. </summary>
    public static bool PreCards(World w, uint meId)
    {
        var o = Opt("precards", "auto");
        if (o != "auto") return o == "1";
        var mine = w.R.Actions.Where(a => a.Source.Id == meId && a.T >= -30000).OrderBy(a => a.T).ToList();
        var firstDraw = mine.FirstOrDefault(a => a.Name is "Astral Draw" or "Umbral Draw");
        if (firstDraw?.Name == "Umbral Draw") return true;
        var cardNames = new HashSet<string> { "the Balance", "the Arrow", "the Spire", "Lord of Crowns" };
        return mine.Any(a => cardNames.Contains(a.Name) && (firstDraw is null || a.T < firstDraw.T));
    }

    // ---------------------------------------------------------------- simulate

    public sealed record Result(PullSummary S, World W, AstSim Sim, List<string> Files, int EndMs, bool PreCards);

    private static World Load(PullSummary s, uint meId, EncounterRegistry registry) =>
        new(PullLoader.Load(s, null, registry), meId) { MinStep = double.Parse(Opt("step", "0.25")), RunSpeed = double.Parse(Opt("run", "5")) };

    private static Result Simulate(PullSummary s, World w, int gcd, string rulesDir, double hpPerPot)
    {
        var r = w.R;
        var pre = PreCards(w, w.Me.Id);
        var sim = new AstSim(w, gcd, pre)
        {
            T = -16000, SlideMs = int.Parse(Opt("slide", "700")), HpPerPot = hpPerPot, StillCasts = Opt("stillcasts", "1") == "1",
            Trace = A.Contains("--trace"), TraceFrom = (int)(double.Parse(Opt("from", "-16")) * 1000), TraceTo = (int)(double.Parse(Opt("to", "9999")) * 1000),
        };
        var describe = new List<string>();
        var files = Compile(sim, rulesDir, describe);
        AstTankModel? cfm = null;
        if (A.Contains("--cfhp") && hpPerPot > 0)
        {
            var m = new AstTankModel(w, sim, hpPerPot);
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
        return new Result(s, w, sim, describe, r.EndMs, pre);
    }

    // ---------------------------------------------------------------- the logged player

    public sealed class Actual
    {
        public readonly Dictionary<string, int> Counts = new();
        public readonly List<(int T, string Src, double Potency, double Mult)> Damage = new();
        public readonly List<(int T, string Name, string Target)> Events = new();
        /// <summary> Logged damage per source against its potency (the potency table's check). </summary>
        public readonly Dictionary<string, (double Dmg, double Pot)> Check = new();
        public int Gcds, Cancels, Draws, CardPlays;
        public double DotUpMs;
        public List<StatusInterval> Mine = new();
        public List<(int S, int E)> Div = new();
    }

    private static readonly Dictionary<string, (int Pot, int Others)> Pot = new()
    {
        ["Fall Malefic"] = (270, 0), ["Gravity II"] = (140, 140), ["Lord of Crowns"] = (400, 400), ["Oracle"] = (860, 430), ["Macrocosmos"] = (270, 162),
        ["Stellar Explosion"] = (310, 310), ["Stellar Burst"] = (205, 205),
    };

    public static Actual ActualOf(Result x, uint meId)
    {
        var w = x.W;
        var a = new Actual();
        a.Div = w.R.Statuses.Where(s => s.Source?.Id == meId && s.Target.Id == meId && s.StatusId == AS.Divination).Select(s => (s.StartMs, s.EndMs)).ToList();
        double Mult(Actor? tgt, int t) => w.RaidBuffs(tgt, t).Mult * (a.Div.Any(d => d.S <= t && t < d.E) ? 1.06 : 1);
        // the Earthly Star's explosions come from the star (owned by the player)
        var mine = w.R.Actions.Where(e => e.Source.Id == meId || e.Source.OwnerId == meId && e.Name is "Stellar Explosion" or "Stellar Burst")
            .GroupBy(e => e.Sequence != 0 ? (long)e.Sequence : ((long)e.ActionId << 32) + e.T).Select(g => g.First()).OrderBy(e => e.T).ToList();
        foreach (var e in mine)
        {
            var tgt = e.PrimaryTarget ?? e.Hits.FirstOrDefault()?.Target;
            a.Events.Add((e.Cast?.StartMs ?? e.T, e.Name, tgt is { IsPlayer: true } && tgt.Id != meId ? tgt.Name : ""));
            if (e.T < -3000) continue;
            if (e.T >= 0)
            {
                a.Counts[e.Name] = a.Counts.GetValueOrDefault(e.Name) + 1;
                if (AstSim.GcdNames.Contains(e.Name)) a.Gcds++;
                if (e.Name is "Astral Draw" or "Umbral Draw") a.Draws++;
                if (e.Name is "the Balance" or "the Spear" or "the Arrow" or "the Bole" or "the Spire" or "the Ewer" or "Lord of Crowns" or "Lady of Crowns") a.CardPlays++;
            }
            if (!Pot.TryGetValue(e.Name, out var p)) continue;
            var hits = e.Hits.Where(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage).ToList();
            var hitTargets = hits.Select(h => h.Target).Distinct().ToList();
            if (hitTargets.Count == 0) continue;
            var first = tgt is { IsPlayer: false } && hitTargets.Contains(tgt) ? tgt : hitTargets[0];
            a.Damage.Add((Math.Max(0, e.T), e.Name, p.Pot, Mult(first, e.T)));
            foreach (var o in hitTargets.Where(o => o != first && o.Kind == ActorKind.Boss))
                if (p.Others > 0) a.Damage.Add((Math.Max(0, e.T), e.Name + " (2nd target)", p.Others, Mult(o, e.T)));
            var fh = hits.Where(h => h.Target == first).Sum(h => (double)h.Damage);
            var c = a.Check.GetValueOrDefault(e.Name);
            a.Check[e.Name] = (c.Dmg + fh, c.Pot + p.Pot * Mult(first, e.T));
        }
        var combusts = w.R.Statuses.Where(s => s.Source?.Id == meId && s.StatusId == AS.Combust3 && !s.Target.IsPlayer).ToList();
        foreach (var st in combusts)
            for (var t = st.StartMs + 1500; t < Math.Min(st.EndMs, w.R.EndMs); t += 3000)
                if (st.Target.IsPresent(t) && st.Target.IsTargetable(t) && t >= 0) a.Damage.Add((t, "Combust III (tick)", 70, Mult(st.Target, st.StartMs)));
        foreach (var tk in w.R.Ticks.Where(tk => !tk.IsHeal && tk.Source?.Id == meId && !tk.Target.IsPlayer && tk.T >= 0))
        {
            var c = a.Check.GetValueOrDefault("Combust III (tick)");
            a.Check["Combust III (tick)"] = (c.Dmg + tk.Amount, c.Pot + 70 * Mult(tk.Target, tk.T));
        }
        a.Cancels = w.R.Casts.Count(c => c.Source.Id == meId && c.Outcome == CastOutcome.Cancelled && c.StartMs >= 0);
        a.Mine = w.R.Statuses.Where(s => s.Source?.Id == meId).ToList();
        for (var t = 0; t < x.EndMs; t += 100)
        {
            if (w.Dead(w.Me, t) || SamSim.TargetAt(w, t) is not { } tg || SamSim.Incapacitated(w, t) is not null) continue;
            if (combusts.Any(s => s.Target.Id == tg.Id && s.Active(t))) a.DotUpMs += 100;
        }
        return a;
    }

    // ---------------------------------------------------------------- party damage (what Divination and the cards add)

    private sealed class PartyDamage
    {
        private readonly Dictionary<uint, List<(int T, double D)>> by = new();

        public PartyDamage(World w)
        {
            var party = w.Party.Select(p => p.Id).Where(id => id != w.Me.Id).ToHashSet();
            void Add(uint who, int t, double d) { if (party.Contains(who)) (by.TryGetValue(who, out var l) ? l : by[who] = []).Add((t, d)); }
            foreach (var a in w.R.Actions)
            {
                var owner = a.Source.IsPlayer ? a.Source.Id : a.Source.OwnerId;
                if (!party.Contains(owner)) continue;
                foreach (var h in a.Hits.Where(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage && h.Damage > 0)) Add(owner, h.T, h.Damage);
            }
            foreach (var tk in w.R.Ticks.Where(tk => !tk.IsHeal && tk.Source is not null && !tk.Target.IsPlayer))
                Add(tk.Source!.IsPlayer ? tk.Source.Id : tk.Source.OwnerId, tk.T, tk.Amount);
            foreach (var l in by.Values) l.Sort((p, q) => p.T.CompareTo(q.T));
        }

        public double In(uint who, int from, int to) => by.TryGetValue(who, out var l) ? l.Where(x => x.T >= from && x.T < to).Sum(x => x.D) : 0;
    }

    private static bool RightRole(string card, Actor t) => card == "the Balance" ? AstSim.MeleeOrTank(t) : AstSim.RangedOrHealer(t);

    // ---------------------------------------------------------------- the sheet (Astrologian column, 11)

    private static IEnumerable<int> Presses(AstSim sim, string name) => sim.Log.Where(e => e.What == name).Select(e => e.T);

    public static bool SimCu(AstSim sim, int t) => sim.ReachLog.Any(r => r.What == (int)AA.CollectiveUnconscious && r.Reached >= 3 && r.T + Metrics.LandMs <= t && t < r.T + Metrics.LandMs + 10000);
    public static bool SimSun(AstSim sim, int t) => sim.ReachLog.Any(r => r.What == (int)AA.SunSign && r.Reached >= 3 && r.T + 1500 <= t && t < r.T + 16500);
    public static bool SimNs(AstSim sim, int t) => sim.ShieldLog.Count(s => s.Status == AS.NSShield && s.From + Metrics.LandMs <= t && t <= s.To + 50) >= 3;
    public static bool SimMacro(AstSim sim, int t) => sim.MacroLog.Any(m => m.On >= 3 && m.From + Metrics.LandMs <= t && t <= m.To);

    public static List<Metrics.Slot> Slots(Result x, Actual a, List<Metrics.SheetRow> sheet)
    {
        var (w, sim) = (x.W, x.Sim);
        return Metrics.SlotsWith(w, sheet, (label, t) => label switch
        {
            "Collective Unconscious" => (SimCu(sim, t), Metrics.RealUp(w, a.Mine, "Collective Unconscious", t)),
            "Sun Sign" => (SimSun(sim, t), Metrics.RealUp(w, a.Mine, "Sun Sign", t)),
            "Neutral Sect" => (SimNs(sim, t), Metrics.RealUp(w, a.Mine, "Neutral Sect", t)),
            "Macrocosmos" => (SimMacro(sim, t), Metrics.RealUp(w, a.Mine, "Macrocosmos", t)),
            _ => null,
        });
    }

    public static string SlotWhy(AstSim sim, Metrics.Slot s)
    {
        var name = s.Label switch { "Neutral Sect" => "Helios Conjunction", _ => s.Label };
        var near = sim.Log.Where(e => (e.What == name || e.What == s.Label) && Math.Abs(e.T - s.T) <= 60000).OrderBy(e => Math.Abs(e.T - s.T)).FirstOrDefault();
        if (near is null) return "no press within 60 s";
        return $"nearest {near.What} {(near.T - s.T) / 1000.0:+0.0;-0.0}s [{Short(near.Rule)}]";
    }

    private static readonly Dictionary<string, int> HealTable = new()
    {
        ["Benefic II"] = 800, ["Benefic"] = 500, ["Helios"] = 400, ["Celestial Opposition"] = 200, ["Lady of Crowns"] = 400, ["Celestial Intersection"] = 200,
    };

    // ---------------------------------------------------------------- run

    private sealed class Row
    {
        public string Label = "";
        public double Avail, SimPot, RealPot, SimContrib, RealContrib, SimDivOverlap, RealDivOverlap, HealRules, HealReal, CfMin = 100, LogMin = 100;
        public int SimGcd, RealGcd, SimCanc, RealCanc, MinMp = 10000, SimDivs, RealDivs, SimCards, SimCardsIn, SimCardsRight, RealCards, RealCardsIn, RealCardsRight,
            SimLord, SimLordIn, SimOracle, SimOracleIn, RealLord, RealLordIn, RealOracle, RealOracleIn, SimStars, SimStarsManual, RealStars, SimLs, RealLs,
            SimLost, RealDraws, RealPlays, AccHeals, AccDanger, RealAccDanger, Unmodelled, CastsLost;
        public double SimDot, RealDot, OneAvail, OneSim, OneReal;   // One*: single-boss phases (P1, P2, P5)
        public List<Metrics.Slot> Slots = new();
        public readonly Dictionary<string, int> SimCounts = new(), RealCounts = new();
        public readonly List<string> Notes = new();
    }

    public static readonly string[] Counted = ["Fall Malefic", "Combust III", "Gravity II", "Helios Conjunction", "Aspected Benefic", "Benefic II", "Helios",
        "Macrocosmos", "Divination", "Lightspeed", "Astral Draw", "Umbral Draw", "the Balance", "the Spear", "the Arrow", "the Bole", "the Spire", "the Ewer",
        "Lord of Crowns", "Lady of Crowns", "Oracle", "Earthly Star", "Stellar Detonation", "Essential Dignity", "Celestial Intersection", "Exaltation",
        "Collective Unconscious", "Celestial Opposition", "Horoscope", "Neutral Sect", "Sun Sign", "Synastry", "Lucid Dreaming", "Swiftcast", "Ascend"];

    private static int Run(string logSpec, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", RepoRules);
        var sheet = Metrics.LoadSheet(Opt("sheet", @"C:\Users\quanh\Workspace\WrathCombo\docs\guides\DMU"), 11);
        Console.WriteLine($"rules {rulesDir}  select {Opt("select", "AST_ST_DPS.json")}  set [{Opt("set", "")}]  step {Opt("step", "0.25")} slide {Opt("slide", "700")}  gcd {Opt("gcd", "measure")}  precards {Opt("precards", "auto")}  stillcasts {Opt("stillcasts", "1")}");
        var rows = new List<Row>();
        var tanks = new List<(string Pull, TankMetrics.Result R, List<AstTankMetrics.Episode> E)>();
        var fires = new Dictionary<string, int>();
        var idle = new Dictionary<string, int>();
        var accList = new List<string>();
        foreach (var log in Logs(logSpec))
        {
            var pulls = Select(index(log), spec, meId);
            if (pulls.Count == 0) continue;
            var worlds = pulls.Select(s => Load(s, meId, registry)).ToList();
            var gcd = PooledGcd(worlds, meId);
            long calRaw = 0, calPot = 0;
            foreach (var w0 in worlds) { var cal = Metrics.CalibrationSample(w0, HealTable); calRaw += cal.Raw; calPot += cal.Pot; }
            var hpPerPot = calPot >= 3000 ? (double)calRaw / calPot : 95.0;
            Console.WriteLine($"{Path.GetFileName(log)}: {pulls.Count} pulls, GCD {gcd} ms, {hpPerPot:0} HP per potency{(A.Contains("--cfhp") ? " (--cfhp: the rules see the tanks' counterfactual HP)" : "")}");
            for (var i = 0; i < pulls.Count; i++)
            {
                var x = Simulate(pulls[i], worlds[i], gcd, rulesDir, hpPerPot);
                var act = ActualOf(x, meId);
                var row = Measure(x, act, sheet, hpPerPot);
                row.Label = $"{Path.GetFileNameWithoutExtension(log)[^8..]}#{x.S.Ordinal}";
                rows.Add(row);
                foreach (var (k, v) in x.Sim.RuleFires) fires[k] = fires.GetValueOrDefault(k) + v;
                foreach (var g in x.Sim.Idle.GroupBy(i2 => $"{i2.Reason} [{Short(i2.Rule)}]")) idle[g.Key] = idle.GetValueOrDefault(g.Key) + g.Sum(i2 => i2.Ms);
                accList.AddRange(x.Sim.AccretionHeals.Where(h => h.SinceOtherOff < 3.2).Select(h => $"{row.Label} {SageSim.F(h.T)} {h.Src} -> {h.Target.Name} ({h.SinceOtherOff:0.0}s after another Accretion came off)"));
                if (pulls.Count == 1 && Logs(logSpec).Count == 1 || A.Contains("--detail")) PrintPull(x, act, row, hpPerPot);
                else PrintShort(x, row);
                if (A.Contains("--tanks"))
                {
                    var (tr, ep) = AstTankMetrics.Run(x.W, x.Sim, hpPerPot);
                    tanks.Add((row.Label, tr, ep));
                }
            }
        }
        PrintTotals(rows, fires, idle, accList);
        if (A.Contains("--tanks")) PrintTanks(tanks);
        return 0;
    }

    private static Row Measure(Result x, Actual act, List<Metrics.SheetRow> sheet, double hpPerPot)
    {
        var (w, sim) = (x.W, x.Sim);
        var me = w.Me;
        var row = new Row
        {
            Avail = sim.AvailMs, SimPot = Eff(sim.Damage), RealPot = Eff(act.Damage), SimGcd = sim.Gcds, RealGcd = act.Gcds, SimCanc = sim.Cancels, RealCanc = act.Cancels,
            MinMp = sim.MinMp, SimDot = sim.DotUpMs, RealDot = act.DotUpMs, Unmodelled = sim.Unmodelled, CastsLost = sim.CastsLostTarget,
        };
        // single-boss phases: P3 (Chaos and Exdeath) and P4 score the rules' AoE on two bosses (see the fflogs memory note)
        bool One(int t) => w.R.PhaseAt(t) is { } ph && (ph.StartsWith("P1") || ph.StartsWith("P2") || ph.StartsWith("P5"));
        foreach (var (ph, pm) in sim.PhaseMs) if (ph is not null && (ph.StartsWith("P1") || ph.StartsWith("P2") || ph.StartsWith("P5"))) row.OneAvail += pm.Avail;
        row.OneSim = sim.Damage.Where(d => One(d.T)).Sum(d => d.Potency * d.Mult);
        row.OneReal = act.Damage.Where(d => One(d.T)).Sum(d => d.Potency * d.Mult);
        foreach (var n in Counted) { row.SimCounts[n] = sim.Log.Count(e => e.What == n && e.T >= 0); row.RealCounts[n] = act.Counts.GetValueOrDefault(n); }
        var pd = new PartyDamage(w);
        // Divination: the rules' presses (party within 30 y, 20 s from landing) and the logged player's statuses on others
        foreach (var p in Presses(sim, "Divination").Where(t => t >= -3000))
        {
            row.SimDivs++;
            foreach (var m in w.Party.Where(m => m.Id != me.Id && !w.Dead(m, p) && System.Numerics.Vector2.Distance(w.Pos(m, p), w.Pos(me, p)) <= 30))
                row.SimContrib += 0.06 * pd.In(m.Id, p + Metrics.LandMs, p + Metrics.LandMs + 20000);
            row.SimDivOverlap += Overlap(w, p + Metrics.LandMs, 20000);
        }
        foreach (var s in act.Mine.Where(s => s.StatusId == AS.Divination && s.Target.Id != me.Id)) row.RealContrib += 0.06 * pd.In(s.Target.Id, s.StartMs, s.EndMs);
        foreach (var d in act.Div) { row.RealDivs++; row.RealDivOverlap += Overlap(w, d.S, d.E - d.S); }
        // the Balance and the Spear
        foreach (var c in sim.CardPlays.Where(c => c.Card is "the Balance" or "the Spear" && c.Target is not null))
        {
            row.SimCards++; if (c.InDiv) row.SimCardsIn++; if (c.Right) row.SimCardsRight++;
            if (c.Target.Id != me.Id) row.SimContrib += (c.Right ? 0.06 : 0.03) * pd.In(c.Target.Id, c.T + Metrics.LandMs, c.T + Metrics.LandMs + 15000);
        }
        foreach (var s in act.Mine.Where(s => s.StatusId is AS.Balance or AS.Spear))
        {
            row.RealCards++;
            var right = RightRole(s.StatusId == AS.Balance ? "the Balance" : "the Spear", s.Target);
            if (right) row.RealCardsRight++;
            if (act.Div.Any(d => d.S - 200 <= s.StartMs && s.StartMs < d.E)) row.RealCardsIn++;
            if (s.Target.Id != me.Id) row.RealContrib += (right ? 0.06 : 0.03) * pd.In(s.Target.Id, s.StartMs, s.EndMs);
        }
        foreach (var b in sim.BurstPresses.Where(b => b.T >= 0))
        {
            if (b.What == "Lord of Crowns") { row.SimLord++; if (b.InDiv) row.SimLordIn++; }
            if (b.What == "Oracle") { row.SimOracle++; if (b.InDiv) row.SimOracleIn++; }
        }
        foreach (var e in act.Events.Where(e => e.T >= 0 && e.Name is "Lord of Crowns" or "Oracle"))
        {
            var inDiv = act.Div.Any(d => d.S <= e.T && e.T < d.E);
            if (e.Name == "Lord of Crowns") { row.RealLord++; if (inDiv) row.RealLordIn++; }
            else { row.RealOracle++; if (inDiv) row.RealOracleIn++; }
        }
        row.SimStars = sim.StarLog.Count(s => s.Placed >= -20000); row.SimStarsManual = sim.StarLog.Count(s => s.Manual);
        row.RealStars = act.Counts.GetValueOrDefault("Earthly Star");
        row.SimLs = row.SimCounts["Lightspeed"]; row.RealLs = act.Counts.GetValueOrDefault("Lightspeed");
        row.SimLost = sim.CardsLost; row.RealDraws = act.Draws; row.RealPlays = act.CardPlays;
        row.Slots = Slots(x, act, sheet);
        var segs = Metrics.Healing(w, sim.Heals, hpPerPot);
        row.HealRules = segs.Sum(g => g.Rules); row.HealReal = segs.Sum(g => g.Real);
        row.CfMin = segs.Select(g => g.CfMin).DefaultIfEmpty(100).Min(); row.LogMin = segs.Select(g => g.LogMin).DefaultIfEmpty(100).Min();
        row.AccHeals = sim.AccretionHeals.Count; row.AccDanger = sim.AccretionHeals.Count(h => h.SinceOtherOff < 3.2);
        foreach (var h in Metrics.RealHealsNamed(w).Where(h => w.OthersHave(h.Target, AS.Accretion, h.T)))
        {
            var e = w.LastLost(w.Party, AS.Accretion, h.T);
            if (e >= 0 && h.T - e < 3200) row.RealAccDanger++;
        }
        return row;
    }

    /// <summary> Seconds of [from, from + ms) with another player's raid buff on you (World's, i.e. not this Astrologian's). </summary>
    private static double Overlap(World w, int from, int ms)
    {
        var n = 0;
        for (var t = from; t < from + ms && t < w.R.EndMs; t += 250) if (w.RaidBuffs(SamSim.TargetAt(w, t), t).Count > 0) n++;
        return n * 0.25;
    }

    private static double Eff(List<(int T, string Src, double Potency, double Mult)> d) => d.Sum(x => x.Potency * x.Mult);
    private static string F(int ms) => SageSim.F(ms);
    public static string Short(string rule) => rule.Replace("DMU mitigation plan (Astrologian): ", "plan: ").Replace("Astrologian single target: ", "");
    private static string Pct(int a, int n) => n == 0 ? "-" : $"{100.0 * a / n:0}%";

    private static void PrintShort(Result x, Row r)
    {
        var mins = Math.Max(0.01, r.Avail / 60000.0);
        Console.WriteLine($"{r.Label,-14} {F(x.EndMs),8} {x.W.R.PhaseAt(x.EndMs - 1),-20} up {r.Avail / 1000,4:0}s  pot/min {r.SimPot / mins,5:0} vs {r.RealPot / mins,5:0}  " +
                          $"GCD/min {r.SimGcd / mins,4:0.0} vs {r.RealGcd / mins,4:0.0}  canc {r.SimCanc,2} vs {r.RealCanc,2}  Combust {100 * r.SimDot / Math.Max(1, r.Avail),3:0}% vs {100 * r.RealDot / Math.Max(1, r.Avail),3:0}%  " +
                          $"party+ {r.SimContrib / 1000,5:0}k vs {r.RealContrib / 1000,5:0}k  cards in Div {r.SimCardsIn}/{r.SimCards} vs {r.RealCardsIn}/{r.RealCards}  star {r.SimStars} vs {r.RealStars}  " +
                          $"MP low {r.MinMp,5}  slots {r.Slots.Count(s => s.Sim)}/{r.Slots.Count} vs {r.Slots.Count(s => s.Real)}  cfLow {r.CfMin,3:0}%  acc {r.AccDanger}/{r.AccHeals}{(x.PreCards ? "" : "  (no cards at the pull)")}");
    }

    private static void PrintPull(Result x, Actual act, Row r, double hpPerPot)
    {
        var (s, w, sim) = (x.S, x.W, x.Sim);
        var mins = Math.Max(0.01, sim.AvailMs / 60000.0);
        var isAst = s.Party.Any(m => m.Id == w.Me.Id && m.Job == AstJob);
        Console.WriteLine($"\n== pull {r.Label} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm} {F(x.EndMs)} {s.Outcome}, furthest {w.R.PhaseAt(x.EndMs - 1)}; target up, alive and able to act {sim.AvailMs / 1000.0:0}s; dead {sim.DeadMs / 1000.0:0}s, incapacitated {sim.IncapMs / 1000.0:0}s");
        Console.WriteLine($"   GCD {sim.GcdMs} ms, Fall Malefic cast {sim.CastTime(AstSim.Defs[AA.FallMalefic])} ms; {(isAst ? "the player" : "stand-in")} {w.Me.Name} (job {w.Me.Job}); cards at the pull: {(x.PreCards ? "Astral (Balance, Arrow, Spire, Lord)" : "none")}");
        Console.WriteLine($"   files: {string.Join(", ", x.Files)}");
        var colB = isAst ? "logged" : "stand-in";
        Console.WriteLine($"   {"",-46}{"rules",10}{colB,10}");
        void Rw(string n, double a, double? b, string fmt = "0") => Console.WriteLine($"   {n,-46}{a.ToString(fmt),10}{(b is { } bb ? bb.ToString(fmt) : ""),10}");
        Rw("potency per minute (own damage)", r.SimPot / mins, r.RealPot / mins);
        Rw("party damage added by Divination + cards (k)", r.SimContrib / 1000, r.RealContrib / 1000);
        Rw("GCDs per minute", r.SimGcd / mins, r.RealGcd / mins, "0.00");
        Rw("GCD uptime while a target was up (%)", 100.0 * sim.BusyMs / Math.Max(1, sim.AvailMs), null, "0.0");
        Rw("Combust III uptime (%)", 100 * r.SimDot / Math.Max(1, r.Avail), 100 * r.RealDot / Math.Max(1, r.Avail), "0.0");
        Rw("cast cancels", r.SimCanc, r.RealCanc);
        Rw("casts lost (target gone)", r.CastsLost, null);
        Rw("Divinations", r.SimDivs, r.RealDivs);
        Rw("Divination: s with other raid buffs (avg)", r.SimDivOverlap / Math.Max(1, r.SimDivs), r.RealDivOverlap / Math.Max(1, r.RealDivs), "0.0");
        Rw("Balance/Spear played", r.SimCards, r.RealCards);
        Rw("  inside Divination", r.SimCardsIn, r.RealCardsIn);
        Rw("  on the right role", r.SimCardsRight, r.RealCardsRight);
        Rw("Lord of Crowns in Divination / all", r.SimLordIn, r.RealLordIn); Rw("  all", r.SimLord, r.RealLord);
        Rw("Oracle in Divination / all", r.SimOracleIn, r.RealOracleIn); Rw("  all", r.SimOracle, r.RealOracle);
        Rw("cards lost to a draw (rules) / drawn-played (logged)", r.SimLost, r.RealDraws * 4 - r.RealPlays);
        Rw("Earthly Stars (rules: manual detonations next row)", r.SimStars, r.RealStars); Rw("  detonated by hand", r.SimStarsManual, act.Counts.GetValueOrDefault("Stellar Detonation"));
        Rw("Lightspeed", r.SimLs, r.RealLs);
        Rw("MP low", r.MinMp, null);
        Rw("GCD clipped by weaves (s)", sim.ClipMs / 1000.0, null, "0.0");
        foreach (var n in Counted) Rw(n, r.SimCounts[n], r.RealCounts[n]);
        Console.WriteLine("   logged damage per potency (the potency table's check; should agree across sources): " +
                          string.Join(", ", act.Check.Where(kv => kv.Value.Pot > 0).Select(kv => $"{kv.Key} {kv.Value.Dmg / kv.Value.Pot:0.0} ({kv.Value.Pot:0})")));
        // bursts
        foreach (var p in Presses(sim, "Divination"))
            Console.WriteLine($"   Divination {F(p)}: other raid buffs {w.RaidBuffs(SamSim.TargetAt(w, p + 3000), p + 3000).Count} at +3 s; " +
                              string.Join(", ", sim.Log.Where(e => e.T >= p - 8000 && e.T <= p + 20000 && e.What is "Lightspeed" or "the Balance" or "the Spear" or "Lord of Crowns" or "Oracle" or "Astral Draw" or "Umbral Draw" or "Combust III" or "Earthly Star")
                                  .Select(e => $"{(e.T - p) / 1000.0:+0.0;-0.0} {e.What}{(e.Target.Length > 0 ? "->" + e.Target.Split(' ')[0] : "")}")));
        if (sim.LostLog.Count > 0) Console.WriteLine("   cards lost: " + string.Join("; ", sim.LostLog.Select(l => $"{F(l.T)} {l.Lost}")));
        Console.WriteLine("   Earthly Star: " + string.Join("; ", sim.StarLog.Select(st => $"{F(st.Placed)}->{F(st.Ended)}{(st.Manual ? " by hand" : "")}{(st.Giant ? "" : " (not charged)")}")));
        // sheet
        if (r.Slots.Count > 0)
        {
            Console.WriteLine($"   sheet slots (Astrologian column): rules {r.Slots.Count(v => v.Sim)}/{r.Slots.Count}, logged {r.Slots.Count(v => v.Real)}/{r.Slots.Count}  (" +
                              string.Join(", ", r.Slots.GroupBy(v => v.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count(v => v.Sim)}/{g.Count(v => v.Real)}/{g.Count()}")) + ")");
            if (A.Contains("--slots") || A.Contains("--slotlist") || A.Contains("--detail"))
                foreach (var v in r.Slots)
                    Console.WriteLine($"     {F(v.T),8} {v.Phase} {v.Mech,-36} {(v.Carry ? "> " : "")}{v.Label,-24} rules {(v.Sim ? "up" : "--")}  logged {(v.Real ? "up" : "--")}{(v.Sim ? "" : "   " + SlotWhy(sim, v))}");
        }
        // healing and Accretions
        Console.WriteLine($"   healing ({hpPerPot:0} HP/potency): lowest counterfactual HP {r.CfMin:0}% (logged {r.LogMin:0}%); rules {r.HealRules / 1e6:0.00}M vs logged {r.HealReal / 1e6:0.00}M");
        if (A.Contains("--heal")) Console.WriteLine("   heal sources: " + Metrics.Breakdown(w, sim.Heals, hpPerPot));
        if (sim.AccretionHeals.Count > 0 || r.RealAccDanger > 0)
        {
            Console.WriteLine($"   Accretion: rules' heals on a player with one {r.AccHeals}, inside 3.2 s of another coming off {r.AccDanger} (logged player {r.RealAccDanger})");
            foreach (var h in sim.AccretionHeals) Console.WriteLine($"      {F(h.T),8} {h.Src} -> {h.Target.Name}  {(h.SinceOtherOff >= 999 ? "no other off yet" : $"{h.SinceOtherOff:0.0}s after another came off")}{(h.SinceOtherOff < 3.2 ? "  DANGER" : "")}");
        }
        // phases
        Console.WriteLine("   by phase:          up(s)  GCD up%  Combust%  GCDs cancels  pot/min  logged  idle(s) top reasons");
        foreach (var (phase, p) in sim.PhaseMs.OrderBy(k => w.R.Phases.FindIndex(ph => ph.Name == k.Key)))
        {
            bool In(int t) => w.R.PhaseAt(t) == phase;
            var pot = sim.Damage.Where(d => In(d.T)).Sum(d => d.Potency * d.Mult);
            var real = act.Damage.Where(d => In(d.T)).Sum(d => d.Potency * d.Mult);
            var idl = sim.Idle.Where(i => i.Phase == phase).GroupBy(i => i.Reason).Select(g => (g.Key, Ms: g.Sum(i => i.Ms))).OrderByDescending(g => g.Ms).ToList();
            Console.WriteLine($"   {phase,-18} {p.Avail / 1000.0,6:0} {100.0 * p.Busy / Math.Max(1, p.Avail),7:0.0} {100.0 * p.Dot / Math.Max(1, p.Avail),8:0} {sim.Log.Count(e => In(e.T) && AstSim.GcdNames.Contains(e.What)),5} {sim.CancelLog.Count(c => In(c.T)),7} " +
                              $"{pot / Math.Max(0.01, p.Avail / 60000.0),8:0} {real / Math.Max(0.01, p.Avail / 60000.0),7:0} {idl.Sum(i => i.Ms) / 1000.0,7:0.0} " + string.Join("; ", idl.Take(3).Select(i => $"{i.Key} {i.Ms / 1000.0:0.0}")));
        }
        Console.WriteLine("   GCD idle while a target was up, by reason and rule (s):");
        foreach (var g in sim.Idle.GroupBy(i => (i.Reason, i.Rule)).Select(g => (g.Key, Ms: g.Sum(i => i.Ms), N: g.Count())).OrderByDescending(g => g.Ms).Take(12))
            Console.WriteLine($"      {g.Ms / 1000.0,6:0.0}s  x{g.N,-3} {g.Key.Reason}  [{Short(g.Key.Rule)}]");
        if (sim.CancelLog.Count > 0) Console.WriteLine("   cancels by rule: " + string.Join(", ", sim.CancelLog.GroupBy(c => c.Rule).Select(g => $"{Short(g.Key)} {g.Count()}")));
        Console.WriteLine("   rule fires: " + string.Join("; ", sim.RuleFires.OrderByDescending(k => k.Value).Select(k => $"{Short(k.Key)} {k.Value}")));
        if (sim.Anomalies.Count > 0) Console.WriteLine("   anomalies: " + string.Join("; ", sim.Anomalies.Take(10)));
        if (sim.Unmodelled > 0) Console.WriteLine($"   ! unmodelled actions returned {sim.Unmodelled} times");
        if (A.Contains("--detail"))
        {
            Console.WriteLine("   oGCD presses:");
            foreach (var o in sim.Ogcds) Console.WriteLine($"      {F(o.T),8} {o.Name,-24} GCD left {o.GcdLeft / 1000.0:0.00}s  [{Short(o.Rule)}]");
            Console.WriteLine("   cancels:");
            foreach (var c in sim.CancelLog) Console.WriteLine($"      {F(c.T),8} {c.Name} moved {c.MovedInMs / 1000.0:0.00}s in [{Short(c.Rule)}]");
        }
    }

    private static void PrintTotals(List<Row> rows, Dictionary<string, int> fires, Dictionary<string, int> idle, List<string> acc)
    {
        if (rows.Count == 0) { Console.WriteLine("no pulls"); return; }
        double mins = rows.Sum(r => r.Avail) / 60000.0;
        var slots = rows.SelectMany(r => r.Slots).ToList();
        Console.WriteLine($"\n===== {rows.Count} pulls, {mins:0.0} minutes with a target up and the player alive");
        void Rw(string n, double a, double b, string fmt = "0") => Console.WriteLine($"   {n,-50}{a.ToString(fmt),10}{b.ToString(fmt),10}");
        Console.WriteLine($"   {"",-50}{"rules",10}{"logged",10}");
        Rw("potency per minute (own damage)", rows.Sum(r => r.SimPot) / mins, rows.Sum(r => r.RealPot) / mins);
        var oneMins = Math.Max(0.01, rows.Sum(r => r.OneAvail) / 60000.0);
        Rw($"  single-boss phases P1/P2/P5 ({oneMins:0.0} min)", rows.Sum(r => r.OneSim) / oneMins, rows.Sum(r => r.OneReal) / oneMins);
        Rw("party damage added by Divination + cards (M)", rows.Sum(r => r.SimContrib) / 1e6, rows.Sum(r => r.RealContrib) / 1e6, "0.00");
        Rw("GCDs per minute", rows.Sum(r => r.SimGcd) / mins, rows.Sum(r => r.RealGcd) / mins, "0.00");
        Rw("Combust III uptime (%)", 100 * rows.Sum(r => r.SimDot) / rows.Sum(r => r.Avail), 100 * rows.Sum(r => r.RealDot) / rows.Sum(r => r.Avail), "0.0");
        Rw("cast cancels", rows.Sum(r => r.SimCanc), rows.Sum(r => r.RealCanc));
        Rw("Divinations", rows.Sum(r => r.SimDivs), rows.Sum(r => r.RealDivs));
        Rw("Divination: s with other raid buffs (avg)", rows.Sum(r => r.SimDivOverlap) / Math.Max(1, rows.Sum(r => r.SimDivs)), rows.Sum(r => r.RealDivOverlap) / Math.Max(1, rows.Sum(r => r.RealDivs)), "0.0");
        Rw("Balance/Spear played", rows.Sum(r => r.SimCards), rows.Sum(r => r.RealCards));
        Rw("  inside Divination", rows.Sum(r => r.SimCardsIn), rows.Sum(r => r.RealCardsIn));
        Rw("  on the right role", rows.Sum(r => r.SimCardsRight), rows.Sum(r => r.RealCardsRight));
        Rw("Lord of Crowns (in Divination)", rows.Sum(r => r.SimLordIn), rows.Sum(r => r.RealLordIn)); Rw("  all", rows.Sum(r => r.SimLord), rows.Sum(r => r.RealLord));
        Rw("Oracle (in Divination)", rows.Sum(r => r.SimOracleIn), rows.Sum(r => r.RealOracleIn)); Rw("  all", rows.Sum(r => r.SimOracle), rows.Sum(r => r.RealOracle));
        Rw("cards lost (rules) / drawn-not-played (logged)", rows.Sum(r => r.SimLost), rows.Sum(r => r.RealDraws * 4 - r.RealPlays));
        Rw("Earthly Stars", rows.Sum(r => r.SimStars), rows.Sum(r => r.RealStars));
        Rw("Lightspeed", rows.Sum(r => r.SimLs), rows.Sum(r => r.RealLs));
        foreach (var n in Counted) Rw($"{n} (count)", rows.Sum(r => r.SimCounts[n]), rows.Sum(r => r.RealCounts[n]));
        if (slots.Count > 0)
            Console.WriteLine($"   sheet slots: rules {slots.Count(x => x.Sim)}/{slots.Count}, logged {slots.Count(x => x.Real)}/{slots.Count}  (" +
                              string.Join(", ", slots.GroupBy(x => x.Label).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count(x => x.Sim)}/{g.Count(x => x.Real)}/{g.Count()}")) + ")");
        if (A.Contains("--slotlist"))
            foreach (var g in slots.GroupBy(s => (s.Phase, s.Mech, s.Label, s.Carry)).OrderBy(g => g.Min(s => s.SheetMs)))
                Console.WriteLine($"     slot {g.Key.Phase} {g.Key.Mech,-36} {(g.Key.Carry ? "> " : "")}{g.Key.Label,-24} rules {g.Count(s => s.Sim)}/{g.Count()}  logged {g.Count(s => s.Real)}/{g.Count()}");
        Console.WriteLine($"   healing: rules {rows.Sum(r => r.HealRules) / 1e6:0.00}M vs logged {rows.Sum(r => r.HealReal) / 1e6:0.00}M; lowest counterfactual HP {rows.Min(r => r.CfMin):0}% (logged {rows.Min(r => r.LogMin):0}%)");
        Console.WriteLine($"   Accretion: rules' heals on a holder {rows.Sum(r => r.AccHeals)}, inside 3.2 s of another coming off {rows.Sum(r => r.AccDanger)} (logged player {rows.Sum(r => r.RealAccDanger)})");
        foreach (var l in acc) Console.WriteLine($"      {l}");
        Console.WriteLine("   rules' GCD idle by reason (s): " + string.Join("; ", idle.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value / 1000.0:0}")));
        Console.WriteLine("   rule fires: " + string.Join("; ", fires.OrderByDescending(k => k.Value).Select(k => $"{Short(k.Key)} {k.Value}")));
        if (rows.Sum(r => r.Unmodelled) > 0) Console.WriteLine($"   ! unmodelled actions returned {rows.Sum(r => r.Unmodelled)} times");
        Console.WriteLine($"SUMMARY pulls={rows.Count} mins={mins:0.0} pot={rows.Sum(r => r.SimPot) / mins:0} real={rows.Sum(r => r.RealPot) / mins:0} pot1={rows.Sum(r => r.OneSim) / oneMins:0} real1={rows.Sum(r => r.OneReal) / oneMins:0} party={rows.Sum(r => r.SimContrib) / 1e6:0.00} realparty={rows.Sum(r => r.RealContrib) / 1e6:0.00} " +
                          $"gcd={rows.Sum(r => r.SimGcd) / mins:0.00} realgcd={rows.Sum(r => r.RealGcd) / mins:0.00} canc={rows.Sum(r => r.SimCanc)} mp={rows.Min(r => r.MinMp)} " +
                          $"combust={100 * rows.Sum(r => r.SimDot) / rows.Sum(r => r.Avail):0.0} cardsIn={rows.Sum(r => r.SimCardsIn)}/{rows.Sum(r => r.SimCards)} lost={rows.Sum(r => r.SimLost)} stars={rows.Sum(r => r.SimStars)} " +
                          $"slots={slots.Count(x => x.Sim)}/{slots.Count} realslots={slots.Count(x => x.Real)} cf={rows.Min(r => r.CfMin):0} heal={rows.Sum(r => r.HealRules) / 1e6:0.00} acc={rows.Sum(r => r.AccDanger)}/{rows.Sum(r => r.AccHeals)}");
    }

    private static void PrintTanks(List<(string Pull, TankMetrics.Result R, List<AstTankMetrics.Episode> E)> tanks)
    {
        Console.WriteLine("\n===== tank care (counterfactual tank HP, AstTankMetrics)");
        var tPh = new Dictionary<string, double[]>();
        foreach (var (pull, tr, eps) in tanks)
        {
            foreach (var d in tr.Deaths) Console.WriteLine($"   death {pull} {F(d.T)} {d.Phase} [{d.Segment}] {d.Tank}: {d.Cause}; rules {(d.Survives ? "SURVIVE" : "die")} ({d.MarginPct:+0;-0}%)");
            foreach (var d in tr.CfDeaths) Console.WriteLine($"   RULES LOSE {pull} {F(d.T)} {d.Phase} {d.Tank}: {d.Hit}");
            foreach (var (k, v) in tr.ByPhase.Where(kv => !kv.Key.Contains(' ')))
            {
                var a = tPh.TryGetValue(k, out var o) ? o : tPh[k] = [0, 0, 0, 0, 100, 100, 0, 0];
                a[0] += v.LogLow30; a[1] += v.CfLow30; a[2] += v.LogLow15; a[3] += v.CfLow15; a[4] = Math.Min(a[4], v.CfMin); a[5] = Math.Min(a[5], v.LogMin); a[6]++; a[7] += v.CfDead;
            }
            if (A.Contains("--episodes"))
                foreach (var e in eps)
                    Console.WriteLine($"   episode {pull} {e.Phase} {F(e.Start)}-{F(e.End)} min@{F(e.MinT)} [{e.Segment}] {e.Tank} cf {e.CfMin:0}% (logged min {e.LogMin:0}%){(e.CfDied ? " CF-DEAD" : "")} | ready {e.Ready} | GCDs {e.Gcds} | oGCDs {e.Ogcds} | hits {e.Hits}");
        }
        Console.WriteLine($"   tanks: logged deaths (not in a wipe) {tanks.Sum(t => t.R.Deaths.Count)}, the rules survive the killing blow in {tanks.Sum(t => t.R.Deaths.Count(d => d.Survives))}; tanks the rules would lose that lived {tanks.Sum(t => t.R.CfDeaths.Count)}");
        foreach (var (k, v) in tPh.OrderBy(v => v.Key))
            Console.WriteLine($"     tank-seconds {k,-10} under 30%: logged {v[0],6:0} rules {v[1],6:0}   under 15%: logged {v[2],6:0} rules {v[3],6:0}   lowest: logged {v[5],3:0}% rules {v[4],3:0}%   rules-dead {v[7],5:0}  (pulls {v[6]})");
    }

    // ---------------------------------------------------------------- pulls, calibrate, timeline

    private static int Pulls(string logSpec, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        foreach (var log in Logs(logSpec))
            foreach (var s in Select(index(log), spec, meId))
            {
                var w = new World(PullLoader.Load(s, null, registry), meId);
                var iv = GcdIntervals(w, meId); iv.Sort();
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(log)[^8..]}#{s.Ordinal,-4} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm:ss} {s.Outcome,-8} {(s.EndTicks - s.StartTicks) / 1e7,6:N1}s {s.FurthestPhase,-20} " +
                                  $"GCD {(iv.Count >= 5 ? iv[iv.Count / 2] : 0)} cards at the pull {(PreCards(w, meId) ? "yes" : "no")}");
                if (A.Contains("--opener"))
                    Console.WriteLine("       " + string.Join(" ", w.R.Actions.Where(a => a.Source.Id == meId && a.T < 15000 && a.T > -30000).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : a.T).Select(g => g.First())
                        .OrderBy(a => a.T).Select(a => $"{a.T / 1000.0:0.0}:{a.Name}")));
            }
        return 0;
    }

    private static int Calibrate(string logSpec, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var casts = new List<(World W, CastEvent C)>();
        foreach (var log in Logs(logSpec))
            foreach (var s in Select(index(log), spec, meId))
            {
                var w = new World(PullLoader.Load(s, null, registry), meId);
                foreach (var c in w.R.Casts.Where(c => c.Source.Id == meId && c.DurationMs >= 1000 && c.StartMs > 0)) casts.Add((w, c));
            }
        var done = casts.Count(x => x.C.Outcome != CastOutcome.Cancelled);
        var canc = casts.Count - done;
        Console.WriteLine($"{done} completed casts, {canc} cancelled; columns: false cancels of completed casts / real cancels seen");
        foreach (var step in new[] { 0.25, 0.5 })
        {
            var line = $"  burst 5 y/s, step {step} y";
            foreach (var slide in new[] { 500, 700, 900 })
            {
                int fp = 0, tp = 0;
                foreach (var (w, c) in casts)
                {
                    w.RunSpeed = 5; w.MinStep = step;
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

    private static int Timeline(string logSpec, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", RepoRules);
        var log = Logs(logSpec)[0];
        var s = Select(index(log), spec, meId).First();
        double from = double.Parse(Opt("from", "-16")), to = double.Parse(Opt("to", "9999"));
        var w0 = Load(s, meId, registry);
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
        foreach (var rw in x.W.Raidwides) ev.Add((rw.T, 1, $"      HIT {rw.Name} #{rw.Ordinal} ({rw.AvgPct:0}% HP) rules: {(SimCu(x.Sim, rw.T) ? "CU " : "")}{(SimSun(x.Sim, rw.T) ? "Sun " : "")}{(SimNs(x.Sim, rw.T) ? "NS " : "")}{(SimMacro(x.Sim, rw.T) ? "Macro" : "")}"));
        foreach (var d in x.W.R.Deaths) ev.Add((d.T, 1, $"      DEATH {d.Victim.Name}"));
        foreach (var p in x.W.R.Phases.Where(p => !p.IsSegment)) ev.Add((p.StartMs, 0, $"=== {p.Name}"));
        foreach (var wv in SamCommands.RaidWindows(x.W, x.EndMs)) { ev.Add((wv.Start, 0, $"      [others' raid window starts, up to {wv.Max} buffs]")); ev.Add((wv.End, 0, "      [others' raid window ends]")); }
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
