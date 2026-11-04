using RaidReplay.Core.Encounters;
using RaidReplay.Core.Indexing;
using RaidReplay.Core.Loading;
using RaidReplay.Core.Model;
using RaidReplay.Core.Parsing;

/// <summary>
///     downtime &lt;log&gt;... [--zone=1363] [--min=30] [--detail]
///     When the party had nothing to hit, per pull, three ways:
///     - meter: Raid Replay's live DPS meter (TargetWatch), run through the same indexer the plugin uses;
///     - world: this sim's "no target" rule (World.Target: no boss present, targetable and not hidden), from the loaded pull;
///     - gaps: stretches of 3 s or more with no party damage on an enemy (the truth the two should match).
///     Prints every span for up to 3 pulls (or with --detail), else totals by phase.
/// </summary>
internal static class DowntimeCommands
{
    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "downtime";

    private sealed record Result(PullSummary Pull, DpsReading? Reading, List<(long From, long To)> Meter, List<long> Hits);

    public static int Main(string[] args, EncounterRegistry registry)
    {
        string Opt(string name, string def) => args.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
        var zone = uint.Parse(Opt("zone", "1363"));
        var minMs = int.Parse(Opt("min", "30")) * 1000L;
        var all = new List<Result>();
        foreach (var path in args.Skip(1).Where(a => !a.StartsWith("--")))
        {
            var meter = new DpsMeter();
            var tap = new Tap(meter);
            var indexer = new LogIndexer(path, new CompositeObserverFactory(new EncounterObserverFactory(registry), meter, new Recorder(meter, tap, all)), tap);
            var end = LogLineReader.Read(path, 0, new FileInfo(path).Length, indexer.Filter, indexer, CancellationToken.None, null).EndOffset;
            indexer.Finish(end);
        }

        var pulls = all.Where(r => r.Pull.ZoneId == zone && r.Pull.EndTicks - r.Pull.StartTicks >= minMs * TimeSpan.TicksPerMillisecond).ToList();
        var detail = pulls.Count <= 3 || args.Contains("--detail");
        var byPhase = new SortedDictionary<string, (long Meter, long World, long Gap, long Total)>();
        long gapsCovered = 0, gapsTotal = 0, meterOutside = 0;
        var uncovered = new List<string>();
        foreach (var r in pulls)
        {
            var p = r.Pull;
            string At(long t) => p.Phases.LastOrDefault(m => m.Ticks <= t)?.Name ?? "-";
            string Clock(long t) { var ms = (t - p.StartTicks) / TimeSpan.TicksPerMillisecond; return $"{ms / 60000}:{ms / 1000 % 60:00}.{ms / 100 % 10}"; }
            var gaps = new List<(long From, long To)>();
            for (var i = 1; i < r.Hits.Count; i++)
                if (r.Hits[i] - r.Hits[i - 1] >= 3 * TimeSpan.TicksPerSecond)
                    gaps.Add((r.Hits[i - 1], r.Hits[i]));
            var world = WorldRule(PullLoader.Load(p, null, registry), p);
            if (r.Hits.Count > 0)
                world = world.Select(s => (Math.Max(s.From, r.Hits[0]), Math.Min(s.To, r.Hits[^1]))).Where(s => s.Item2 > s.Item1).ToList();

            foreach (var (name, spans, k) in new[] { ("meter", r.Meter, 0), ("world", world, 1), ("gap", gaps, 2) })
                foreach (var (from, to) in spans)
                {
                    var e = byPhase.GetValueOrDefault(At(from));
                    var d = to - from;
                    byPhase[At(from)] = k switch { 0 => e with { Meter = e.Meter + d }, 1 => e with { World = e.World + d }, _ => e with { Gap = e.Gap + d } };
                }

            if (r.Hits.Count > 1)
                foreach (var m in p.Phases.Append(new PhaseMark { Ticks = r.Hits[^1] }).Zip(p.Phases.Skip(1).Append(new PhaseMark { Ticks = r.Hits[^1] })))
                {
                    var e = byPhase.GetValueOrDefault(m.First.Name);
                    if (m.First.Name.Length > 0)
                        byPhase[m.First.Name] = e with { Total = e.Total + Math.Max(0, m.Second.Ticks - Math.Max(m.First.Ticks, r.Hits[0])) };
                }

            // How much of the gaps the meter called downtime, and how much downtime fell outside any gap (damage was being dealt).
            static long Overlap(List<(long From, long To)> a, List<(long From, long To)> b) =>
                a.Sum(x => b.Sum(y => Math.Max(0, Math.Min(x.To, y.To) - Math.Max(x.From, y.From))));
            var covered = Overlap(gaps, r.Meter);
            gapsCovered += covered;
            gapsTotal += gaps.Sum(s => s.To - s.From);
            meterOutside += r.Meter.Sum(s => s.To - s.From) - covered;
            foreach (var g in gaps.Where(g => Overlap([g], r.Meter) < (g.To - g.From) / 2))
                uncovered.Add($"#{p.Ordinal} {Clock(g.From)} {(g.To - g.From) / 1e7:N1} s {At(g.From)}");

            var d0 = r.Reading;
            Console.WriteLine($"#{p.Ordinal} {p.Outcome} {(p.EndTicks - p.StartTicks) / 1e7:N0} s, furthest {p.FurthestPhase}: elapsed {d0?.ElapsedMs / 1000.0:N1} s, " +
                              $"meter downtime {d0?.DowntimeMs / 1000.0:N1} s, world rule {world.Sum(s => s.To - s.From) / 1e7:N1} s, damage gaps {gaps.Sum(s => s.To - s.From) / 1e7:N1} s");
            if (!detail)
                continue;
            var rows = r.Meter.Select(s => (s.From, s.To, "meter")).Concat(world.Select(s => (s.From, s.To, "world"))).Concat(gaps.Select(s => (s.From, s.To, "gap  ")))
                          .OrderBy(x => x.From).ThenBy(x => x.Item3);
            foreach (var (from, to, what) in rows)
                Console.WriteLine($"    {what} {Clock(from),8} – {Clock(to),8}  {(to - from) / 1e7,5:N1} s   {At(from)}");
        }

        Console.WriteLine();
        Console.WriteLine($"{pulls.Count} pulls; downtime by phase (meter / world rule / damage gaps of 3 s+ / time in phase):");
        foreach (var (ph, (m, w, g, t)) in byPhase)
            Console.WriteLine($"  {ph,-26} {m / 1e7,8:N1} s {w / 1e7,8:N1} s {g / 1e7,8:N1} s {t / 1e7,9:N0} s");
        Console.WriteLine($"damage gaps: {gapsTotal / 1e7:N1} s, {gapsCovered / 1e7:N1} s of it meter downtime; meter downtime outside any gap: {meterOutside / 1e7:N1} s");
        Console.WriteLine($"gaps mostly not downtime ({uncovered.Count}):");
        foreach (var u in uncovered.Take(40))
            Console.WriteLine($"    {u}");
        return 0;
    }

