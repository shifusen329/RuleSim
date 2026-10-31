using System.Collections.Concurrent;
using Newtonsoft.Json;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The Dancer replay's commands (2026-10-23). Every Dancer in every Dancing Mad pull (or --me only) is replayed through
///     the DNC rule files and scored against what that Dancer really did, the same way:
///
///   run       all|&lt;log&gt; dnc [--me=HEX] [--rules=DIR] [--set=a=b,...] [--seeds=5] [--from=YYYYMMDD] [--file=substr]
///             [--min-active=60] [--detail] [--rate=0.18] [--select=FILE]
///   timeline  &lt;log&gt; &lt;pull&gt; dnc --me=HEX [--seed=1] [--from=s --to=s] [--trace]
///
/// Score: personal = potency x (Standard Finish, Technical Finish, Devilment's expected crit/DH, other players' raid buffs);
/// contributed = 5% of the party's logged damage inside the Dancer's Technical Finish, the partner's damage inside Devilment
/// (+15.5%) and Standard Finish (+5%), the real buffs' share taken out of the logged damage. Personal potency is turned into
/// damage with that Dancer's own damage-per-scored-potency in the pull, so total = personal + contributed is in damage.
/// </summary>
internal static class DncCommands
{
    public const byte DncJob = 38;
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private const string RepoRules = @"C:\Users\quanh\Workspace\WrathCombo\WrathCombo\CustomRules\Examples";
    private static readonly object DumpLock = new();
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");

    public static bool Handles(string[] args) => args.Length > 2 && args[0] is "run" or "timeline" && (args[2] == "dnc" || args.Contains("--job=dnc") || args.Length > 3 && args[3] == "dnc");

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

    private static HashSet<uint>? gcdActions;
    /// <summary> Weaponskills and spells (ActionCategory 2/3; dnc/action_cat.csv from the game data). </summary>
    private static HashSet<uint> GcdActions()
    {
        if (gcdActions is not null) return gcdActions;
        var p = Path.Combine(AppContext.BaseDirectory, "action_cat.csv");
        if (!File.Exists(p)) p = Path.Combine(Directory.GetCurrentDirectory(), "action_cat.csv");
        return gcdActions = File.ReadLines(p).Select(l => l.Split(',')).Where(x => x.Length >= 2 && x[1] is "2" or "3").Select(x => uint.Parse(x[0])).ToHashSet();
    }

    private static string SymbolsPath()
    {
        var p = Path.Combine(AppContext.BaseDirectory, "dnc_symbols.json");
        return File.Exists(p) ? p : Path.Combine(Directory.GetCurrentDirectory(), "dnc_symbols.json");
    }

    private static readonly ConcurrentDictionary<string, string> fileTexts = new();

