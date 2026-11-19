using System.Collections.Concurrent;
using System.Numerics;
using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;

/// <summary>
///     The user's runs at a fight-known move (2026-11-18, user: "a span during P1 Graven 2 where I need to run from one side of the
///     room to the other and a point during P3 BoA ... Identify those spans"):
///
///   moves all|&lt;log&gt; &lt;job&gt; --anchor=Cast[#n] [--win=-5,25] [--also=Cast#n,...] [--me=HEX,HEX] [--from=YYYYMMDD] [--file=substr]
///         [--dump] [--casts] [--pause=2.5] [--min-disp=15]
///
/// For each Dancing Mad pull with the user on the job that reached the anchor (the n-th start of that enemy cast, World.CastStarts),
/// the user's position samples from anchor+from to anchor+to are split into journeys from a standstill to a standstill: steps
/// of 0.3 y or more, joined across pauses shorter than --pause=2.5 s (a run around the room's edge stops for up to ~1.8 s on
/// the way). The journey with the largest displacement is the run. A step (0.3 y or more between samples) is taken to end at its later
/// sample and to have run at 6 y/s (the base run speed), so the run starts at its first step's later sample minus that step's
/// length / 6, and ends at its last step's sample; "moving" is the time spent on the steps. Printed per pull and summarised: the run's start and end from the anchor (and from each
/// --also cast), its length in time, displacement and path, and where it went (x, y from the arena centre 100, 100). --dump prints
/// every enemy cast start and position sample in the window; --casts lists the user's actions and interrupted casts during the run.
/// </summary>
internal static class MoveCommands
{
    private static string[] A = [];
    private static string Opt(string name, string def) => A.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
    private static readonly string LogsDir = Environment.GetEnvironmentVariable("RR_LOGS") is { Length: > 0 } rrLogs ? rrLogs : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Advanced Combat Tracker", "FFXIVLogs");
    private static readonly string CacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaidReplay", "index");
    private const double RunSpeed = 6.0, MinStep = 0.3;