    /// <summary>World.Target's rule: no boss present, targetable and not hidden; as spans in ticks, sampled every 100 ms.</summary>
    private static List<(long From, long To)> WorldRule(PullReplay r, PullSummary p)
    {
        var bosses = r.Actors.Where(a => a.Kind == ActorKind.Boss).ToList();
        var spans = new List<(long From, long To)>();
        long? since = null;
        for (var t = 0; t <= (p.EndTicks - p.StartTicks) / TimeSpan.TicksPerMillisecond; t += 100)
        {
            var up = bosses.Any(b => b.IsPresent(t) && b.IsTargetable(t) && !b.IsHidden(t));
            var ticks = r.StartTicks + t * TimeSpan.TicksPerMillisecond;
            if (!up && since == null) since = ticks;
            else if (up && since != null) { spans.Add((since.Value, ticks)); since = null; }
        }

        if (since != null) spans.Add((since.Value, p.EndTicks));
        return spans;
    }

    /// <summary>The meter's tap, also noting when the party damaged an enemy.</summary>
    private sealed class Tap(DpsMeter meter) : ILineTap
    {
        public List<long>? Hits;

        public LineTypeSet Types => ((ILineTap)meter).Types;

        public void OnLine(int type, scoped in LineFields f, long ticks, WorldStateTracker world)
        {
            meter.OnLine(type, f, ticks, world);
            if (Hits == null)
                return;
            if (type is LineType.Ability or LineType.AoeAbility)
            {
                if (!Party(f.Hex(F21.SourceId), f.Hex(F21.OwnerId), world) || !Enemy(f.Hex(F21.TargetId), world))
                    return;
                for (var i = 0; i < F21.EffectPairs; i++)
                    if (Effects.IsDamage(Effects.Type(f.Hex(F21.FirstEffect + i * 2))) && Effects.Amount(f.Hex(F21.FirstEffect + i * 2 + 1)) > 0)
                    {
                        Hits.Add(ticks);
                        return;
                    }
            }
            else if (type == LineType.DoTHoT && f.Is(F24.Which, "DoT"u8) && f.Hex(F24.Amount) > 0 &&
                     Party(f.Hex(F24.SourceId), 0, world) && Enemy(f.Hex(F24.TargetId), world))
            {
                Hits.Add(ticks);
            }
        }

        private static bool Party(uint id, uint owner, WorldStateTracker w) =>
            id >> 28 == 1 || owner >> 28 == 1 || w.Combatants.TryGetValue(id, out var c) && c.OwnerId >> 28 == 1;

        private static bool Enemy(uint id, WorldStateTracker w) =>
            id >> 28 == 4 && !(w.Combatants.TryGetValue(id, out var c) && c.OwnerId >> 28 == 1);
    }

    /// <summary>Starts the tap's hit list at each pull and keeps the meter's reading when it ends.</summary>
    private sealed class Recorder(DpsMeter meter, Tap tap, List<Result> results) : IPullObserverFactory, IPullObserver
    {
        private PullSummary? pull;

        public LineTypeSet Types { get; } = new();

        public IPullObserver? Create(uint zoneId, PullSummary p, WorldStateTracker world)
        {
            pull = p;
            tap.Hits = [];
            return this;
        }

        public void OnLine(int type, scoped in LineFields f, long ticks, WorldStateTracker world)
        {
        }

        public void Complete(PullSummary p)
        {
            results.Add(new Result(p, meter.Read(), meter.Downtime().ToList(), tap.Hits ?? []));
            tap.Hits = null;
            pull = null;
        }
    }
}
