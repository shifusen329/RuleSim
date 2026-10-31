using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The Samurai replay's commands (2026-10-21). Program.cs hands over when the pulls spec is "sam" (the --me
///     player's Samurai pulls) or --job=sam is given (any pulls, with --me as the stand-in):
///
///   run      &lt;log&gt; sam|dmu|N,M-K [--job=sam] [--me=HEX] [--hits=HEX] [--rules=DIR] [--select=FILE|none] [--set=a=b,...]
///            [--gcd=ms] [--step=0.25 --slide=700] [--dot=lost|keep] [--detail]
///   timeline &lt;log&gt; &lt;pull&gt; ... [--from=s --to=s]
///   samgcd   &lt;log&gt; [&lt;log&gt; ...] [--me=HEX]   the player's GCD with Fuka up, per night
/// </summary>
internal static class SamCommands
{
    public const byte SamJob = 34;

    public static bool Handles(string[] args) =>
        args.Length > 0 && (args[0] == "samgcd" || args[0] is "run" or "timeline" && (args.Length > 2 && args[2] == "sam" || args.Contains("--job=sam")));

    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;

    public static readonly HashSet<string> GcdNames = ["Gyofu", "Hakaze", "Jinpu", "Shifu", "Yukikaze", "Gekko", "Kasha", "Enpi", "Higanbana",
        "Tenka Goken", "Midare Setsugekka", "Tendo Goken", "Tendo Setsugekka", "Kaeshi: Goken", "Tendo Kaeshi Goken", "Kaeshi: Setsugekka",
        "Tendo Kaeshi Setsugekka", "Ogi Namikiri", "Kaeshi: Namikiri", "Fuko", "Mangetsu", "Oka"];

    /// <summary> The Reaper stand-in's weaponskills and spells, for its own GCD rate (context only). </summary>
    private static readonly HashSet<string> StandInGcds = ["Slice", "Waxing Slice", "Infernal Slice", "Shadow of Death", "Soul Slice", "Gibbet", "Gallows",
        "Executioner's Gibbet", "Executioner's Gallows", "Void Reaping", "Cross Reaping", "Plentiful Harvest", "Communio", "Harpe", "Harvest Moon",
        "Perfectio", "Spinning Scythe", "Nightmare Scythe", "Whorl of Death", "Soul Scythe", "Guillotine", "Executioner's Guillotine", "Grim Reaping"];

    private static readonly HashSet<string> AutoCritNames = ["Midare Setsugekka", "Tendo Setsugekka", "Kaeshi: Setsugekka", "Tendo Kaeshi Setsugekka", "Ogi Namikiri", "Kaeshi: Namikiri"];

    public static int Main(string[] args, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        A = args;
        var meId = Convert.ToUInt32(Opt("me", "1001A4A8"), 16);
        switch (args[0])
        {
            case "samgcd":
            {
                foreach (var log in args.Skip(1).Where(a => !a.StartsWith("--")))
                {
                    var all = new List<int>();
                    foreach (var s in Select(index(log), "sam", meId))
                        all.AddRange(MeasureGcd(new World(PullLoader.Load(s, null, registry), meId), meId).Intervals);
                    all.Sort();
                    Console.WriteLine($"{Path.GetFileName(log)}: {all.Count} intervals between GCDs with Fuka up (1.9-2.6 s): median {(all.Count > 0 ? all[all.Count / 2] : 0)} ms, " +
                                      $"quartiles {(all.Count > 3 ? all[all.Count / 4] : 0)}-{(all.Count > 3 ? all[3 * all.Count / 4] : 0)} ms");
                }
                return 0;
            }
            case "run" when args.Contains("--gaps"): return Gaps(args[1], args[2], meId, registry, index);
            case "run" when args.Contains("--transition"): return Transition(args[1].Split(';').ToList(), args[2], meId, registry, index);
            case "run" when args.Contains("--meleecheck"):
            {
                // The logged player's melee weaponskills (3 y in game): their distance to the target's hitbox edge in the replay.
                HashSet<string> melee = ["Slice", "Waxing Slice", "Infernal Slice", "Gibbet", "Gallows", "Executioner's Gibbet", "Executioner's Gallows",
                    "Void Reaping", "Cross Reaping", "Shadow of Death", "Soul Slice", "Gyofu", "Jinpu", "Shifu", "Yukikaze", "Gekko", "Kasha"];
                foreach (var s in Select(index(args[1]), args[2], meId))
                {
                    var w = new World(PullLoader.Load(s, null, registry), meId);
                    var byPhase = new Dictionary<string, List<double>>();
                    foreach (var e in w.R.Actions.Where(e => e.Source.Id == meId && melee.Contains(e.Name) && e.T >= 0))
                    {
                        var tgt = e.PrimaryTarget ?? e.Hits.FirstOrDefault()?.Target;
                        if (tgt is null || tgt.IsPlayer) continue;
                        var d = Math.Max(0, System.Numerics.Vector2.Distance(e.SourcePos, e.Hits.FirstOrDefault()?.TargetPos ?? w.Pos(tgt, e.T)) - World.Radius(w.Me) - World.Radius(tgt));
                        var dTrack = A.Contains("--raw") ? w.Dist(w.Me, tgt, e.T - 450) : SamSim.DistFor(w, w.Me, tgt, e.T - 450);
                        (byPhase.TryGetValue(w.R.PhaseAt(e.T), out var l) ? l : byPhase[w.R.PhaseAt(e.T)] = []).Add(dTrack);
                        if (args.Contains("--detail") && dTrack > 3) Console.WriteLine($"   {SageSim.F(e.T)} {e.Name} on {tgt.Name} (radius {tgt.Radius:0.0}): track {dTrack:0.0} y, line {d:0.0} y");
                    }
                    foreach (var (ph, l) in byPhase)
                        Console.WriteLine($"#{s.Ordinal} {ph,-20} {l.Count,4} melee GCDs: track distance > 3 y for {l.Count(v => v > 3),3}, > 5 y for {l.Count(v => v > 5),3}; median {l.OrderBy(v => v).ElementAt(l.Count / 2):0.0} y");
                }
                return 0;
            }
            case "run": return Run(args[1], args[2], meId, registry, index);
            case "timeline": return Timeline(args[1], args[2], meId, registry, index);
        }
        return 1;
    }

    // ---------------------------------------------------------------- pulls and files