    /// <summary> RuleEngine.FilesFor for DNC_ST_CustomRules (Cascade): the job's layers for the button in name order, then the selected file. </summary>
    public static List<CompiledRuleSet> Compile(DncSim sim, string rulesDir)
    {
        var symbols = new SimSymbols(SymbolsPath(), sim.Gauge);
        var selected = Opt("select", "DNC_ST.json");
        var layers = new List<CompiledRuleSet>(); var others = new List<CompiledRuleSet>();
        foreach (var path in Directory.GetFiles(rulesDir, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            var text = fileTexts.GetOrAdd(path, File.ReadAllText);
            var file = JsonConvert.DeserializeObject<RuleSetFile>(text);
            if (file is null || !string.Equals(file.Job, "DNC", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var kv in Opt("set", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var (k, v) = (kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
                if (file.Define.ContainsKey(k)) file.Define[k] = v;
            }
            var errors = new List<string>();
            var set = RuleSetCompiler.Compile(file, Path.GetFileNameWithoutExtension(path), symbols, _ => sim.Functions(), errors, DA.Cascade);
            if (set is null) throw new InvalidOperationException($"{Path.GetFileName(path)} doesn't compile:\n    " + string.Join("\n    ", errors));
            var forButton = set.Replaces.Count == 0 || set.Replaces.Contains(DA.Cascade);
            if (!forButton) continue;
            if (set.Layer) layers.Add(set);
            else if (Path.GetFileName(path).Equals(selected, StringComparison.OrdinalIgnoreCase)) others.Add(set);
        }
        return layers.Concat(others).ToList();
    }

    /// <summary> Median 2.5 s weaponskill interval of the logged Dancer in this pull (2.40-2.60 s), else 2500. </summary>
    private static int MeasureGcd(World w)
    {
        var names = new HashSet<string>(["Cascade", "Fountain", "Reverse Cascade", "Fountainfall", "Saber Dance", "Last Dance", "Dance of the Dawn", "Starfall Dance", "Tillana", "Finishing Move"]);
        var t = w.R.Actions.Where(a => a.Source.Id == w.Me.Id && names.Contains(a.Name)).Select(a => a.T).OrderBy(x => x).ToList();
        var iv = t.Zip(t.Skip(1), (a, b) => b - a).Where(d => d is >= 2400 and <= 2600).OrderBy(x => x).ToList();
        return iv.Count >= 5 ? iv[iv.Count / 2] : 2500;
    }

    public sealed record Outcome(string Key, string Name, uint Id, double ActiveMin, double SimPot, double RealPot, double SimContrib, double RealContrib,
        double K, double SimGcds, int RealGcds, double SimExpired, int RealExpired, double FeathersLost, double EspritLost, string Phase, Dictionary<string, double> Fires,
        double RaidPct, double SimSamba, double RealSamba, double SimBarrier, double RealBarrier);

    // ---------------------------------------------------------------- scoring shared by sim and real

    private sealed class Contrib
    {
        public readonly Dictionary<uint, List<(int T, long Dmg)>> PartyDmg = new();
        public readonly Dictionary<(uint Target, uint Status), List<(int S, int E)>> RealBy = new();
        public Contrib(World w)
        {
            var ids = w.Party.Where(p => p.Id != w.Me.Id).Select(p => p.Id).ToHashSet();
            foreach (var a in w.R.Actions)
            {
                if (!ids.Contains(a.Source.Id) && !(a.Source.Kind == ActorKind.Pet && ids.Contains(a.Source.OwnerId))) continue;
                var owner = ids.Contains(a.Source.Id) ? a.Source.Id : a.Source.OwnerId;
                var dmg = a.Hits.Where(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage).Sum(h => (long)h.Damage);
                if (dmg <= 0) continue;
                (PartyDmg.TryGetValue(owner, out var l) ? l : PartyDmg[owner] = []).Add((a.T, dmg));
            }
            foreach (var s in w.R.Statuses.Where(s => s.Source?.Id == w.Me.Id && s.StatusId is DS.TechnicalFinish or DS.Devilment or DS.PartnerStandardFinish))
                (RealBy.TryGetValue((s.Target.Id, s.StatusId), out var l) ? l : RealBy[(s.Target.Id, s.StatusId)] = []).Add((s.StartMs, s.EndMs));
        }
        public bool Real(uint target, uint status, int t) => RealBy.TryGetValue((target, status), out var l) && l.Any(x => x.S <= t && t < x.E);

        /// <summary> Damage the windows add: dmg x bonus / (1 + bonus if the real buff was on it). </summary>
        public double Windows(uint actor, IEnumerable<(int S, int E)> windows, uint realStatus, double bonus)
        {
            if (!PartyDmg.TryGetValue(actor, out var l)) return 0;
            var merged = new List<(int S, int E)>();
            foreach (var w in windows.OrderBy(x => x.S))
                if (merged.Count > 0 && w.S <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, w.E)); else merged.Add(w);
            double sum = 0;
            foreach (var (t, d) in l)
                if (merged.Any(m => m.S + 600 <= t && t < m.E + 600)) sum += d * bonus / (Real(actor, realStatus, t) ? 1 + bonus : 1);
            return sum;
        }
        public double RealWindows(uint actor, uint status, double bonus) =>
            RealBy.TryGetValue((actor, status), out var w) ? Windows(actor, w, status, bonus) : 0;
    }

    private static (double Pot, double Dmg, int Gcds) ActualPersonal(World w)
    {
        var me = w.Me;
        bool Own(uint st, int t) => w.R.Statuses.Any(s => s.Target.Id == me.Id && s.StatusId == st && s.Active(t));
        var sfI = w.R.Statuses.Where(s => s.Target.Id == me.Id && s.StatusId == DS.StandardFinish).ToList();
        var tfI = w.R.Statuses.Where(s => s.Target.Id == me.Id && s.StatusId == DS.TechnicalFinish).ToList();
        var dvI = w.R.Statuses.Where(s => s.Target.Id == me.Id && s.StatusId == DS.Devilment).ToList();
        double pot = 0, dmg = 0; var gcds = 0;
        var gcdNames = DncSim.Defs.Values.Where(d => d.Kind == AKind.Gcd).Select(d => d.Name).ToHashSet();
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == me.Id && a.T >= 0 && a.ActionId is not (33215 or 33216 or 33217 or 33218)))
        {
            if (gcdNames.Contains(a.Name) && !w.Dead(me, a.T)) gcds++;
            if (!Score.Potency.TryGetValue(a.Name, out var p)) continue;
            var hit = a.Hits.FirstOrDefault(h => !h.Target.IsPlayer && h.Kind == HitKind.Damage && h.Damage > 0);
            if (hit is null) continue;
            var m = Score.SelfMult(sfI.Any(s => s.Active(a.T)), tfI.Any(s => s.Active(a.T)), dvI.Any(s => s.Active(a.T))) * w.RaidBuffs(hit.Target, a.T).Mult;
            pot += p * (a.Name == "Starfall Dance" ? Score.Starfall : 1) * m;
            dmg += a.Hits.Where(h => h.Target == hit.Target).Sum(h => (double)h.Damage);
        }
        return (pot, dmg, gcds);
    }

    private static int RealExpired(World w)
    {
        var me = w.Me; var n = 0;
        var procs = new Dictionary<uint, string[]>
        {
            [DS.SilkenSymmetry] = ["Reverse Cascade", "Rising Windmill"], [DS.FlourishingSymmetry] = ["Reverse Cascade", "Rising Windmill"],
            [DS.SilkenFlow] = ["Fountainfall", "Bloodshower"], [DS.FlourishingFlow] = ["Fountainfall", "Bloodshower"], [DS.ThreeFold] = ["Fan Dance III"],
            [DS.FourFold] = ["Fan Dance IV"], [DS.LastDanceReady] = ["Last Dance"], [DS.FinishingMoveReady] = ["Finishing Move"],
            [DS.DanceOfTheDawnReady] = ["Dance of the Dawn"], [DS.FlourishingStarfall] = ["Starfall Dance"], [DS.FlourishingFinish] = ["Tillana"],
        };
        var acts = w.R.Actions.Where(a => a.Source.Id == me.Id).ToList();
        foreach (var s in w.R.Statuses.Where(s => s.Target.Id == me.Id && procs.ContainsKey(s.StatusId)))
        {
            var e = s.EndMs;
            if (e < 0 || e >= w.R.EndMs - 1000 || w.Dead(me, e + 1500)) continue;
            if (!acts.Any(a => Math.Abs(a.T - e) <= 700 && procs[s.StatusId].Contains(a.Name))) n++;
        }
        return n;
    }

    private static Outcome? One(PullSummary s, Actor dancer, PullReplay r, string rulesDir, int seeds, string key)
    {
        var w = new World(r, dancer.Id);
        var gcd = MeasureGcd(w);
        var contrib = new Contrib(w);
        var partner = DncSim.Partner(w);
        var (realPot, realDmg, realGcds) = ActualPersonal(w);
        var k = realPot > 0 ? realDmg / realPot : 0;
        var realCover = Cover(w.Raidwides.Where(x => x.T >= 0 && x.T < r.EndMs && !w.Dead(w.Me, x.T)).ToList(),
            r.Actions.Where(a => a.Source.Id == dancer.Id && a.Name == "Shield Samba").Select(a => a.T), r.Actions.Where(a => a.Source.Id == dancer.Id && a.Name == "Improvised Finish").Select(a => a.T));
        double realContrib = w.Party.Where(p => p.Id != dancer.Id).Sum(p => contrib.RealWindows(p.Id, DS.TechnicalFinish, 0.05));
        if (partner is not null)
            realContrib += contrib.RealWindows(partner.Id, DS.Devilment, Score.Dev - 1) + contrib.RealWindows(partner.Id, DS.PartnerStandardFinish, 0.05);
        double simPot = 0, simContrib = 0, simGcds = 0, simExp = 0, fl = 0, el = 0; var avail = 0;
        var fires = new Dictionary<string, double>();
        double simSamba = 0, simBarrier = 0;
        var rws = w.Raidwides.Where(x => x.T >= 0 && x.T < r.EndMs && !w.Dead(w.Me, x.T)).ToList();
        for (var seed = 1; seed <= seeds; seed++)
        {
            var sim = new DncSim(w, gcd, seed * 7919 + s.Ordinal, GcdActions()) { EspritRate = double.Parse(Opt("rate", "0.18")) };
            var files = Compile(sim, rulesDir);
            sim.T = StartMs(s, w);
            while (sim.T < r.EndMs)
            {
                sim.Press(files);
                var next = Math.Min(sim.NextWake(40), r.EndMs);
                if (next <= sim.T) break;
                sim.Advance(next - sim.T);
            }
            simPot += sim.Damage.Sum(d => d.Potency * d.Mult);
            var c = sim.TechWindows.Sum(tw => tw.Who.Sum(p => contrib.Windows(p.Id, [(tw.Start, tw.End)], DS.TechnicalFinish, 0.05)));
            if (sim.PartnerActor is not null)
                c += contrib.Windows(sim.PartnerActor.Id, sim.DevWindows, DS.Devilment, Score.Dev - 1) + contrib.Windows(sim.PartnerActor.Id, sim.SfWindows, DS.PartnerStandardFinish, 0.05);
            simContrib += c;
            simGcds += sim.Gcds; simExp += sim.Expired.Values.Sum(); fl += sim.FeathersLost; el += sim.EspritLost;
            avail = sim.AvailMs;
            var (sa, ba) = Cover(rws, sim.Mit.Where(m => m.What == "Shield Samba").Select(m => m.T), sim.Mit.Where(m => m.What == "Improvised Finish").Select(m => m.T));
            simSamba += sa; simBarrier += ba;
            if (Environment.GetEnvironmentVariable("SAMBA_DUMP") is { Length: > 0 } dump)
            {
                var sb = new System.Text.StringBuilder();
                string Ts(IEnumerable<int> xs) => string.Join(";", xs);
                sb.AppendLine($"P|{key}|{dancer.Id:X8}|{seed}|{r.EndMs}|{Ts(sim.Mit.Where(m => m.What == "Shield Samba").Select(m => m.T))}|" +
                    $"{Ts(sim.Log.Where(e => e.What == "Technical Step").Select(e => e.T))}|{Ts(sim.Log.Where(e => e.What.Contains("Technical Finish")).Select(e => e.T))}|" +
                    $"{Ts(sim.Log.Where(e => e.What == "Shield Samba").Select(e => e.T))}|{string.Join(";", sim.Log.Where(e => e.What == "Shield Samba").Select(e => e.Rule))}");
                if (seed == 1)
                {
                    sb.AppendLine($"R|{key}|{dancer.Id:X8}|{Ts(r.Actions.Where(a => a.Source.Id == dancer.Id && a.Name == "Shield Samba").Select(a => a.T))}");
                    foreach (var x in rws) sb.AppendLine($"H|{key}|{x.T}|{x.Name}|{x.Ordinal}|{x.Players}|{x.AvgPct:0.00}");
                    // Wave Cannon hits four players, so it is not a raidwide: the first hit of each set.
                    var wcs = new List<int>();
                    foreach (var h in w.HitsOnParty.Where(h => h.Name == "Wave Cannon" && !w.Dead(w.Me, h.T)).OrderBy(h => h.T))
                        if (wcs.Count == 0 || h.T - wcs[^1] > 2000) wcs.Add(h.T);
                    foreach (var t in wcs) sb.AppendLine($"W|{key}|{t}");
                }
                lock (DumpLock) File.AppendAllText(dump, sb.ToString());
            }
            foreach (var (rule, n) in sim.RuleFires) fires[rule] = fires.GetValueOrDefault(rule) + n / (double)seeds;
        }
        var activeMin = avail / 60000.0;
        return new Outcome(key, dancer.Name, dancer.Id, activeMin, simPot / seeds, realPot, simContrib / seeds, realContrib, k, simGcds / seeds, realGcds,
            simExp / seeds, RealExpired(w), fl / seeds, el / seeds, r.PhaseAt(r.EndMs - 1), fires,
            rws.Sum(x => x.AvgPct), simSamba / seeds, realCover.Samba, simBarrier / seeds, realCover.Barrier);
    }

    /// <summary> The countdown the replay gets: the pull's (up to 20 s), or 16 s when the logged Dancer acted before the pull without one detected. </summary>
    private static int StartMs(PullSummary s, World w)
    {
        if (s.CountdownSeconds > 0) return -Math.Min(s.CountdownSeconds, 20) * 1000;
        return w.R.Actions.Any(a => a.Source.Id == w.Me.Id && a.T < -1000 && a.Name != "attack") ? -16000 : 0;
    }

    /// <summary> Raidwide damage (sum of each hit's average % of max HP) under Shield Samba (pressed 0.6-15.6 s before), and the first raidwide within 30 s of an Improvised Finish (its barrier). </summary>
    private static (double Samba, double Barrier) Cover(List<World.Raidwide> rws, IEnumerable<int> samba, IEnumerable<int> finish)
    {
        var s = samba.ToList(); double sa = 0, ba = 0;
        foreach (var x in rws) if (s.Any(t => t + 600 <= x.T && x.T <= t + 15600)) sa += x.AvgPct;
        foreach (var t in finish) if (rws.FirstOrDefault(x => x.T >= t + 600 && x.T <= t + 30600) is { } hit) ba += hit.AvgPct;
        return (sa, ba);
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
                                                    && p.Party.Any(x => me is null ? x.Job == DncJob : x.Id == me)))
                jobs.Add((s, $"{Path.GetFileNameWithoutExtension(path)[^8..]}_{s.Ordinal:D3}"));
        }
        Console.Error.WriteLine($"{jobs.Count} pulls; rules {rulesDir}; set {Opt("set", "-")}; seeds {seeds}");
        var results = new ConcurrentBag<Outcome>();
        var done = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, job =>
        {
            PullReplay r;
            try { r = PullLoader.Load(job.S, null, registry); } catch { return; }
            var dancers = r.Party.Count > 0 ? r.Party : r.Players.ToList();
            foreach (var d in dancers.Where(x => me is null ? x.Job == DncJob : x.Id == me))
            {
                try { if (One(job.S, d, r, rulesDir, seeds, job.Key) is { } o) results.Add(o); }
                catch (InvalidOperationException e) { Console.Error.WriteLine(e.Message); Environment.Exit(2); }
                catch (Exception e) { Console.Error.WriteLine($"{job.Key} {d.Name}: {e.GetType().Name} {e.Message}"); }
            }
            if (Interlocked.Increment(ref done) % 100 == 0) Console.Error.WriteLine($"  {done}/{jobs.Count}");
        });
        var R = results.Where(o => o.ActiveMin >= minActive && o.RealPot > 0).OrderBy(o => o.Key).ToList();
        if (A.Contains("--detail"))
            foreach (var o in R)
                Console.WriteLine($"{o.Key} {o.Name,-20} {o.ActiveMin,5:0.0}m {o.Phase,-20} pot/min sim {o.SimPot / o.ActiveMin,7:0} real {o.RealPot / o.ActiveMin,7:0} ({100 * (o.SimPot / o.RealPot - 1),+6:0.0}%)  contrib/min sim {o.SimContrib / o.ActiveMin,7:0} real {o.RealContrib / o.ActiveMin,7:0}  gcd {o.SimGcds,5:0.0}/{o.RealGcds} expired {o.SimExpired:0.0}/{o.RealExpired}");
        Console.WriteLine($"\n{R.Count} Dancer-pulls with >= {minActive * 60:0} s active ({results.Count} total), {seeds} seeds, rules {Path.GetFileName(rulesDir)}, set {Opt("set", "-")}");
        Console.WriteLine($"{"Dancer",-22} {"pulls",5} {"min",6} {"pot/min sim",11} {"real",7} {"diff",7} {"contrib sim",11} {"real",7} {"total diff",10} {"GCD/min sim",11} {"real",6} {"exp/10m sim",11} {"real",5}");
        void Line(string name, List<Outcome> L)
        {
            var act = L.Sum(o => o.ActiveMin);
            double sp = L.Sum(o => o.SimPot), rp = L.Sum(o => o.RealPot), sc = L.Sum(o => o.SimContrib), rc = L.Sum(o => o.RealContrib);
            double st = L.Sum(o => o.SimPot * o.K + o.SimContrib), rt = L.Sum(o => o.RealPot * o.K + o.RealContrib);
            Console.WriteLine($"{name,-22} {L.Count,5} {act,6:0} {sp / act,11:0} {rp / act,7:0} {100 * (sp / rp - 1),+6:0.0}% {sc / act,11:0} {rc / act,7:0} {100 * (st / rt - 1),+9:0.0}% {L.Sum(o => o.SimGcds) / act,11:0.00} {L.Sum(o => o.RealGcds) / act,6:0.00} {10 * L.Sum(o => o.SimExpired) / act,11:0.0} {10 * L.Sum(o => o.RealExpired) / act,5:0.0}");
        }
        foreach (var g in R.GroupBy(o => o.Name).Where(g => g.Sum(o => o.ActiveMin) >= 10).OrderByDescending(g => g.Sum(o => o.RealPot) / g.Sum(o => o.ActiveMin)))
            Line(g.Key, g.ToList());
        Line("ALL", R);
        var med = R.GroupBy(o => o.Name).Where(g => g.Sum(o => o.ActiveMin) >= 10).Select(g => g.Sum(o => o.RealPot) / g.Sum(o => o.ActiveMin)).OrderBy(x => x).ToList();
        if (med.Count > 0) Console.WriteLine($"median Dancer real pot/min {med[med.Count / 2]:0}, best {med[^1]:0}; rules {R.Sum(o => o.SimPot) / R.Sum(o => o.ActiveMin):0}");
        Console.WriteLine($"sim feathers lost/10m {10 * R.Sum(o => o.FeathersLost) / R.Sum(o => o.ActiveMin):0.00}, Esprit lost/10m {10 * R.Sum(o => o.EspritLost) / R.Sum(o => o.ActiveMin):0}");
        Console.WriteLine($"raidwide damage under Shield Samba: sim {100 * R.Sum(o => o.SimSamba) / R.Sum(o => o.RaidPct):0.0}% real {100 * R.Sum(o => o.RealSamba) / R.Sum(o => o.RaidPct):0.0}%; first hit on an Improvised Finish barrier: sim {R.Sum(o => o.SimBarrier):0} real {R.Sum(o => o.RealBarrier):0} (% HP summed)");
        foreach (var ph in R.GroupBy(o => o.Phase.Split(' ')[0]).OrderBy(g => g.Key))
            Console.WriteLine($"   pulls ending in {ph.Key}: Samba sim {100 * ph.Sum(o => o.SimSamba) / Math.Max(1, ph.Sum(o => o.RaidPct)):0.0}% real {100 * ph.Sum(o => o.RealSamba) / Math.Max(1, ph.Sum(o => o.RaidPct)):0.0}%; barrier sim {ph.Sum(o => o.SimBarrier):0} real {ph.Sum(o => o.RealBarrier):0}");
        if (A.Contains("--fires"))
            foreach (var kv in R.SelectMany(o => o.Fires).GroupBy(kv => kv.Key).Select(g => (g.Key, N: g.Sum(x => x.Value))).OrderByDescending(x => x.N))
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
        var sim = new DncSim(w, MeasureGcd(w), int.Parse(Opt("seed", "1")) * 7919 + s.Ordinal, GcdActions())
        {
            EspritRate = double.Parse(Opt("rate", "0.18")), Trace = A.Contains("--trace"),
            TraceFrom = (int)(double.Parse(Opt("from", "-30")) * 1000), TraceTo = (int)(double.Parse(Opt("to", "9999")) * 1000),
        };
        var files = Compile(sim, Opt("rules", RepoRules));
        sim.T = StartMs(s, w);
        while (sim.T < r.EndMs)
        {
            sim.Press(files);
            var next = Math.Min(sim.NextWake(40), r.EndMs);
            if (next <= sim.T) break;
            sim.Advance(next - sim.T);
        }
        int from = sim.TraceFrom, to = sim.TraceTo;
        Console.WriteLine($"{Path.GetFileName(path)} #{ordinal} {w.Me.Name} gcd {sim.GcdMs} partner {sim.PartnerActor?.Name}; sim left, real right");
        var real = r.Actions.Where(a => a.Source.Id == meId && a.T >= from && a.T <= to && a.Name != "attack" && a.ActionId is not (33215 or 33216 or 33217 or 33218))
            .Select(a => (a.T, Text: $"{"",-58} | {a.Name}")).ToList();
        var mine = sim.Log.Where(e => e.T >= from && e.T <= to).Select(e => (e.T, Text: $"{e.What,-26} {e.Rule.Split(": ").Last(),-31}|")).ToList();
        foreach (var (t, text) in mine.Concat(real).OrderBy(x => x.T)) Console.WriteLine($"{SageSim.F(t),8} {text}");
        Console.WriteLine($"expired: {string.Join(", ", sim.Expired.Select(kv => $"{kv.Key} {kv.Value}"))}; feathers lost {sim.FeathersLost}, Esprit lost {sim.EspritLost}, from party {sim.EspritFromParty}");
        foreach (var b in sim.Bursts) Console.WriteLine($"burst {SageSim.F(b.T)}: {string.Join(", ", b.Gcds)}");
        var realSamba = r.Actions.Where(a => a.Source.Id == meId && a.Name == "Shield Samba").Select(a => a.T).ToList();
        var simSamba = sim.Mit.Where(m => m.What == "Shield Samba").Select(m => m.T).ToList();
        Console.WriteLine($"Samba real {string.Join(" ", realSamba.Select(SageSim.F))}; sim {string.Join(" ", simSamba.Select(SageSim.F))}; Improvised Finish sim {string.Join(" ", sim.Mit.Where(m => m.What == "Improvised Finish").Select(m => SageSim.F(m.T)))}");
        foreach (var x in w.Raidwides.Where(x => x.T >= 0))
            Console.WriteLine($"   raidwide {SageSim.F(x.T)} {x.Name,-28} {x.AvgPct,5:0.0}%  samba sim {(simSamba.Any(t => t + 600 <= x.T && x.T <= t + 15600) ? "X" : "-")} real {(realSamba.Any(t => t + 600 <= x.T && x.T <= t + 15600) ? "X" : "-")}");
        var contrib = new Contrib(w);
        var partner = DncSim.Partner(w);
        Console.WriteLine($"real Technical Finish on party: {string.Join(", ", contrib.RealBy.Where(kv => kv.Key.Status == DS.TechnicalFinish && kv.Key.Target == meId).SelectMany(kv => kv.Value).Select(x => SageSim.F(x.S)))}; sim: {string.Join(", ", sim.TechWindows.Select(x => SageSim.F(x.Start) + $"({x.Who.Count})"))}");
        double rt = w.Party.Where(p => p.Id != meId).Sum(p => contrib.RealWindows(p.Id, DS.TechnicalFinish, 0.05)), st = sim.TechWindows.Sum(tw => tw.Who.Sum(p => contrib.Windows(p.Id, [(tw.Start, tw.End)], DS.TechnicalFinish, 0.05)));
        double rd = partner is null ? 0 : contrib.RealWindows(partner.Id, DS.Devilment, Score.Dev - 1), sd = partner is null ? 0 : contrib.Windows(partner.Id, sim.DevWindows, DS.Devilment, Score.Dev - 1);
        double rs = partner is null ? 0 : contrib.RealWindows(partner.Id, DS.PartnerStandardFinish, 0.05), ss = partner is null ? 0 : contrib.Windows(partner.Id, sim.SfWindows, DS.PartnerStandardFinish, 0.05);
        Console.WriteLine($"contrib tech sim {st:0} real {rt:0}; devilment sim {sd:0} real {rd:0}; standard finish sim {ss:0} real {rs:0}");
        foreach (var p in w.Party.Where(p => p.Id != meId))
            Console.WriteLine($"   {p.Name,-22} tech sim {sim.TechWindows.Sum(tw => tw.Who.Contains(p) ? contrib.Windows(p.Id, [(tw.Start, tw.End)], DS.TechnicalFinish, 0.05) : 0),8:0} real {contrib.RealWindows(p.Id, DS.TechnicalFinish, 0.05),8:0}  real windows {string.Join(" ", contrib.RealBy.TryGetValue((p.Id, DS.TechnicalFinish), out var l) ? l.Select(x => SageSim.F(x.S) + "-" + SageSim.F(x.E)) : [])}");
        return 0;
    }
}
