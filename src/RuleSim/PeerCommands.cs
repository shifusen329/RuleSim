using System.Collections.Concurrent;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

/// <summary>
///     Damage per phase of every player on one job in Dancing Mad, the user against their peers (2026-11-18, user: "how does my DPS as
///     SGE in DMU through P2 compare to peers?"):
///
///   peers all|&lt;log&gt; &lt;job abbreviation&gt; [--from=YYYYMMDD] [--file=substr] [--extra=DIR;DIR] [--me=HEX,HEX] [--until=P2] [--min-pulls=5]
///
/// For each pull, each player on the job, and each top-level phase up to --until that the pull got past (a later phase began):
/// the player's damage in it (their actions' hits on enemies and their damage-over-time ticks, which the ACT lines attribute to
/// the caster) over the phase's length. "Clean" phases are those the player was alive through. --extra adds folders of other logs
/// (FFLogs fights converted to ACT logs, e.g. the top parses). Per player: phases, clean DPS per phase and through the last
/// phase (pulls that got past it, alive throughout), deaths per phase, and the pulls' dates.
/// </summary>
internal static class PeerCommands
{
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");

    private static readonly Dictionary<string, byte> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23, ["WHM"] = 24, ["BLM"] = 25, ["SMN"] = 27, ["SCH"] = 28, ["NIN"] = 30,
        ["MCH"] = 31, ["DRK"] = 32, ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35, ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39, ["SGE"] = 40, ["VPR"] = 41, ["PCT"] = 42,
    };

    public static bool Handles(string[] args) => args.Length > 2 && args[0] == "peers";

    private static FileIndex Index(string path, EncounterRegistry reg)
    {
        var prev = IndexStore.Load(CacheDir, path);
        var idx = IndexStore.IndexFile(path, prev, new EncounterObserverFactory(reg), reg.HashFor);
        if (!ReferenceEquals(idx, prev)) IndexStore.Save(CacheDir, idx);
        return idx;
    }

    private static IEnumerable<string> LogsIn(string dir, string from, string only) => Directory.GetFiles(dir, "Network_*.log")
        .Where(f => string.Compare(Path.GetFileNameWithoutExtension(f)[^8..], from, StringComparison.Ordinal) >= 0 && (only == "" || f.Contains(only)));

    private sealed record Seg(string Phase, int Order, double Dmg, double Sec, bool Died, int Gcds, double BuffFree, int HealGcds, Dictionary<string, int> HealNames,
        double NoLuck = 0, int Hits = 0, int Crits = 0, int Dhs = 0, int CleanHits = 0, int CleanCrits = 0, int CleanDhs = 0, int DevHits = 0);

    // Luck taken out (2026-11-18, user: "you're trying to tell me a 10% variance is attributable to RNG?"): each hit back to a normal hit
    // (a critical hit's multiplier, 1.62 at ~3,450-3,550 Critical Hit, and a direct hit's 1.25 divided out) and the raid buffs off.
    // Damage-over-time ticks carry no crit flags in the logs and stay as they are. The rate buffs (Battle Litany, Battle Voice, Chain
    // Stratagem, a Dancer's Devilment on their partner) work through crits and direct hits, so they come out with the flags, not with
    // World.RaidBuffs' multiplier; a Dancer's Standard Finish on their partner (+5%) comes out as damage. Crit% and DH% "clean" count
    // only hits under none of the rate buffs: with equal gear, those differ only by luck.
    private const double CritMult = 1.62, DhMult = 1.25;
    private static readonly string[] RateSelf = ["Battle Litany", "Battle Voice"];
    private const uint DevilmentId = 1825, PartnerStandardFinishId = 2105;

    private static (double DmgMult, bool Rate, bool Dev) Buffs(World w, Actor target, int t)
    {
        var m = w.RaidBuffs(target, t).Mult;
        var rate = false;
        foreach (var n in RateSelf)
            if (w.OthersHaveNamed(w.Me, n, t, out _)) { rate = true; if (World.BuffValue.TryGetValue(n, out var v)) m /= 1 + v; }
        if (w.OthersHaveNamed(target, "Chain Stratagem", t, out _)) { rate = true; m /= 1 + World.BuffValue["Chain Stratagem"]; }
        var dev = w.OthersHave(w.Me, DevilmentId, t);
        if (dev) rate = true;
        if (w.OthersHave(w.Me, PartnerStandardFinishId, t)) m *= 1.05;
        return (m, rate, dev);
    }

    private static HashSet<uint>? gcdActions;
    /// <summary> Weaponskills and spells (ActionCategory 2/3; action_cat.csv from the game data). </summary>
    private static HashSet<uint> GcdActions()
    {
        if (gcdActions is not null) return gcdActions;
        var p = Path.Combine(AppContext.BaseDirectory, "action_cat.csv");
        if (!File.Exists(p)) p = Path.Combine(Directory.GetCurrentDirectory(), "action_cat.csv");
        return gcdActions = File.ReadLines(p).Select(l => l.Split(',')).Where(x => x.Length >= 2 && x[1] is "2" or "3").Select(x => uint.Parse(x[0])).ToHashSet();
    }
    private sealed record PullResult(string Key, string Name, uint Id, DateTime When, bool Extra, List<Seg> Segs);

    public static int Main(string[] args, EncounterRegistry registry)
    {
        A = args;
        if (!Jobs.TryGetValue(args[2], out var job)) { Console.Error.WriteLine($"unknown job {args[2]}"); return 2; }
        var from = Opt("from", "20260918");
        var only = Opt("file", "");
        var me = Opt("me", "10041112,1001A4A8").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
        var until = Opt("until", "P2");
        var minPulls = int.Parse(Opt("min-pulls", "5"));
        var logs = new List<(string Path, bool Extra)>();
        if (args[1] == "all") logs.AddRange(LogsIn(LogsDir, from, only).Select(p => (p, false)));
        else logs.Add((File.Exists(args[1]) ? args[1] : Path.Combine(LogsDir, args[1]), false));
        foreach (var d in Opt("extra", "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            logs.AddRange((Directory.Exists(d) ? Directory.GetFiles(d, "*.log", SearchOption.AllDirectories) : [d]).Select(p => (p, true)));

        var jobsToLoad = new List<(PullSummary S, string Key, bool Extra)>();
        foreach (var (path, extra) in logs)
        {
            FileIndex idx;
            try { idx = Index(path, registry); } catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
            foreach (var s in idx.Pulls.Where(p => p.ZoneId == 1363 && p.HasDirector && p.EndTicks - p.StartTicks > 15 * TimeSpan.TicksPerSecond && p.Party.Any(x => x.Job == job)))
                jobsToLoad.Add((s, $"{Path.GetFileNameWithoutExtension(path)}_{s.Ordinal:D3}", extra));
        }
        Console.Error.WriteLine($"{jobsToLoad.Count} pulls with a {args[2].ToUpper()}");
        var results = new ConcurrentBag<PullResult>();
        Parallel.ForEach(jobsToLoad, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, j =>
        {
            PullReplay r;
            try { r = PullLoader.Load(j.S, null, registry); } catch { return; }
            var phases = r.Phases.Where(p => !p.IsSegment).OrderBy(p => p.StartMs).ToList();
            var players = (r.Party.Count > 0 ? r.Party : r.Players.ToList()).Where(x => x.Job == job).ToList();
            foreach (var pl in players)
            {
                var w = new World(r, pl.Id);
                var gcdIds = GcdActions();
                var segs = new List<Seg>();
                for (var i = 0; i < phases.Count; i++)
                {
                    var ph = phases[i];
                    var name = ph.Name.Split(' ')[0];
                    if (i + 1 >= phases.Count) break;                    // the pull ended in this phase: not complete
                    int s0 = ph.StartMs, s1 = phases[i + 1].StartMs;
                    double dmg = 0, free = 0, noLuck = 0; int hits = 0, crits = 0, dhs = 0, cHits = 0, cCrits = 0, cDhs = 0, devHits = 0;
                    var own = r.Actions.Where(a => a.Source.Id == pl.Id && a.T >= s0 && a.T < s1).ToList();
                    // Damage, and the same with each hit divided by the other players' raid buffs on the player and the target then
                    // (World.RaidBuffs, the scoring multipliers): the party's composition taken out.
                    foreach (var a in own)
                        foreach (var h in a.Hits.Where(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage))
                        {
                            var m = w.RaidBuffs(h.Target, h.T).Mult;
                            dmg += h.Damage; free += h.Damage / m;
                            var b = Buffs(w, h.Target, h.T);
                            noLuck += h.Damage / b.DmgMult / (h.Crit ? CritMult : 1) / (h.DirectHit ? DhMult : 1);
                            if (h.Damage > 0)
                            {
                                hits++; if (h.Crit) crits++; if (h.DirectHit) dhs++; if (b.Dev) devHits++;
                                if (!b.Rate) { cHits++; if (h.Crit) cCrits++; if (h.DirectHit) cDhs++; }
                            }
                        }
                    foreach (var t in r.Ticks.Where(t => !t.IsHeal && t.Source?.Id == pl.Id && !t.Target.IsPlayer && t.T >= s0 && t.T < s1))
                    { var m = w.RaidBuffs(t.Target, t.T).Mult; dmg += t.Amount; free += t.Amount / m; noLuck += t.Amount / Buffs(w, t.Target, t.T).DmgMult; }
                    var died = r.Deaths.Any(d => d.Victim.Id == pl.Id && d.T >= s0 && d.T < s1);
                    // GCDs (weaponskills and spells, one per use: the first line of an AoE's sequence), and those that hit no enemy (heals,
                    // shields, Eukrasia, Swiftcast-free raises).
                    var gcdUses = own.Where(a => gcdIds.Contains(a.ActionId)).GroupBy(a => (a.T / 100, a.ActionId)).Select(g => g.First()).ToList();
                    var healUses = gcdUses.Where(a => !own.Where(b => b.ActionId == a.ActionId && Math.Abs(b.T - a.T) < 100).Any(b => b.Hits.Any(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage))).ToList();
                    segs.Add(new Seg(name, i, dmg, (s1 - s0) / 1000.0, died, gcdUses.Count, free, healUses.Count,
                        healUses.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.Count()), noLuck, hits, crits, dhs, cHits, cCrits, cDhs, devHits));
                    if (name.Equals(until, StringComparison.OrdinalIgnoreCase)) break;
                }
                if (segs.Count > 0) results.Add(new PullResult(j.Key, pl.Name, pl.Id, new DateTime(j.S.StartTicks).ToLocalTime(), j.Extra, segs));
            }
        });

        var R = results.ToList();
        var phaseNames = R.SelectMany(p => p.Segs).GroupBy(s => s.Order).OrderBy(g => g.Key).Select(g => g.First().Phase).ToList();
        Console.WriteLine($"{R.Count} player-pulls that finished at least one phase; phases through {until}: {string.Join(", ", phaseNames)}; DPS = damage in the phase / its length, phases the player lived through");
        var header = $"{"player",-24} {"dates",-13} " + string.Join(" ", phaseNames.Select(p => $"{p + " n",6} {p + " DPS",8} {p + " died",8}")) + $" {"thru " + until + " n",11} {"thru DPS",9} {"no-buff DPS",11} {"no-luck DPS",11} {"crit%",6} {"DH%",6} {"clean crit%",11} {"clean DH%",9} {"clean hits",10} {"Devil%",6} {"GCD/min",8} {"heal GCD/min",12}";
        Console.WriteLine(header);
        void Line(string label, List<PullResult> L)
        {
            var parts = new List<string>();
            foreach (var p in phaseNames)
            {
                var segs = L.SelectMany(x => x.Segs).Where(s => s.Phase == p).ToList();
                var clean = segs.Where(s => !s.Died).ToList();
                var dps = clean.Sum(s => s.Sec) > 0 ? clean.Sum(s => s.Dmg) / clean.Sum(s => s.Sec) : 0;
                parts.Add($"{segs.Count,6} {dps,8:0} {(segs.Count > 0 ? 100.0 * segs.Count(s => s.Died) / segs.Count : 0),7:0}%");
            }
            var thru = L.Where(x => x.Segs.Count == phaseNames.Count && x.Segs.All(s => !s.Died)).ToList();
            var tSec = thru.Sum(x => x.Segs.Sum(s => s.Sec));
            var tDps = tSec > 0 ? thru.Sum(x => x.Segs.Sum(s => s.Dmg)) / tSec : 0;
            var tFree = tSec > 0 ? thru.Sum(x => x.Segs.Sum(s => s.BuffFree)) / tSec : 0;
            var tLuck = tSec > 0 ? thru.Sum(x => x.Segs.Sum(s => s.NoLuck)) / tSec : 0;
            double nh = thru.Sum(x => x.Segs.Sum(s => s.Hits));
            var crit = nh > 0 ? 100 * thru.Sum(x => x.Segs.Sum(s => s.Crits)) / nh : 0;
            var dh = nh > 0 ? 100 * thru.Sum(x => x.Segs.Sum(s => s.Dhs)) / nh : 0;
            double nc = thru.Sum(x => x.Segs.Sum(s => s.CleanHits));
            var cCrit = nc > 0 ? 100 * thru.Sum(x => x.Segs.Sum(s => s.CleanCrits)) / nc : 0;
            var cDh = nc > 0 ? 100 * thru.Sum(x => x.Segs.Sum(s => s.CleanDhs)) / nc : 0;
            var dev = nh > 0 ? 100 * thru.Sum(x => x.Segs.Sum(s => s.DevHits)) / nh : 0;
            var tGcd = tSec > 0 ? 60 * thru.Sum(x => x.Segs.Sum(s => s.Gcds)) / tSec : 0;
            var tHeal = tSec > 0 ? 60 * thru.Sum(x => x.Segs.Sum(s => s.HealGcds)) / tSec : 0;
            var dates = L.Count == 0 ? "" : $"{L.Min(x => x.When):MM/dd}-{L.Max(x => x.When):MM/dd}";
            Console.WriteLine($"{label,-24} {dates,-13} {string.Join(" ", parts)} {thru.Count,11} {tDps,9:0} {tFree,11:0} {tLuck,11:0} {crit,6:0.0} {dh,6:0.0} {cCrit,11:0.0} {cDh,9:0.0} {nc,10:0} {dev,6:0.0} {tGcd,8:0.0} {tHeal,12:0.0}");
        }
        var groups = R.GroupBy(x => (x.Name, x.Extra)).Where(g => g.Count() >= minPulls || g.Key.Extra || g.Any(x => me.Contains(x.Id))).ToList();
        foreach (var g in groups.OrderByDescending(g => g.Any(x => me.Contains(x.Id))).ThenBy(g => g.Key.Extra)
                     .ThenByDescending(g => g.SelectMany(x => x.Segs).Where(s => s.Phase == phaseNames[0] && !s.Died).Sum(s => s.Dmg) / Math.Max(1, g.SelectMany(x => x.Segs).Where(s => s.Phase == phaseNames[0] && !s.Died).Sum(s => s.Sec))))
            Line((g.Any(x => me.Contains(x.Id)) ? "* " : g.Key.Extra ? "^ " : "  ") + g.Key.Name, g.ToList());
        Line("  all peers (not you, logs)", R.Where(x => !me.Contains(x.Id) && !x.Extra).ToList());
        if (R.Any(x => x.Extra)) Line("  all --extra (FFLogs)", R.Where(x => x.Extra).ToList());
        Console.WriteLine("* you   ^ --extra logs (FFLogs top parses)");
        // GCDs that hit no enemy, per minute by name, through the last phase (the "thru" pulls), for you, the peers and --extra.
        if (A.Contains("--heals"))
        {
            void Heals(string label, List<PullResult> L)
            {
                var thru = L.Where(x => x.Segs.Count == phaseNames.Count && x.Segs.All(s => !s.Died)).ToList();
                var sec = thru.Sum(x => x.Segs.Sum(s => s.Sec));
                if (sec <= 0) return;
                var by = thru.SelectMany(x => x.Segs).SelectMany(s => s.HealNames).GroupBy(kv => kv.Key).Select(g => (g.Key, N: 60 * g.Sum(kv => kv.Value) / sec)).OrderByDescending(x => x.N);
                Console.WriteLine($"  no-enemy GCDs/min, {label} ({thru.Count} pulls): {string.Join(", ", by.Where(x => x.N >= 0.05).Select(x => $"{x.Key} {x.N:0.0}"))}");
            }
            Heals("you", R.Where(x => me.Contains(x.Id)).ToList());
            Heals("peers in your logs", R.Where(x => !me.Contains(x.Id) && !x.Extra).ToList());
            Heals("--extra (FFLogs)", R.Where(x => x.Extra).ToList());
        }
        // Same weeks only: peers' pulls from the dates the user played the job.
        var mine = R.Where(x => me.Contains(x.Id)).Select(x => x.When.Date).ToHashSet();
        Line("  peers, your nights only", R.Where(x => !me.Contains(x.Id) && !x.Extra && mine.Contains(x.When.Date)).ToList());
        return 0;
    }
}