    private static List<PullSummary> Select(FileIndex idx, string spec, uint meId)
    {
        var all = idx.Pulls.Where(p => p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond).ToList();
        if (spec == "sam") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId && m.Job == SamJob)).ToList();
        if (spec == "dmu") return all.Where(p => p.ZoneId == 1363 && p.Party.Any(m => m.Id == meId)).ToList();
        var want = new HashSet<int>();
        foreach (var part in spec.Split(','))
            if (part.Contains('-')) { var x = part.Split('-'); for (var i = int.Parse(x[0]); i <= int.Parse(x[1]); i++) want.Add(i); }
            else want.Add(int.Parse(part));
        return all.Where(p => want.Contains(p.Ordinal)).ToList();
    }

    private static string SymbolsPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "sam_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "sam_symbols.json");
    }

    /// <summary>
    ///     RuleEngine.FilesFor for the Samurai's single-target mode (buttons Hakaze, Gyofu): the job's layers for the
    ///     button in name order, then the selected file (--select, default SAM_Lv100_SlowMAL.json as in the user's
    ///     CustomRuleSelections), or with --select=none every other file for the button. "enabled" is honoured at
    ///     evaluation, as EvaluateForPreset does.
    /// </summary>
    public static List<CompiledRuleSet> Compile(SamSim sim, string rulesDir, List<string> describe)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var buttons = new HashSet<uint> { SA.Hakaze, SA.Gyofu };
        var selected = Opt("select", "SAM_Lv100_SlowMAL.json");
        var layers = new List<CompiledRuleSet>();
        var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            RuleSetFile? file;
            try { file = JsonConvert.DeserializeObject<RuleSetFile>(File.ReadAllText(path)); }
            catch (JsonException e) { describe.Add($"{Path.GetFileName(path)}: JSON error {e.Message}"); continue; }
            if (file is null || !string.Equals(file.Job, "SAM", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in Opt("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var set = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, SA.Hakaze);
            if (set is null)
            {
                // Like the engine: a broken file is reported and skipped; fatal here only when the mode would run it.
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
        }
        if (selected != "none" && others.Count == 0) describe.Add($"WARNING: the selected file {selected} isn't in {rulesDir}");
        return layers.Concat(others).ToList();
    }

    /// <summary> Median interval between the player's GCDs (start to start) while Fuka is on them, 1.9-2.6 s. </summary>
    public static (int Median, List<int> Intervals) MeasureGcd(World w, uint meId)
    {
        var fuka = w.R.Statuses.Where(s => s.Target.Id == meId && s.Name == "Fuka").ToList();
        var starts = new List<int>();
        var mine = w.R.Actions.Where(a => a.Source.Id == meId && GcdNames.Contains(a.Name)).GroupBy(a => a.Sequence != 0 ? (long)a.Sequence : a.T).Select(g => g.First());
        foreach (var a in mine) starts.Add(a.Cast?.StartMs ?? a.T);
        starts.Sort();
        var iv = new List<int>();
        for (var i = 1; i < starts.Count; i++)
        {
            var d = starts[i] - starts[i - 1];
            if (d is >= 1900 and <= 2600 && fuka.Any(s => s.Active(starts[i - 1]))) iv.Add(d);
        }
        iv.Sort();
        return (iv.Count >= 5 ? iv[iv.Count / 2] : 0, iv);
    }

    // ---------------------------------------------------------------- simulate

    public sealed record Result(PullSummary S, World W, SamSim Sim, List<string> Files, int EndMs);

    private static Result Simulate(PullSummary s, uint meId, EncounterRegistry registry, string rulesDir)
    {
        var r = PullLoader.Load(s, null, registry);
        var w = new World(r, meId) { MinStep = double.Parse(Opt("step", "0.25")), RunSpeed = double.Parse(Opt("run", "5")) };
        Actor? hitsActor = Opt("hits", "") is { Length: > 0 } h ? r.Actors.FirstOrDefault(a => a.Id == Convert.ToUInt32(h, 16)) : null;
        var gcd = Opt("gcd", "2140") == "measure" ? 0 : int.Parse(Opt("gcd", "2140"));
        if (gcd == 0) gcd = MeasureGcd(w, meId).Median is var m and > 0 ? m : 2140;
        var sim = new SamSim(w, gcd, hitsActor)
        {
            T = -16000, SlideMs = int.Parse(Opt("slide", "700")), DotLostOnSwitch = Opt("dot", "lost") != "keep", ForesightKenki = Opt("foresightKenki", "1") != "0",
            Trace = A.Contains("--trace"), TraceFrom = (int)(double.Parse(Opt("from", "-16")) * 1000), TraceTo = (int)(double.Parse(Opt("to", "9999")) * 1000),
        };
        var describe = new List<string>();
        var files = Compile(sim, rulesDir, describe);
        while (sim.T < r.EndMs)
        {
            sim.Advance(20);
            sim.Press(files);
        }
        return new Result(s, w, sim, describe, r.EndMs);
    }

    // ---------------------------------------------------------------- scoring

    /// <summary> Higanbana ticks: 50 potency every 3 s from landing, while the target is present and targetable, until it ends. </summary>
    public static List<(int T, double Pot, SamSim.DotApp App)> DotTicks(Result x, bool keepOnSwitch = false)
    {
        var res = new List<(int, double, SamSim.DotApp)>();
        foreach (var d in x.Sim.Dots)
        {
            var end = Math.Min(d.Expire, x.EndMs);
            if (!(keepOnSwitch && d.Why == "target changed")) end = Math.Min(end, d.EndedAt);
            else
            {
                var next = x.Sim.Dots.FirstOrDefault(o => o.Target.Id == d.Target.Id && o.Land > d.Land);
                if (next is not null) end = Math.Min(end, next.Land);
            }
            for (var t = d.Land + 3000; t <= end; t += 3000)
                if (d.Target.IsPresent(t) && d.Target.IsTargetable(t) && t >= 0) res.Add((t, 50 * d.Mult * d.Crit, d));
        }
        return res;
    }

    public static double Effective(Result x) => x.Sim.Damage.Sum(d => d.Potency * d.Mult * d.Crit) + DotTicks(x).Sum(t => t.Pot);
    public static double Raw(Result x) => x.Sim.Damage.Sum(d => d.Potency * d.Mult) + DotTicks(x).Sum(t => t.Pot / t.App.Crit);

    /// <summary> Raid windows: spans where World.RaidBuffs counts a party buff on the player or a debuff on the target. </summary>
    public static List<(int Start, int End, int Max)> RaidWindows(World w, int endMs)
    {
        var res = new List<(int, int, int)>();
        int? from = null; var max = 0;
        for (var t = 0; t < endMs; t += 100)
        {
            var n = w.RaidBuffs(SamSim.TargetAt(w, t), t).Count;
            if (n > 0 && from is null) { from = t; max = 0; }
            if (n > 0) max = Math.Max(max, n);
            if (n == 0 && from is { } f) { if (t - f >= 3000) res.Add((f, t, max)); from = null; }
        }
        if (from is { } ff) res.Add((ff, endMs, max));
        return res;
    }

    /// <summary> The logged player's own play, scored with the same table (combo steps assumed in combo, positionals from geometry and their True North). </summary>
    public sealed class Actual
    {
        public readonly Dictionary<string, int> Counts = new();
        public int Gcds, Cancels;
        public double Effective, BanaUpMs, BusyMs;
        public int PosHit, PosMiss, PosTn;
        public readonly List<(int T, string Name, string Target)> Events = new();
    }

    public static Actual ActualOf(Result x, uint meId)
    {
        var w = x.W;
        var a = new Actual();
        var mine = w.R.Actions.Where(e => e.Source.Id == meId).GroupBy(e => e.Sequence != 0 ? (long)e.Sequence : ((long)e.ActionId << 32) + e.T).Select(g => g.First()).OrderBy(e => e.T).ToList();
        var tn = w.R.Statuses.Where(s => s.Target.Id == meId && s.Name == "True North").ToList();
        var pot = new Dictionary<string, int>
        {
            ["Gyofu"] = 240, ["Jinpu"] = 300, ["Shifu"] = 300, ["Yukikaze"] = 340, ["Gekko"] = 370, ["Kasha"] = 370, ["Enpi"] = 100, ["Higanbana"] = 200,
            ["Midare Setsugekka"] = 640, ["Tendo Setsugekka"] = 1100, ["Kaeshi: Setsugekka"] = 640, ["Tendo Kaeshi Setsugekka"] = 1100, ["Ogi Namikiri"] = 1000,
            ["Kaeshi: Namikiri"] = 1000, ["Hissatsu: Shinten"] = 250, ["Hissatsu: Senei"] = 800, ["Zanshin"] = 940, ["Shoha"] = 640, ["Hissatsu: Gyoten"] = 100,
        };
        var probe = new SamSim(w, x.Sim.GcdMs);
        var gcdStarts = new List<int>();
        foreach (var e in mine)
        {
            var tgt = e.PrimaryTarget ?? e.Hits.FirstOrDefault()?.Target;
            a.Events.Add((e.Cast?.StartMs ?? e.T, e.Name, tgt is { IsPlayer: true } && tgt.Id != meId ? tgt.Name : ""));
            if (e.T < 0) continue;
            a.Counts[e.Name] = a.Counts.GetValueOrDefault(e.Name) + 1;
            if (GcdNames.Contains(e.Name) || StandInGcds.Contains(e.Name)) { a.Gcds++; gcdStarts.Add(e.Cast?.StartMs ?? e.T - 450); }
            if (!pot.TryGetValue(e.Name, out var p) || tgt is null || tgt.IsPlayer || !e.Hits.Any(h => h.Kind == HitKind.Damage && !h.Target.IsPlayer)) continue;
            if (e.Name is "Gekko" or "Kasha")
            {
                probe.T = e.T;
                var angle = probe.AngleTo(tgt);
                if (Environment.GetEnvironmentVariable("SAM_POSDUMP") is { } dump)
                    File.AppendAllText(dump, $"{new DateTime(x.W.R.StartTicks + e.T * TimeSpan.TicksPerMillisecond):yyyy-MM-ddTHH:mm:ss.fff},{e.Name},{angle},{tn.Any(st => st.Active(e.T))},{probe.NeedsPositionals(tgt)}" + Environment.NewLine);
                var ok = angle == (e.Name == "Gekko" ? "rear" : "flank");
                var hasTn = tn.Any(s => s.Active(e.T));
                if (!probe.NeedsPositionals(tgt) || ok) { p += 50; a.PosHit++; }
                else if (hasTn) { p += 50; a.PosTn++; }
                else a.PosMiss++;
            }
            var (mult, crit) = probe.Factors(tgt, e.T, AutoCritNames.Contains(e.Name));
            a.Effective += p * mult * crit;
        }
        foreach (var st in w.R.Statuses.Where(s => s.Source?.Id == meId && s.Name == "Higanbana" && !s.Target.IsPlayer))
        {
            var (mult, crit) = probe.Factors(st.Target, st.StartMs, false);
            for (var t = st.StartMs + 3000; t <= Math.Min(st.EndMs, x.EndMs); t += 3000)
                if (st.Target.IsTargetable(t) && t >= 0) a.Effective += 50 * mult * crit;
        }
        a.Cancels = w.R.Casts.Count(c => c.Source.Id == meId && c.Outcome == CastOutcome.Cancelled && c.StartMs >= 0);
        gcdStarts.Sort();
        for (var t = 0; t < x.EndMs; t += 100)
        {
            if (w.Dead(w.Me, t) || SamSim.TargetAt(w, t) is not { } tg || SamSim.Incapacitated(w, t) is not null) continue;
            var gi = gcdStarts.BinarySearch(t); if (gi < 0) gi = ~gi - 1;
            if (gi >= 0 && t < gcdStarts[gi] + 2140) a.BusyMs += 100;
            if (w.R.Statuses.Any(s => s.Source?.Id == meId && s.Name == "Higanbana" && s.Target.Id == tg.Id && s.Active(t))) a.BanaUpMs += 100;
        }
        return a;
    }

    // ---------------------------------------------------------------- the sheet's Feint slots

    public sealed record SheetRow(string Phase, string Mech, int SheetMs, List<(string Label, bool Carry)> D1, List<(string Label, bool Carry)> D2);

    public static List<SheetRow> LoadSheet(string dir)
    {
        var rows = new List<SheetRow>();
        if (!Directory.Exists(dir)) return rows;
        List<(string, bool)> Col(string cell)
        {
            var l = new List<(string, bool)>();
            foreach (var raw in cell.Split('\n'))
            {
                var ln = raw.Trim();
                if (ln.Length == 0) continue;
                var carry = ln.StartsWith('➔');
                ln = ln.TrimStart('➔', ' ').Trim();
                foreach (var part in ln.Split('+'))
                {
                    var p = Regex.Replace(part, @"\s*[¹²³⁴⁵⁶⁷⁸⁹⁰]+", "").Trim();
                    if (p.Length > 0) l.Add((p, carry));
                }
            }
            return l;
        }
        foreach (var path in Directory.GetFiles(dir, "Ikuya Mitty (DMU) - P*.csv").OrderBy(p => p))
        {
            var phase = Regex.Match(Path.GetFileName(path), @" - (P\d)").Groups[1].Value;
            foreach (var r in Metrics.Csv(File.ReadAllText(path)))
            {
                if (r.Count < 26 || r[1].Trim().Length == 0 || r[1].Trim() == "Notes" || !r[3].Contains(':') || r[3].Contains('\n')) continue;
                var ms = r[3].Trim().Split(':');
                rows.Add(new SheetRow(phase, r[1].Trim().Replace('\n', ' '), (int.Parse(ms[0]) * 60 + int.Parse(ms[1])) * 1000, Col(r[17]), Col(r[19])));
            }
        }
        return rows;
    }

    public sealed record FeintSlot(string Col, string Phase, string Mech, int T, string Label, bool Carry, bool Sim, string Real);

    /// <summary> Each sheet row matched to its hit (one clock offset per phase, as reconcile.py fits it); the D1/D2 Feints checked. </summary>
    public static List<FeintSlot> FeintSlots(Result x, List<SheetRow> sheet, uint meId)
    {
        var w = x.W;
        var hits = Metrics.HitClusters(w);
        var used = new HashSet<(string, int)>();
        var res = new List<FeintSlot>();
        var realFeints = w.R.Statuses.Where(s => s.Name == "Feint" && !s.Target.IsPlayer).ToList();
        foreach (var phase in sheet.GroupBy(r => r.Phase).OrderBy(g => g.Key))
        {
            var rows = phase.ToList();
            (double D, int N, double Dev)? best = null;
            for (var d10 = -200; d10 <= 200; d10 += 5)
            {
                var copy = new HashSet<(string, int)>(used);
                int n = 0; double dev = 0;
                foreach (var r in rows)
                {
                    var exp = r.SheetMs + d10 * 100;
                    if (Metrics.FindHit(w, hits, r.Mech, exp, copy).T is { } tt) { n++; dev += Math.Abs(tt - exp); }
                }
                if (best is null || n > best.Value.N || n == best.Value.N && dev < best.Value.Dev) best = (d10 * 100, n, dev);
            }
            foreach (var r in rows)
            {
                if (Metrics.FindHit(w, hits, r.Mech, r.SheetMs + (int)best!.Value.D, used).T is not { } T) continue;
                foreach (var (col, labels) in new[] { ("D1", r.D1), ("D2", r.D2) })
                    foreach (var (label, carry) in labels.Where(l => l.Label.StartsWith("Feint")))
                    {
                        var boss = Regex.Match(label, @"\((Chaos|Exdeath)\)").Groups[1].Value;
                        bool Right(Actor? t) => t is not null && !t.IsPlayer && (boss.Length == 0 || t.Name.Contains(boss));
                        var sim = x.Sim.Feints.Any(f => f.T + Metrics.LandMs <= T && T < f.T + Metrics.LandMs + 15000 && Right(f.Target));
                        var real = realFeints.Where(s => s.StartMs <= T + 20 && s.EndMs >= T - 50 && Right(s.Target)).Select(s => s.Source?.Id == meId ? "stand-in" : s.Source?.Name ?? "?").Distinct().ToList();
                        res.Add(new FeintSlot(col, r.Phase, r.Mech, T, label, carry, sim, string.Join("+", real)));
                    }
            }
        }
        return res;
    }

    // ---------------------------------------------------------------- run

    private static int Run(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples");
        var pulls = Select(index(log), spec, meId);
        var sheet = LoadSheet(Opt("sheet", @"C:\Users\quanh\Workspace\WrathCombo\docs\guides\DMU"));
        var detail = A.Contains("--detail");
        var results = new List<(Result X, Actual Act, bool IsSam)>();
        Console.WriteLine($"rules {rulesDir}  select {Opt("select", "SAM_Lv100_SlowMAL.json")}  set [{Opt("set", "")}]  step {Opt("step", "0.25")} slide {Opt("slide", "700")}  dot {Opt("dot", "lost")}");
        foreach (var s in pulls)
        {
            var x = Simulate(s, meId, registry, rulesDir);
            var isSam = s.Party.Any(m => m.Id == meId && m.Job == SamJob);
            var act = ActualOf(x, meId);
            results.Add((x, act, isSam));
            if (pulls.Count == 1 || detail) PrintPull(x, act, isSam, sheet, meId, detail);
            else PrintPullShort(x, act, isSam);
        }
        if (results.Count > 1) PrintTotals(results);
        return 0;
    }

    private static string F(int ms) => SageSim.F(ms);

    private static void PrintPullShort(Result x, Actual act, bool isSam)
    {
        var sim = x.Sim;
        var mins = sim.AvailMs / 60000.0;
        Console.WriteLine($"#{x.S.Ordinal,-3} {new DateTime(x.S.StartTicks).ToLocalTime():MM-dd HH:mm} {F(x.EndMs),8} {x.W.R.PhaseAt(x.EndMs - 1),-22} up {sim.AvailMs / 1000,4}s GCD {sim.GcdMs}  " +
                          $"pot/min {Effective(x) / Math.Max(0.01, mins),5:0} vs {act.Effective / Math.Max(0.01, mins),5:0}  GCD/min {sim.Gcds / Math.Max(0.01, mins),4:0.0} vs {act.Gcds / Math.Max(0.01, mins),4:0.0}  " +
                          $"cancels {sim.Cancels,2} vs {act.Cancels,2}  Enpi {sim.Log.Count(e => e.What == "Enpi"),2} vs {act.Counts.GetValueOrDefault("Enpi"),2}  bana {100.0 * sim.BanaUpMs / Math.Max(1, sim.AvailMs),3:0}% vs {100.0 * act.BanaUpMs / Math.Max(1, sim.AvailMs),3:0}%");
    }

    private static readonly string[] Counted = ["Gyofu", "Jinpu", "Shifu", "Yukikaze", "Gekko", "Kasha", "Higanbana", "Midare Setsugekka", "Tendo Setsugekka",
        "Kaeshi: Setsugekka", "Tendo Kaeshi Setsugekka", "Ogi Namikiri", "Kaeshi: Namikiri", "Enpi", "Meikyo Shisui", "Hissatsu: Senei", "Ikishoten", "Zanshin",
        "Shoha", "Hissatsu: Shinten", "Hissatsu: Gyoten", "True North", "Tengentsu", "Feint", "Sprint"];

    private static void PrintPull(Result x, Actual act, bool isSam, List<SheetRow> sheet, uint meId, bool detail)
    {
        var (s, w, sim) = (x.S, x.W, x.Sim);
        var mins = sim.AvailMs / 60000.0;
        Console.WriteLine($"\n== pull #{s.Ordinal} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm} {F(x.EndMs)} {s.Outcome}, furthest {w.R.PhaseAt(x.EndMs - 1)}; target up and Samurai alive and able to act {sim.AvailMs / 1000.0:0}s; dead {sim.DeadMs / 1000.0:0}s, incapacitated {sim.IncapMs / 1000.0:0}s");
        Console.WriteLine($"   GCD {sim.GcdMs} ms with Fuka ({sim.GcdBaseMs} without), Iaijutsu/Ogi cast {sim.CastTime(SamSim.Defs[SA.Midare]) + 0} ms with Fuka; stand-in {w.Me.Name} (job {w.Me.Job}); hits from {sim.HitsActor.Name}");
        Console.WriteLine($"   files: {string.Join(", ", x.Files)}");
        var fixes = SamSim.RadiusFixes(w).ToList();
        if (fixes.Count > 0) Console.WriteLine("   hitbox radius from the party's melee hits: " + string.Join(", ", fixes.Select(f => $"{f.Actor.Name} {F(f.Actor.SpawnMs == int.MinValue ? 0 : f.Actor.SpawnMs)} {f.Actor.Radius:0.0} -> {f.Radius:0.0} y")));
        var colB = isSam ? "you" : "stand-in";
        Console.WriteLine($"   {"",-40}{"rules",10}{colB,10}");
        void Row(string n, double a, double? b, string fmt = "0") => Console.WriteLine($"   {n,-40}{a.ToString(fmt),10}{(b is { } bb ? bb.ToString(fmt) : ""),10}");
        var eff = Effective(x);
        Row("effective potency per minute", eff / mins, isSam ? act.Effective / mins : null);
        Row("raw potency x buffs per minute", Raw(x) / mins, null);
        Row("GCDs per minute", sim.Gcds / mins, act.Gcds / mins, "0.00");
        Row("GCD uptime while a target was up (%)", 100.0 * sim.BusyMs / Math.Max(1, sim.AvailMs), 100.0 * act.BusyMs / Math.Max(1, sim.AvailMs), "0.0");
        Row("Higanbana uptime (%)", 100.0 * sim.BanaUpMs / Math.Max(1, sim.AvailMs), isSam ? 100.0 * act.BanaUpMs / Math.Max(1, sim.AvailMs) : null, "0.0");
        var ticksKeep = DotTicks(x, true).Sum(t => t.Pot); var ticks = DotTicks(x).Sum(t => t.Pot);
        Row("Higanbana DoT potency (effective)", ticks, null);
        if (sim.DotLostOnSwitch) Row("  ... if kept through target changes", ticksKeep, null);
        Row("cast cancels", sim.Cancels, act.Cancels);
        Row("casts lost (target gone)", sim.CastsLostTarget, null);
        Row("Enpi", sim.Log.Count(e => e.What == "Enpi"), act.Counts.GetValueOrDefault("Enpi"));
        Row("out of melee (>3 y) while a target was up (s)", sim.OutOfMeleeMs / 1000.0, null, "0.0");
        Row("Kenki gained / wasted", sim.KenkiGained, null); Row("  Kenki wasted", sim.KenkiWasted, null);
        Row("Meditation wasted", sim.MeditationWasted, null);
        foreach (var n in Counted) Row(n, sim.Log.Count(e => e.What == n), act.Counts.GetValueOrDefault(n));
        // positionals
        var pos = sim.Positionals.Where(p => p.T >= 0).ToList();
        Console.WriteLine($"   positionals: {pos.Count} Gekko/Kasha; not needed {pos.Count(p => !p.Needed)}, hit by position {pos.Count(p => p.Needed && p.Geometry)}, by True North {pos.Count(p => p.Needed && !p.Geometry && p.TrueNorth)}, MISSED {pos.Count(p => p.Needed && !p.Geometry && !p.TrueNorth)}" +
                          (isSam ? $"   (you, same geometry: hit {act.PosHit}, True North {act.PosTn}, missed {act.PosMiss})" : ""));
        Console.WriteLine($"   True North: {sim.TrueNorths.Count} used, {sim.TrueNorths.Count(t => t.UsedByPositional)} saved a positional");
        // drift
        string Drift(uint id, int recast)
        {
            var uses = sim.Log.Where(e => e.What == SamSim.Defs[id].Name && e.T >= 0).Select(e => e.T).ToList();
            var d = uses.Zip(uses.Skip(1), (a, b) => (b - a - recast) / 1000.0).ToList();
            return $"{uses.Count} uses, held past ready {d.Sum():0.0}s total [{string.Join(" ", d.Select(v => v.ToString("0.0")))}]";
        }
        Console.WriteLine($"   Senei: {Drift(SA.Senei, 60000)}");
        Console.WriteLine($"   Ikishoten: {Drift(SA.Ikishoten, 120000)}");
        Console.WriteLine($"   Meikyo: {sim.Log.Count(e => e.What == "Meikyo Shisui" && e.T >= 0)} uses in combat, both charges up (capped) {sim.MeikyoCapMs / 1000.0:0.0}s");
        // raid windows
        var wins = RaidWindows(w, x.EndMs);
        var all = wins.Select(v => v.Max).DefaultIfEmpty(0).Max();
        var critGcds = sim.Damage.Where(d => d.AutoCrit).ToList();
        Console.WriteLine($"   raid windows (party buffs up to {all} at once): " + string.Join(" | ", wins.Select(v =>
        {
            var c = critGcds.Where(d => d.T >= v.Start && d.T < v.End).ToList();
            return $"{F(v.Start)}-{F(v.End)}: {c.Count} auto-crits, {c.Count(d => d.RaidCount >= all)} under all";
        })));
        var inWin = critGcds.Count(d => wins.Any(v => d.T >= v.Start && d.T < v.End));
        Console.WriteLine($"   auto-crit GCDs: {critGcds.Count} total, {inWin} in raid windows ({(wins.Count > 0 ? (double)inWin / wins.Count : 0):0.0} per window), {critGcds.Count(d => d.RaidCount >= all && all > 0)} under all buffs");
        // Tengentsu and hits
        var hitsTaken = sim.HitsTaken;
        var procs = sim.Tengentsus.Where(t => t.T >= 0 && t.Hit > 0).Select(t => (t, V: sim.ProcValue(t.Hit, sim.MergedHits.First(h => h.T == t.Hit).Pct))).ToList();
        Console.WriteLine($"   Tengentsu: {sim.Tengentsus.Count(t => t.T >= 0)} presses, {procs.Count} procs (Foresight, +10 Kenki each); HP saved {procs.Sum(p => p.V.Mit + p.V.Heal):0.0}% of max HP " +
                          $"(mitigation {procs.Sum(p => p.V.Mit):0.0}%, regen {procs.Sum(p => p.V.Heal):0.0}%); hits on {sim.HitsActor.Name}: {hitsTaken.Count} ({hitsTaken.Sum(h => h.Pct):0}% of HP in total)");
        if (detail)
            foreach (var t in sim.Tengentsus.Where(t => t.T >= 0))
                Console.WriteLine($"      Tengentsu {F(t.T)} -> {(t.Hit > 0 ? $"{t.HitName} {F(t.Hit)} (+{(t.Hit - t.T) / 1000.0:0.0}s), saved {procs.Where(p => p.t.T == t.T).Select(p => p.V.Mit + p.V.Heal).FirstOrDefault():0.0}% HP" : "no hit (wasted)")}   [{t.Rule.Replace("DMU mitigation plan (Samurai): ", "")}]");
        // Feint
        var slots = FeintSlots(x, sheet, meId);
        foreach (var col in new[] { "D1", "D2" })
        {
            var l = slots.Where(v => v.Col == col).ToList();
            if (l.Count == 0) continue;
            Console.WriteLine($"   Feint at the sheet's {col} slots: rules {l.Count(v => v.Sim)}/{l.Count} (direct {l.Count(v => v.Sim && !v.Carry)}/{l.Count(v => !v.Carry)}, carried {l.Count(v => v.Sim && v.Carry)}/{l.Count(v => v.Carry)}); log (anyone's Feint) {l.Count(v => v.Real.Length > 0)}/{l.Count}");
            if (detail)
                foreach (var v in l)
                    Console.WriteLine($"      {F(v.T),8} {v.Phase} {v.Mech,-34} {(v.Carry ? "> " : "")}{v.Label,-16} rules {(v.Sim ? "up" : "--")}  log {(v.Real.Length > 0 ? v.Real : "--")}");
        }
        Console.WriteLine($"   Feint presses: {string.Join(", ", sim.Feints.Select(f => $"{F(f.T)} on {f.Target?.Name}"))}");
        // phases
        Console.WriteLine("   by phase:          up(s)  GCD up%  bana%  out(s)  GCDs  cancels  Enpi  pot/min  idle(s) top reasons");
        foreach (var (phase, p) in sim.PhaseMs.OrderBy(k => w.R.Phases.FindIndex(ph => ph.Name == k.Key)))
        {
            var ph = w.R.Phases.FirstOrDefault(v => v.Name == phase && !v.IsSegment);
            bool In(int t) => w.R.PhaseAt(t) == phase;
            var pot = sim.Damage.Where(d => In(d.T)).Sum(d => d.Potency * d.Mult * d.Crit) + DotTicks(x).Where(t => In(t.T)).Sum(t => t.Pot);
            var idle = sim.Idle.Where(i => i.Phase == phase).GroupBy(i => i.Reason).Select(g => (g.Key, Ms: g.Sum(i => i.Ms))).OrderByDescending(g => g.Ms).ToList();
            Console.WriteLine($"   {phase,-18} {p.Avail / 1000.0,6:0} {100.0 * p.Busy / Math.Max(1, p.Avail),7:0.0} {100.0 * p.Bana / Math.Max(1, p.Avail),6:0} {p.Out / 1000.0,6:0.0} {sim.Log.Count(e => In(e.T) && SamCommands.GcdNames.Contains(e.What)),5} {sim.CancelLog.Count(c => In(c.T)),7} {sim.Log.Count(e => In(e.T) && e.What == "Enpi"),5} {pot / Math.Max(0.01, p.Avail / 60000.0),8:0} {idle.Sum(i => i.Ms) / 1000.0,7:0.0} " +
                              string.Join("; ", idle.Take(3).Select(i => $"{i.Key} {i.Ms / 1000.0:0.0}")));
        }
        // idle by reason and rule
        Console.WriteLine("   GCD idle while a target was up, by reason and rule (s):");
        foreach (var g in sim.Idle.GroupBy(i => (i.Reason, i.Rule)).Select(g => (g.Key, Ms: g.Sum(i => i.Ms), N: g.Count())).OrderByDescending(g => g.Ms).Take(14))
            Console.WriteLine($"      {g.Ms / 1000.0,6:0.0}s  x{g.N,-3} {g.Key.Reason}  [{g.Key.Rule}]");
        if (sim.CancelLog.Count > 0)
            Console.WriteLine("   cancels by cast: " + string.Join(", ", sim.CancelLog.GroupBy(c => c.Name).Select(g => $"{g.Key} {g.Count()}")) + "; by rule: " +
                              string.Join(", ", sim.CancelLog.GroupBy(c => c.Rule).Select(g => $"{g.Key} {g.Count()}")));
        Console.WriteLine($"   Kenki from: {string.Join(", ", sim.KenkiBy.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value}"))}");
        Console.WriteLine("   rule fires: " + string.Join("; ", sim.RuleFires.OrderByDescending(k => k.Value).Select(k => $"{k.Key.Replace("SlowSAM MAL (The Balance, 2.14 GCD): ", "").Replace("DMU mitigation plan (Samurai): ", "plan: ")} {k.Value}")));
        if (sim.Unmodelled > 0) Console.WriteLine($"   ! unmodelled actions returned {sim.Unmodelled} times");
        if (detail)
        {
            Console.WriteLine("   idle segments of 1 s or more:");
            foreach (var i in sim.Idle.Where(i => i.Ms >= 1000))
                Console.WriteLine($"      {F(i.T),8} {i.Ms / 1000.0,5:0.0}s {i.Phase,-18} {i.Reason} [{i.Rule}]");
            Console.WriteLine("   cancels:");
            foreach (var c in sim.CancelLog) Console.WriteLine($"      {F(c.T),8} {c.Name} moved {c.MovedInMs / 1000.0:0.00}s in [{c.Rule}]");
            Console.WriteLine("   missed positionals:");
            foreach (var p in pos.Where(p => p.Needed && !p.Geometry && !p.TrueNorth)) Console.WriteLine($"      {F(p.T),8} {p.Name} at the {p.Angle}");
            Console.WriteLine("   Higanbana applications:");
            foreach (var d in sim.Dots) Console.WriteLine($"      {F(d.Land),8} on {d.Target.Name}, ended {(d.EndedAt == int.MaxValue ? "-" : F(d.EndedAt))} {d.Why}");
        }
    }

    private static void PrintTotals(List<(Result X, Actual Act, bool IsSam)> rs)
    {
        double mins = rs.Sum(r => r.X.Sim.AvailMs) / 60000.0;
        double eff = rs.Sum(r => Effective(r.X)), aeff = rs.Sum(r => r.Act.Effective);
        int g = rs.Sum(r => r.X.Sim.Gcds), ag = rs.Sum(r => r.Act.Gcds);
        Console.WriteLine($"\n===== {rs.Count} pulls, {mins:0.0} minutes with a target up and the Samurai alive");
        void Row(string n, double a, double b, string fmt = "0") => Console.WriteLine($"   {n,-44}{a.ToString(fmt),10}{b.ToString(fmt),10}");
        Console.WriteLine($"   {"",-44}{"rules",10}{"logged",10}");
        Row("effective potency per minute", eff / mins, aeff / mins);
        Row("GCDs per minute", g / mins, ag / mins, "0.00");
        Row("GCD uptime while a target was up (%)", 100.0 * rs.Sum(r => r.X.Sim.BusyMs) / rs.Sum(r => r.X.Sim.AvailMs), 100.0 * rs.Sum(r => r.Act.BusyMs) / rs.Sum(r => r.X.Sim.AvailMs), "0.0");
        Row("Higanbana uptime (%)", 100.0 * rs.Sum(r => r.X.Sim.BanaUpMs) / rs.Sum(r => r.X.Sim.AvailMs), 100.0 * rs.Sum(r => r.Act.BanaUpMs) / rs.Sum(r => r.X.Sim.AvailMs), "0.0");
        Row("cast cancels", rs.Sum(r => r.X.Sim.Cancels), rs.Sum(r => r.Act.Cancels));
        Row("Kenki wasted", rs.Sum(r => r.X.Sim.KenkiWasted), 0);
        Row("Meditation wasted", rs.Sum(r => r.X.Sim.MeditationWasted), 0);
        Row("Kenki gained per minute", rs.Sum(r => r.X.Sim.KenkiGained) / mins, 0, "0.0");
        Row("Kenki spent per minute", rs.Sum(r => r.X.Sim.KenkiSpent) / mins, 0, "0.0");
        Console.WriteLine("   rules' Kenki wasted by source: " + string.Join(", ", rs.SelectMany(r => r.X.Sim.KenkiWastedBy).GroupBy(k => k.Key).Select(g => $"{g.Key} {g.Sum(k => k.Value)}")));
        Console.WriteLine("   incapacitated (not counted as up time) (s): " + (rs.Sum(r => r.X.Sim.IncapMs) / 1000.0).ToString("0"));
        foreach (var n in Counted)
            Row($"{n} per 10 min", 10 * rs.Sum(r => r.X.Sim.Log.Count(e => e.What == n && e.T >= 0)) / mins, 10 * rs.Sum(r => r.Act.Counts.GetValueOrDefault(n)) / mins, "0.0");
        var pos = rs.SelectMany(r => r.X.Sim.Positionals.Where(p => p.T >= 0)).ToList();
        Console.WriteLine($"   positionals: rules hit {pos.Count(p => !p.Needed || p.Geometry)} by position + {pos.Count(p => p.Needed && !p.Geometry && p.TrueNorth)} by True North, missed {pos.Count(p => p.Needed && !p.Geometry && !p.TrueNorth)}; " +
                          $"logged (same geometry) hit {rs.Sum(r => r.Act.PosHit)} + TN {rs.Sum(r => r.Act.PosTn)}, missed {rs.Sum(r => r.Act.PosMiss)}");
        var reasons = rs.SelectMany(r => r.X.Sim.Idle).GroupBy(i => (i.Reason, i.Rule)).Select(gg => (gg.Key, Ms: gg.Sum(i => i.Ms))).OrderByDescending(x => x.Ms).Take(10);
        Console.WriteLine("   rules' GCD idle by reason (s): " + string.Join("; ", reasons.Select(r => $"{r.Key.Reason} [{r.Key.Rule.Replace("SlowSAM MAL (The Balance, 2.14 GCD): ", "")}] {r.Ms / 1000.0:0}")));
    }

    // ---------------------------------------------------------------- validation: where the logged player's GCD time went

    /// <summary>
    ///     For the logged player's own pulls: every 100 ms with a target up and the player alive that no GCD of theirs
    ///     covers (start + 2.14 s, or the cast), by cause: out of melee, moving, incapacitated, else "free" (nothing in
    ///     the log explains it). The rules' idle time on the same pulls, by reason, alongside.
    /// </summary>
    private static int Gaps(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples");
        var you = new Dictionary<string, double>();
        var rules = new Dictionary<string, double>();
        double avail = 0;
        foreach (var s in Select(index(log), spec, meId))
        {
            var x = Simulate(s, meId, registry, rulesDir);
            var w = x.W;
            var starts = w.R.Actions.Where(e => e.Source.Id == meId && GcdNames.Contains(e.Name)).GroupBy(e => e.Sequence != 0 ? (long)e.Sequence : e.T)
                .Select(g => g.First()).Select(e => e.Cast?.StartMs ?? e.T - 450).OrderBy(t => t).ToList();   // instants: the press is ~0.45 s before the effect
            for (var t = 0; t < x.EndMs; t += 100)
            {
                if (w.Dead(w.Me, t) || SamSim.TargetAt(w, t) is not { } tg) continue;
                if (SamSim.Incapacitated(w, t) is { } inc0) { you[$"(incapacitated: {inc0}, not counted)"] = you.GetValueOrDefault($"(incapacitated: {inc0}, not counted)") + 0.1; continue; }
                avail += 0.1;
                var i = starts.BinarySearch(t); if (i < 0) i = ~i - 1;
                if (i >= 0 && t < starts[i] + 2140) continue;
                var why = SamSim.Incapacitated(w, t) is { } inc ? $"incapacitated ({inc})" : SamSim.DistFor(w, w.Me, tg, t) > 3 ? "out of melee" : w.Moving(t) ? "moving" : "free (no cause in the log)";
                you[why] = you.GetValueOrDefault(why) + 0.1;
            }
            foreach (var i in x.Sim.Idle) { var k = i.Reason.Split(':')[0]; rules[k] = rules.GetValueOrDefault(k) + i.Ms / 1000.0; }
        }
        Console.WriteLine($"{avail / 60:0.0} minutes with a target up and the player alive");
        Console.WriteLine("logged player's uncovered GCD time: " + string.Join(", ", you.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:0}s ({100 * k.Value / avail:0.0}%)")));
        Console.WriteLine("rules' GCD idle: " + string.Join(", ", rules.OrderByDescending(k => k.Value).Select(k => $"{k.Key} {k.Value:0}s ({100 * k.Value / avail:0.0}%)")));
        return 0;
    }

    // ---------------------------------------------------------------- the Phase 1 -> Phase 2 transition

    public static string Short(string n) => n switch
    {
        "Gyofu" => "Gy", "Jinpu" => "Ji", "Shifu" => "Sh", "Yukikaze" => "Yu", "Gekko" => "Ge", "Kasha" => "Ka", "Enpi" => "En", "Higanbana" => "HIGAN",
        "Midare Setsugekka" => "MIDARE", "Tendo Setsugekka" => "TENDO", "Kaeshi: Setsugekka" => "kaeshi", "Tendo Kaeshi Setsugekka" => "tendoK",
        "Ogi Namikiri" => "OGI", "Kaeshi: Namikiri" => "kaeshiN", "Tenka Goken" => "Tenka", "Tendo Goken" => "TGoken", _ => n,
    };

    public sealed record TransitionRow(string Pull, int P2Ms, string EntryState, string EntryRule, string ReengageState, int FirstGcdRel, string Seq,
        string Meikyos, string Tendos, string Higans, string Ogis, int WinStartRel, int WinEndRel, int CritsInWin, bool TendoInWin, bool OgiInWin,
        int CritsUnderAll, string Losses, double PotP1, double P1Mins, double PotP2, double P2Mins, int FirstHiganRel, double BanaP2, double PotP2Win,
        int HigApps = 0, int HigInWin = 0, int Win2StartRel = 0, bool TendoInWin2 = false, bool OgiInWin2 = false);

    /// <summary>
    ///     For every pull that reaches Phase 2: the rules' state at the last Phase 1 GCD and at the first Phase 2 GCD, the
    ///     first 90 s of Phase 2 (GCDs, Meikyo/Tendo/Higanbana/Ogi, the first Phase 2 raid window's auto-crits), and what
    ///     ran out or was wasted from 30 s before Phase 2 to 90 s into it. Potency per minute in Phase 1 and in Phase 2's
    ///     first 90 s, for A/B runs. Logs: one or several, separated by ';'.
    /// </summary>
    private static int Transition(List<string> logs, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples");
        var rows = new List<TransitionRow>();
        var quiet = A.Contains("--quiet");
        foreach (var log in logs)
            foreach (var s in Select(index(log), spec, meId))
            {
                var x = Simulate(s, meId, registry, rulesDir);
                var w = x.W; var sim = x.Sim;
                var p2 = w.R.Phases.FirstOrDefault(p => !p.IsSegment && p.Name.StartsWith("P2"));
                if (p2 is null) continue;
                var p2Start = p2.StartMs;
                var label = $"{Path.GetFileNameWithoutExtension(log)}#{s.Ordinal}";
                var last = sim.GcdStates.LastOrDefault(g => g.T < p2Start);
                var first = sim.GcdStates.FirstOrDefault(g => g.T >= p2Start);
                if (first.State is null) continue;
                var from = first.T;
                // --span=N: how many seconds of Phase 2 to study (default 90; 175 covers all of Phase 2).
                var to = Math.Min(from + int.Parse(Opt("span", "90")) * 1000, x.EndMs);
                string Rel(int t) => $"{(t - from) / 1000.0:+0.0;-0.0}";
                var gcds = sim.Log.Where(e => e.T >= from && e.T < to && GcdNames.Contains(e.What)).ToList();
                string Times(string name) => string.Join(" ", sim.Log.Where(e => e.T >= from - 20000 && e.T < to && e.What == name)
                    .Select(e => Rel(e.T) + (name == "Meikyo Shisui" ? $"({e.Rule.Split(": ").Last()})" : "")));
                var wins = RaidWindows(w, x.EndMs);
                var win = wins.FirstOrDefault(v => v.Start >= p2Start);
                var all = wins.Select(v => v.Max).DefaultIfEmpty(0).Max();
                var crits = win.End > 0 ? sim.Damage.Where(d => d.AutoCrit && d.T >= win.Start && d.T < win.End).ToList() : [];
                var losses = string.Join("; ", sim.Expired.Where(e => e.T >= p2Start - 30000 && e.T < to).Select(e => $"{Rel(e.T)} {e.What}"));
                bool InP1(int t) => w.R.PhaseAt(t).StartsWith("P1");
                double PotIn(Func<int, bool> f) => sim.Damage.Where(d => f(d.T)).Sum(d => d.Potency * d.Mult * d.Crit) + DotTicks(x).Where(t => f(t.T)).Sum(t => t.Pot);
                var p1Avail = sim.PhaseMs.Where(k => k.Key.StartsWith("P1")).Sum(k => k.Value.Avail) / 60000.0;
                var p2Avail = (to - from) / 60000.0;
                var firstHigan = sim.Log.FirstOrDefault(e => e.T >= from && e.What == "Higanbana")?.T ?? int.MaxValue;
                var bana = DotTicks(x).Where(t => t.T >= from && t.T < to).Sum(t => t.Pot);
                var potWin = win.End > 0 ? PotIn(t => t >= win.Start && t < win.End) : 0;
                // Higanbana alignment over the span: applications inside any raid window; the second Phase 2 window's Tendo and Ogi.
                var p2Wins = wins.Where(v => v.Start >= p2Start && v.Start < to).ToList();
                var higApps = sim.Log.Where(e => e.T >= from && e.T < to && e.What == "Higanbana").Select(e => e.T).ToList();
                var win2 = p2Wins.Skip(1).FirstOrDefault();
                bool In2(string src) => win2.End > 0 && sim.Damage.Any(d => d.AutoCrit && d.Src == src && d.T >= win2.Start && d.T < win2.End);
                var row = new TransitionRow(label, p2Start, last.State ?? "-", last.Rule ?? "-", first.State, first.T - p2Start,
                    string.Join(" ", gcds.Select(e => Short(e.What))), Times("Meikyo Shisui"), Times("Tendo Setsugekka"), Times("Higanbana"), Times("Ogi Namikiri"),
                    win.End > 0 ? win.Start - from : 0, win.End > 0 ? win.End - from : 0, crits.Count, crits.Any(c => c.Src == "Tendo Setsugekka"),
                    crits.Any(c => c.Src == "Ogi Namikiri"), crits.Count(c => c.RaidCount >= all), losses,
                    PotIn(InP1), p1Avail, PotIn(t => t >= from && t < to), p2Avail, firstHigan == int.MaxValue ? -1 : firstHigan - from, bana, potWin,
                    higApps.Count, higApps.Count(t => p2Wins.Any(v => t >= v.Start && t < v.End)), win2.End > 0 ? win2.Start - from : 0,
                    In2("Tendo Setsugekka"), In2("Ogi Namikiri"));
                rows.Add(row);
                if (quiet) continue;
                Console.WriteLine($"\n== {label} {new DateTime(s.StartTicks).ToLocalTime():MM-dd HH:mm}  P2 at {F(p2Start)}, first P2 GCD {F(first.T)} (+{(first.T - p2Start) / 1000.0:0.0}s)  pull ends {F(x.EndMs)} {w.R.PhaseAt(x.EndMs - 1)}");
                Console.WriteLine($"   last P1 GCD {F(last.T)} {last.Action} [{last.Rule?.Split(": ").Last()}]: {last.State}");
                Console.WriteLine($"   first P2 GCD {F(first.T)} {first.Action} [{first.Rule.Split(": ").Last()}]: {first.State}");
                Console.WriteLine($"   P2 first 90 s (times from the first P2 GCD): {row.Seq}");
                Console.WriteLine($"   Meikyo {row.Meikyos} | Tendo {row.Tendos} | Higanbana {row.Higans} | Ogi {row.Ogis}");
                Console.WriteLine($"   first P2 raid window {(win.End > 0 ? $"{Rel(win.Start)}..{Rel(win.End)}" : "none")}: {crits.Count} auto-crits ({string.Join(" ", crits.Select(c => Short(c.Src)))}), {row.CritsUnderAll} under all buffs; window potency {potWin:0}");
                if (losses.Length > 0) Console.WriteLine($"   ran out / wasted: {losses}");
                Console.WriteLine($"   potency/min: Phase 1 {row.PotP1 / Math.Max(0.01, p1Avail):0}, Phase 2 first 90 s {row.PotP2 / Math.Max(0.01, p2Avail):0}");
            }
        Console.WriteLine($"\n===== {rows.Count} pulls reaching Phase 2");
        Console.WriteLine($"   Phase 1 potency/min {rows.Sum(r => r.PotP1) / rows.Sum(r => r.P1Mins):0}; Phase 2 first 90 s potency/min {rows.Sum(r => r.PotP2) / rows.Sum(r => r.P2Mins):0}; first P2 raid window potency {rows.Sum(r => r.PotP2Win) / Math.Max(1, rows.Count):0} per pull");
        Console.WriteLine($"   first P2 raid window: auto-crits {rows.Sum(r => r.CritsInWin) / (double)Math.Max(1, rows.Count):0.00} per pull ({rows.Sum(r => r.CritsUnderAll)} under all buffs); Tendo in it {rows.Count(r => r.TendoInWin)}/{rows.Count}, Ogi in it {rows.Count(r => r.OgiInWin)}/{rows.Count}");
        Console.WriteLine($"   Higanbana in P2 first 90 s: first applied {string.Join(",", rows.Select(r => r.FirstHiganRel < 0 ? "-" : (r.FirstHiganRel / 1000.0).ToString("0")))} s after the first P2 GCD; DoT potency {rows.Sum(r => r.BanaP2) / Math.Max(1, rows.Count):0} per pull");
        var meikyoRules = rows.SelectMany(r => Regex.Matches(r.Meikyos, @"\(([^)]*)\)").Select(m => m.Groups[1].Value)).GroupBy(n => n).Select(g => $"{g.Key} {g.Count()}");
        Console.WriteLine($"   Meikyo rules from 20 s before to 90 s after the first P2 GCD: {string.Join(", ", meikyoRules)}");
        if (Opt("csv", "") is { Length: > 0 } csv)
            File.WriteAllLines(csv, new[] { "pull,p2Ms,firstGcdRel,critsInWin,tendoInWin,ogiInWin,critsUnderAll,potP1,p1Mins,potP2,p2Mins,firstHiganRel,banaP2,potP2Win,entryRule,entry,reengage,meikyos,tendos,higans,ogis,winStart,winEnd,losses,seq,higApps,higInWin,win2Start,tendoInWin2,ogiInWin2" }
                .Concat(rows.Select(r => string.Join(",", r.Pull, r.P2Ms, r.FirstGcdRel, r.CritsInWin, r.TendoInWin, r.OgiInWin, r.CritsUnderAll, r.PotP1.ToString("0"), r.P1Mins.ToString("0.000"),
                    r.PotP2.ToString("0"), r.P2Mins.ToString("0.000"), r.FirstHiganRel, r.BanaP2.ToString("0"), r.PotP2Win.ToString("0"), Q(r.EntryRule), Q(r.EntryState), Q(r.ReengageState), Q(r.Meikyos), Q(r.Tendos),
                    Q(r.Higans), Q(r.Ogis), r.WinStartRel, r.WinEndRel, Q(r.Losses), Q(r.Seq), r.HigApps, r.HigInWin, r.Win2StartRel, r.TendoInWin2, r.OgiInWin2))));
        return 0;
    }

    private static string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";

    // ---------------------------------------------------------------- timeline

    private static int Timeline(string log, string spec, uint meId, EncounterRegistry registry, Func<string, FileIndex> index)
    {
        var rulesDir = Opt("rules", @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples");
        var s = Select(index(log), spec, meId).First();
        double from = double.Parse(Opt("from", "-16")), to = double.Parse(Opt("to", "9999"));
        var x = Simulate(s, meId, registry, rulesDir);
        var act = ActualOf(x, meId);
        var ev = new List<(int T, int Order, string Line)>();
        foreach (var e in x.Sim.Log)
            ev.Add((e.T, 2, $"RULES {e.What}{(e.Note.Length > 0 ? $"  [{e.Note}]" : "")}   ({e.Rule.Replace("SlowSAM MAL (The Balance, 2.14 GCD): ", "").Replace("DMU mitigation plan (Samurai): ", "plan: ")})"));
        foreach (var e in act.Events) ev.Add((e.T, 3, $"{"",60}{x.W.Me.Name.Split(' ')[0]}: {e.Name}{(e.Target.Length > 0 ? " -> " + e.Target : "")}"));
        foreach (var i in x.Sim.Idle.Where(i => i.Ms >= 500)) ev.Add((i.T, 1, $"      idle {i.Ms / 1000.0:0.0}s: {i.Reason} [{i.Rule.Replace("SlowSAM MAL (The Balance, 2.14 GCD): ", "")}]"));
        var seen = new Dictionary<string, int>();
        foreach (var c in x.W.R.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Helper or ActorKind.Enemy))
        {
            if (seen.TryGetValue(c.Name, out var last) && Math.Abs(c.StartMs - last) <= 1000) continue;
            seen[c.Name] = c.StartMs;
            ev.Add((c.StartMs, 0, $"      boss cast: {c.Name} ({c.DurationMs / 1000.0:0.0}s)"));
        }
        foreach (var h in x.Sim.HitsTaken) ev.Add((h.T, 1, $"      HIT on {x.Sim.HitsActor.Name.Split(' ')[0]}: {h.HitName} {h.Pct:0}%{(h.Tengentsu ? " (Tengentsu)" : "")}"));
        foreach (var d in x.W.R.Deaths) ev.Add((d.T, 1, $"      DEATH {d.Victim.Name}"));
        foreach (var p in x.W.R.Phases.Where(p => !p.IsSegment)) ev.Add((p.StartMs, 0, $"=== {p.Name}"));
        int lastDist = -1;
        for (var t = (int)(from * 1000); t <= Math.Min(to * 1000, x.EndMs); t += 1000)
        {
            var tg = SamSim.TargetAt(x.W, t);
            var dist = tg is null ? -1 : (int)Math.Round(x.W.Dist(x.W.Me, tg, t));
            if (dist != lastDist && (dist > 3 || lastDist > 3)) ev.Add((t, 0, $"      [target {tg?.Name ?? "none"} {(dist < 0 ? "" : $"{dist} y")}]"));
            lastDist = dist;
        }
        foreach (var e in ev.Where(e => e.T >= from * 1000 && e.T <= to * 1000).OrderBy(e => e.T).ThenBy(e => e.Order))
            Console.WriteLine($"{F(e.T),8} {e.Line}");
        return 0;
    }
}