    private static readonly Dictionary<string, byte> Jobs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = 19, ["MNK"] = 20, ["WAR"] = 21, ["DRG"] = 22, ["BRD"] = 23, ["WHM"] = 24, ["BLM"] = 25, ["SMN"] = 27, ["SCH"] = 28, ["NIN"] = 30,
        ["MCH"] = 31, ["DRK"] = 32, ["AST"] = 33, ["SAM"] = 34, ["RDM"] = 35, ["GNB"] = 37, ["DNC"] = 38, ["RPR"] = 39, ["SGE"] = 40, ["VPR"] = 41, ["PCT"] = 42,
    };

    public static bool Handles(string[] args) => args.Length > 2 && args[0] == "moves";

    private static FileIndex Index(string path, EncounterRegistry reg)
    {
        var prev = IndexStore.Load(CacheDir, path);
        var idx = IndexStore.IndexFile(path, prev, new EncounterObserverFactory(reg), reg.HashFor);
        if (!ReferenceEquals(idx, prev)) IndexStore.Save(CacheDir, idx);
        return idx;
    }

    private static (string Name, int N) ParseCast(string s)
    {
        var i = s.LastIndexOf('#');
        return i < 0 ? (World.Normalize(s), 1) : (World.Normalize(s[..i]), int.Parse(s[(i + 1)..]));
    }

    private sealed record Run(string Key, DateTime When, int AnchorMs, double Start, double End, double Disp, double Path, Vector2 From, Vector2 To,
        Dictionary<string, double> Also, List<string> Lines, double Moving, string Route, string FromSide, string ToSide);

    private static string Side(float x, float y)
    {
        var a = (Math.Atan2(x - 100, -(y - 100)) * 180 / Math.PI + 360) % 360;
        return new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)((a + 22.5) % 360 / 45)];
    }

    public static int Main(string[] args, EncounterRegistry registry)
    {
        A = args;
        if (!Jobs.TryGetValue(args[2], out var job)) { Console.Error.WriteLine($"unknown job {args[2]}"); return 2; }
        var me = Opt("me", "10041112,1001A4A8").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
        var anchor = ParseCast(Opt("anchor", ""));
        var win = Opt("win", "-5,25").Split(',').Select(double.Parse).ToArray();
        var also = Opt("also", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => (Label: x, Cast: ParseCast(x))).ToList();
        bool dump = A.Contains("--dump"), casts = A.Contains("--casts");
        var pause = (int)(double.Parse(Opt("pause", "2.5")) * 1000);
        var from = Opt("from", "20260918");
        var only = Opt("file", "");
        var logs = args[1] == "all"
            ? Directory.GetFiles(LogsDir, "Network_*.log").Where(f => string.Compare(Path.GetFileNameWithoutExtension(f)[^8..], from, StringComparison.Ordinal) >= 0 && (only == "" || f.Contains(only))).ToList()
            : [File.Exists(args[1]) ? args[1] : Path.Combine(LogsDir, args[1])];

        var toLoad = new List<(PullSummary S, string Key)>();
        foreach (var path in logs)
        {
            FileIndex idx;
            try { idx = Index(path, registry); } catch (Exception e) { Console.Error.WriteLine($"{path}: {e.Message}"); continue; }
            foreach (var s in idx.Pulls.Where(p => p.ZoneId == 1363 && p.HasDirector && p.Party.Any(x => x.Job == job && me.Contains(x.Id))))
                toLoad.Add((s, $"{Path.GetFileNameWithoutExtension(path)}_{s.Ordinal:D3}"));
        }
        var runs = new ConcurrentBag<Run>();
        Parallel.ForEach(toLoad, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, j =>
        {
            PullReplay r;
            try { r = PullLoader.Load(j.S, null, registry); } catch { return; }
            var pl = r.Players.FirstOrDefault(p => me.Contains(p.Id) && p.Job == job);
            if (pl is null) return;
            var w = new World(r, pl.Id);
            var st = w.CastStarts(anchor.Name).ToList();
            if (st.Count < anchor.N) return;
            var a0 = st[anchor.N - 1];
            int w0 = a0 + (int)(win[0] * 1000), w1 = a0 + (int)(win[1] * 1000);
            if (w1 > r.LastMs || w.Dead(pl, a0)) return;
            var tr = pl.Track;
            var lines = new List<string>();
            if (dump)
            {
                foreach (var c in r.Casts.Where(c => !c.Source.IsPlayer && c.StartMs >= w0 && c.StartMs <= w1).GroupBy(c => (c.Name, c.StartMs / 500)).Select(g => g.First()).OrderBy(c => c.StartMs))
                    lines.Add($"    {(c.StartMs - a0) / 1000.0,6:+0.0;-0.0}  cast {c.Name} ({c.DurationMs / 1000.0:0.0} s) by {c.Source.Name}");
                foreach (var h in r.HeadMarkers.Where(h => h.Target.Id == pl.Id && h.T >= w0 && h.T <= w1))
                    lines.Add($"    {(h.T - a0) / 1000.0,6:+0.0;-0.0}  head marker {h.MarkerId:X} {h.Label}");
                foreach (var t in r.Tethers.Where(t => (t.Source.Id == pl.Id || t.Target.Id == pl.Id) && t.StartMs >= w0 && t.StartMs <= w1))
                    lines.Add($"    {(t.StartMs - a0) / 1000.0,6:+0.0;-0.0}  tether {t.TetherId:X} {t.Label} {t.Source.Name} -> {t.Target.Name}");
                foreach (var x in r.Statuses.Where(x => x.Target.Id == pl.Id && x.Source is { IsPlayer: false } && x.StartMs >= w0 && x.StartMs <= w1))
                    lines.Add($"    {(x.StartMs - a0) / 1000.0,6:+0.0;-0.0}  status {x.Name} ({x.StatusId}, {x.Duration:0} s) from {x.Source!.Name}");
                for (var k = Math.Max(0, tr.Floor(w0)); k < tr.Count && tr.T[k] <= w1; k++)
                    lines.Add($"    {(tr.T[k] - a0) / 1000.0,6:+0.0;-0.0}  pos {tr.X[k] - 100,6:0.0} {tr.Y[k] - 100,6:0.0}");
            }
            // The run: journeys from a standstill to a standstill (steps of MinStep or more, joined across pauses shorter than --pause,
            // so a run around the room's edge with short stops stays one journey); the one with the largest displacement.
            int k0 = Math.Max(1, tr.Floor(w0) + 1), k1 = tr.Floor(w1);
            if (k1 < k0) return;
            var journeys = new List<(int F, int L)>();
            int jf = -1, jl = -1;
            for (var k = k0; k <= k1; k++)
            {
                if ((tr.Flags[k] & PosFlags.Teleport) != 0 || Math.Sqrt(Math.Pow(tr.X[k] - tr.X[k - 1], 2) + Math.Pow(tr.Y[k] - tr.Y[k - 1], 2)) < MinStep) continue;
                if (jf >= 0 && tr.T[k - 1] - tr.T[jl] > pause) { journeys.Add((jf, jl)); jf = -1; }
                if (jf < 0) jf = k;
                jl = k;
            }
            if (jf >= 0) journeys.Add((jf, jl));
            if (journeys.Count == 0) return;
            var (f, l) = journeys.MaxBy(x => Math.Pow(tr.X[x.L] - tr.X[x.F - 1], 2) + Math.Pow(tr.Y[x.L] - tr.Y[x.F - 1], 2));
            var bd = Math.Sqrt(Math.Pow(tr.X[l] - tr.X[f - 1], 2) + Math.Pow(tr.Y[l] - tr.Y[f - 1], 2));
            double StepLen(int k) => Math.Sqrt(Math.Pow(tr.X[k] - tr.X[k - 1], 2) + Math.Pow(tr.Y[k] - tr.Y[k - 1], 2));
            double path = 0, movingMs = 0;
            for (var k = f; k <= l; k++) { var d = StepLen(k); path += d; if (d >= MinStep) movingMs += Math.Min(tr.T[k] - tr.T[k - 1], d / RunSpeed * 1000); }
            var mv = (First: f, Last: l, Start: Math.Max(tr.T[f - 1], tr.T[f] - StepLen(f) / RunSpeed * 1000), Path: path);
            var disp = bd;
            // Route: through the middle when a sample of the run comes within 6 y of the centre, else around the edge, clockwise or
            // counter-clockwise by the turn around the centre (compass angle, 0 = north = -y, clockwise positive).
            double minR = 99, sweep = 0;
            for (var k = f - 1; k <= l; k++)
            {
                minR = Math.Min(minR, Math.Sqrt(Math.Pow(tr.X[k] - 100, 2) + Math.Pow(tr.Y[k] - 100, 2)));
                if (k >= f)
                {
                    var d = Math.Atan2(tr.X[k] - 100, -(tr.Y[k] - 100)) - Math.Atan2(tr.X[k - 1] - 100, -(tr.Y[k - 1] - 100));
                    while (d > Math.PI) d -= 2 * Math.PI;
                    while (d < -Math.PI) d += 2 * Math.PI;
                    sweep += d;
                }
            }
            var route = minR < 6 ? "middle" : sweep < 0 ? "edge CCW" : "edge CW";
            var alsoOff = new Dictionary<string, double>();
            foreach (var (label, (name, n)) in also)
            {
                var s = w.CastStarts(name).ToList();
                if (s.Count >= n) alsoOff[label] = (mv.Start - s[n - 1]) / 1000.0;
            }
            if (casts)
            {
                foreach (var a in r.Actions.Where(a => a.Source.Id == pl.Id && a.T >= mv.Start - 2500 && a.T <= tr.T[mv.Last] + 1500).GroupBy(a => (a.ActionId, a.T / 100)).Select(g => g.First()))
                    lines.Add($"    {(a.T - mv.Start) / 1000.0,6:+0.0;-0.0}  {a.Name}");
                foreach (var c in r.Casts.Where(c => c.Source.Id == pl.Id && c.StartMs >= mv.Start - 2500 && c.StartMs <= tr.T[mv.Last] + 1500))
                    lines.Add($"    {(c.StartMs - mv.Start) / 1000.0,6:+0.0;-0.0}  cast start {c.Name}{(c.Resolution is null ? " (never landed)" : "")}");
                lines.Sort((x, y) => double.Parse(x.Trim().Split(' ')[0]).CompareTo(double.Parse(y.Trim().Split(' ')[0])));
            }
            if (dump)
                foreach (var b in w.Bosses.Where(b => b.IsPresent((int)mv.Start)).GroupBy(b => b.Name).Select(g => g.First()))
                {
                    Vector2 p0 = w.Pos(b, (int)mv.Start) - new Vector2(100, 100), p1 = w.Pos(b, tr.T[mv.Last]) - new Vector2(100, 100);
                    lines.Add($"    run start {b.Name} at ({p0.X:0.0}, {p0.Y:0.0}) {Side(p0.X + 100, p0.Y + 100)}, run end at ({p1.X:0.0}, {p1.Y:0.0}) {Side(p1.X + 100, p1.Y + 100)}; boss target then {w.BossTarget(b, (int)mv.Start)?.Name ?? "-"}");
                }
            runs.Add(new Run(j.Key, new DateTime(j.S.StartTicks).ToLocalTime(), a0, (mv.Start - a0) / 1000.0, (tr.T[mv.Last] - a0) / 1000.0, disp, mv.Path,
                new(tr.X[mv.First - 1] - 100, tr.Y[mv.First - 1] - 100), new(tr.X[mv.Last] - 100, tr.Y[mv.Last] - 100), alsoOff, lines, movingMs / 1000.0, route, Side(tr.X[mv.First - 1], tr.Y[mv.First - 1]), Side(tr.X[mv.Last], tr.Y[mv.Last])));
        });

        var R = runs.OrderBy(x => x.When).ToList();
        Console.WriteLine($"{R.Count} pulls reaching {Opt("anchor", "")} alive; window {win[0]:+0.0;-0.0} to {win[1]:+0.0} s from its cast start; the longest move per pull");
        Console.WriteLine($"{"pull",-30} {"anchor at",9} {"start",6} {"end",6} {"dur",5} {"moving",6} {"disp y",6} {"path y",6}  {"route",8} {"from",4} {"to",4} {"from (x, y)",14} -> {"to (x, y)",14}  " + string.Join(" ", also.Select(a => $"{"start from " + a.Label,22}")));
        foreach (var x in R)
        {
            Console.WriteLine($"{x.Key,-30} {TimeSpan.FromMilliseconds(x.AnchorMs):m\\:ss\\.f} {x.Start,6:+0.0;-0.0} {x.End,6:+0.0;-0.0} {x.End - x.Start,5:0.0} {x.Moving,6:0.0} {x.Disp,6:0.0} {x.Path,6:0.0}  {x.Route,8} {x.FromSide,4} {x.ToSide,4} ({x.From.X,5:0.0}, {x.From.Y,5:0.0}) -> ({x.To.X,5:0.0}, {x.To.Y,5:0.0})  " +
                string.Join(" ", also.Select(a => x.Also.TryGetValue(a.Label, out var v) ? $"{v,22:+0.0;-0.0}" : $"{"-",22}")));
            foreach (var l in x.Lines) Console.WriteLine(l);
        }
        if (R.Count == 0) return 0;
        string Q(IEnumerable<double> v)
        {
            var s = v.Order().ToList();
            double P(double p) => s[(int)Math.Round(p * (s.Count - 1))];
            return $"min {s[0]:+0.0;-0.0}  p10 {P(0.1):+0.0;-0.0}  median {P(0.5):+0.0;-0.0}  p90 {P(0.9):+0.0;-0.0}  max {s[^1]:+0.0;-0.0}";
        }
        var minDisp = double.Parse(Opt("min-disp", "15"));
        var big = R.Where(x => x.Disp >= minDisp).ToList();
        Console.WriteLine();
        Console.WriteLine($"runs of {minDisp:0} y or more: {big.Count} of {R.Count} pulls");
        foreach (var g in big.GroupBy(x => x.Route).OrderByDescending(g => g.Count()).Prepend(big.GroupBy(x => "all").First()))
        {
            var L = g.ToList();
            Console.WriteLine();
            Console.WriteLine($"{g.Key}: {L.Count} pulls ({L.Min(x => x.When):MM/dd}-{L.Max(x => x.When):MM/dd}); from -> to: " +
                string.Join(", ", L.GroupBy(x => $"{x.FromSide}->{x.ToSide}").OrderByDescending(h => h.Count()).Take(6).Select(h => $"{h.Key} {h.Count()}")));
            Console.WriteLine($"  start from {Opt("anchor", "")}: {Q(L.Select(x => x.Start))}");
            Console.WriteLine($"  end:   {Q(L.Select(x => x.End))}");
            Console.WriteLine($"  duration: {Q(L.Select(x => x.End - x.Start))}");
            Console.WriteLine($"  of it moving: {Q(L.Select(x => x.Moving))}");
            Console.WriteLine($"  displacement (y): {Q(L.Select(x => x.Disp))}  path (y): {Q(L.Select(x => x.Path))}");
            foreach (var a in also)
                if (L.Any(x => x.Also.ContainsKey(a.Label)))
                    Console.WriteLine($"  start from {a.Label}: {Q(L.Where(x => x.Also.ContainsKey(a.Label)).Select(x => x.Also[a.Label]))}");
            Console.WriteLine($"  anchor at (pull clock): {Q(L.Select(x => x.AnchorMs / 1000.0))}");
        }
        return 0;
    }
}
