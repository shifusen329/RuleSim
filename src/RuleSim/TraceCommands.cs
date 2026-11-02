using System.Buffers;
using System.Text;
using System.Text.Json;
using RaidReplay.Core.Parsing;

/// <summary>
///     Extracts "response bundles" from raw ACT network logs: for each sampled player action, the action (21/22 lines of
///     one sequence), the state just before it, and everything logged in response to it.
///
///   traces &lt;log|dir|glob&gt; [...] --out=DIR [--owner-cap=40] [--other-cap=12] [--party-statuses=caster|all] [--scan-only]
///
/// statusesBefore lists every status of the caster and of each target. Party members that are neither only list the
/// statuses the caster applied (a dance partner's), and are left out when there are none, since their full lists are
/// most of the output; --party-statuses=all keeps those whole.
///
/// One JSONL file per log. Samples are capped per (caster job, action), separately for the log owner and for others,
/// spread evenly over all logs in command-line order; <see cref="AlwaysKeep" /> actions are never capped, except the
/// non-owner ones in <see cref="NonOwnerCaps" />. Only combat jobs are sampled. Two passes:
/// the first only lists candidates (--scan-only prints them per job and action and stops), the second rebuilds the same
/// state and writes the selected bundles.
/// </summary>
internal static class TraceCommands
{
    // Technical Finish lands as two actions: 16193-16196 deal the damage, then 33215-33218 (same name) give the party its
    // buff about 0.7 s later, under their own sequence. UNVERIFIED: 33215/33216 as the Single/Double follow-ups.
    private static readonly HashSet<uint> AlwaysKeep =
    [
        15997, 15998, 15999, 16000, 16001, 16002, 16003, 16004, 16191, 16192, 16193, 16194, 16195, 16196, 33215, 33216,
        33217, 33218, 25789, 16014, 16483, 16485, 16486, 25782, 36967, 36968,
    ];

    // Non-owner plain steps say nothing beyond "a step was pressed" (no gauge), so 10 each (40 in all) is enough.
    private static readonly Dictionary<uint, int> NonOwnerCaps = new()
    {
        [15999] = 10, [16000] = 10, [16001] = 10, [16002] = 10,
        [16485] = 40, [16486] = 40, [25782] = 40, [36967] = 40, [36968] = 40,
    };

    private static bool IsCombatJob(byte job) => job is >= 19 and <= 25 or 27 or 28 or >= 30 and <= 35 or >= 37 and <= 42;

    private static readonly Dictionary<byte, string> JobNames = new()
    {
        [19] = "PLD", [20] = "MNK", [21] = "WAR", [22] = "DRG", [23] = "BRD", [24] = "WHM", [25] = "BLM", [27] = "SMN",
        [28] = "SCH", [30] = "NIN", [31] = "MCH", [32] = "DRK", [33] = "AST", [34] = "SAM", [35] = "RDM", [36] = "BLU",
        [37] = "GNB", [38] = "DNC", [39] = "RPR", [40] = "SGE", [41] = "VPR", [42] = "PCT",
    };

    private static string JobName(byte job) => JobNames.TryGetValue(job, out var n) ? n : $"job{job}";

    public static bool Handles(string[] args) => args.Length > 0 && args[0] == "traces";

    public static int Run(string[] args)
    {
        string Opt(string name, string def) => args.FirstOrDefault(a => a.StartsWith($"--{name}=")) is { } a ? a[(name.Length + 3)..] : def;
        var logs = args.Skip(1).Where(a => !a.StartsWith("--")).SelectMany(Expand).ToList();
        var outDir = Opt("out", "");
        if (logs.Count == 0 || outDir == "")
        {
            Console.WriteLine("usage: RuleSim traces <log|dir|glob> [<log>...] --out=DIR [--owner-cap=40] [--other-cap=12] [--party-statuses=caster|all] [--scan-only]");
            return 1;
        }

        var ownerCap = int.Parse(Opt("owner-cap", "40"));
        var otherCap = int.Parse(Opt("other-cap", "12"));
        var allParty = Opt("party-statuses", "caster") == "all";

        var candidates = new List<Candidate>();
        for (var i = 0; i < logs.Count; i++)
        {
            var sc = new Scanner(i, Stem(logs[i]), null, candidates, null, allParty);
            sc.Run(logs[i]);
            Console.WriteLine($"scan  {Stem(logs[i])}: {sc.Actions} player actions, {sc.Candidates} with a known job, {sc.Warnings} odd lines");
        }

        var keep = Select(candidates, ownerCap, otherCap);
        if (args.Contains("--scan-only"))
        {
            foreach (var g in candidates.GroupBy(c => (c.Job, c.Action, c.Owner)).OrderBy(g => g.Key.Job).ThenBy(g => g.Key.Owner).ThenBy(g => g.Key.Action))
                Console.WriteLine($"{JobName(g.Key.Job),-5} {(g.Key.Owner ? "owner" : "other")} {g.Key.Action,6} {g.Count(),6}{(AlwaysKeep.Contains(g.Key.Action) ? " always" : "")}");
            Console.WriteLine($"would keep {keep.Count} of {candidates.Count}");
            return 0;
        }

        Directory.CreateDirectory(outDir);
        long total = 0;
        for (var i = 0; i < logs.Count; i++)
        {
            var path = Path.Combine(outDir, Stem(logs[i]) + ".jsonl");
            if (!keep.Any(k => k.Log == i))
            {
                File.Delete(path);
                Console.WriteLine($"skip  {Path.GetFileName(path)}: no bundles");
                continue;
            }

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var sc = new Scanner(i, Stem(logs[i]), keep, null, fs, allParty);
            sc.Run(logs[i]);
            total += fs.Length;
            Console.WriteLine($"write {Path.GetFileName(path)}: {sc.Written} bundles, {fs.Length / 1024.0:N0} KB");
        }

        Console.WriteLine($"total {keep.Count} bundles, {total / 1024.0 / 1024.0:N2} MB");
        Console.WriteLine();
        Console.WriteLine("job   owner  other   (kept / candidates)");
        foreach (var g in candidates.GroupBy(c => c.Job).OrderBy(g => g.Key))
        {
            int Kept(bool owner) => g.Count(c => c.Owner == owner && keep.Contains(c.Key));
            Console.WriteLine($"{JobName(g.Key),-5} {Kept(true),5}/{g.Count(c => c.Owner),-6} {Kept(false),5}/{g.Count(c => !c.Owner),-6}");
        }

        Console.WriteLine();
        for (var i = 0; i < logs.Count; i++)
        {
            var own = candidates.Where(c => c.Key.Log == i && c.Owner).GroupBy(c => c.Job).OrderByDescending(g => g.Count());
            Console.WriteLine($"{Stem(logs[i])}: owner jobs {string.Join(", ", own.Select(g => $"{JobName(g.Key)} ({g.Count()} actions)"))}");
            var lv = candidates.Where(c => c.Key.Log == i).GroupBy(c => c.Level).OrderBy(g => g.Key ?? 0);
            Console.WriteLine($"    levels (kept/actions): {string.Join(", ", lv.Select(g => $"{g.Key?.ToString() ?? "null"} {g.Count(c => keep.Contains(c.Key))}/{g.Count()}"))}");
        }

        return 0;
    }

    private static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    private static IEnumerable<string> Expand(string arg)
    {
        if (Directory.Exists(arg)) return Directory.GetFiles(arg, "*.log").OrderBy(f => f, StringComparer.Ordinal);
        if (!arg.Contains('*')) return [arg];
        var dir = Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
        return Directory.GetFiles(dir, Path.GetFileName(arg)).OrderBy(f => f, StringComparer.Ordinal);
    }

    private static HashSet<Key> Select(List<Candidate> all, int ownerCap, int otherCap)
    {
        var keep = new HashSet<Key>();
        foreach (var g in all.GroupBy(c => (c.Job, c.Action, c.Owner)))
        {
            var list = g.OrderBy(c => c.Key.Log).ThenBy(c => c.Key.T).ToList();
            var cap = !g.Key.Owner && NonOwnerCaps.TryGetValue(g.Key.Action, out var c) ? c
                      : AlwaysKeep.Contains(g.Key.Action) ? int.MaxValue
                      : g.Key.Owner ? ownerCap : otherCap;
            if (list.Count <= cap)
            {
                foreach (var k in list) keep.Add(k.Key);
                continue;
            }

            for (var i = 0; i < cap; i++)
                keep.Add(list[(int)((i + 0.5) * list.Count / cap)].Key);
        }

        return keep;
    }

    private readonly record struct Key(int Log, uint Source, uint Seq, long T);

    private sealed record Candidate(Key Key, byte Job, uint Action, bool Owner, byte? Level);

    private abstract class Ev
    {
        public long T;
    }

    private sealed class ActEv : Ev
    {
        public string Ts = "";
        public uint Src, Action, Target, Seq;
        public readonly uint[] Flags = new uint[8], Values = new uint[8];
        public float TX, TY, SX, SY, Lock;
        public long THp, SHp;
        public int TIndex, TCount;
    }

    private sealed class StatusEv : Ev
    {
        public bool Gain;
        public uint Status, Src, Target, Param;
        public float Duration;
    }

    private sealed class GaugeEv : Ev
    {
        public uint Actor;
        public byte[] Bytes = [];
    }

    private sealed class ResultEv : Ev
    {
        public uint Actor, Seq;
        public long Hp, MaxHp, Mp, Shield;
        public float X, Y;
        public string? Field15;
        public readonly List<(uint Raw, uint Param, float Duration, uint Src)> Entries = [];
    }

    private sealed class ListEv : Ev
    {
        public uint Actor;
        public byte Job, Level;
        public long Hp;
        public float X, Y;
        public readonly List<(uint Status, uint Param, float Duration, uint Src)> Entries = [];
    }

    private sealed class CastEv : Ev
    {
        public uint Src, Action;
        public float CastTime, X, Y;
    }

    private sealed class SimpleEv : Ev
    {
        public int Type;
        public uint Id;
        public byte Job, Level;
        public long Hp = -1;
        public float X = float.NaN, Y = float.NaN;
        public uint[] Ids = [];
    }

    private sealed class Pending
    {
        public long T;
        public string Ts = "";
        public uint Src, Seq, Action;
        public byte Job;
        public byte? Level;
        public bool Owner, Selected;
        public float Lock, CastTime, CX, CY;
        public int TCount;
        public readonly List<ActEv> Lines = [];
        public List<(uint Action, double Ago)> Recent = [];
        public string? GaugeBefore, GaugeAfter;
        public readonly List<(StatusEv S, long Dt)> Gained = [], Lost = [];
        public readonly List<ResultEv> Results = [];
        public readonly Dictionary<uint, List<(uint Status, uint Param, double? Remaining, uint Src)>> StatusesBefore = [];
        public List<(uint Id, byte Job, (float X, float Y)? Pos, bool Alive)> Party = [];

        public bool Involves(uint id) => id == Src || Lines.Any(l => l.Target == id);
    }

    private sealed class StatusEntry
    {
        public uint Status, Src, Param;
        public long Expiry;
    }

    private sealed class Scanner(int logIdx, string stem, HashSet<Key>? keep, List<Candidate>? candidates, Stream? output, bool allParty)
        : ILineConsumer
    {
        private const uint NoTarget = 0xE0000000;
        // A few lines the client logs for the same server bundle land a millisecond after the 21 they belong to.
        private const long SameMoment = 10 * TimeSpan.TicksPerMillisecond;
        private const long ResultWindow = 3 * TimeSpan.TicksPerSecond;
        private const long RecentWindow = 30 * TimeSpan.TicksPerSecond;
        private const long Permanent = long.MaxValue;

        private static readonly LineTypeSet Filter = new(LineType.ChangePrimaryPlayer, LineType.AddCombatant, LineType.PartyList,
            LineType.PlayerStats, LineType.StartsCasting, LineType.Ability, LineType.AoeAbility, LineType.CancelAbility, LineType.Death,
            LineType.StatusAdd, LineType.StatusRemove, LineType.Gauge, LineType.EffectResult, LineType.StatusList, LineType.UpdateHp,
            LineType.ActorMove);

        private readonly List<Ev> batch = [];
        private long batchT;

        private readonly Dictionary<uint, byte> jobs = [];
        private readonly Dictionary<uint, byte> levels = [];
        private readonly Dictionary<uint, (float X, float Y)> pos = [];
        private readonly Dictionary<uint, long> hp = [];
        private readonly Dictionary<uint, Dictionary<(uint, uint), StatusEntry>> statuses = [];
        private readonly Dictionary<uint, CastEv> lastCast = [];
        private readonly Dictionary<uint, List<(long T, uint Action)>> history = [];
        private uint ownerId;
        private uint[] party = [];
        private byte[]? lastGauge;
        private uint lastGaugeActor;

        private readonly Dictionary<(uint, uint), Pending> open = [];
        private readonly List<Pending> pendings = [];
        private readonly Dictionary<uint, List<Pending>> bySeq = [];

        public int Actions, Candidates, Written, Warnings;

        public void Run(string path)
        {
            LogLineReader.Read(path, 0, -1, Filter, this);
            if (batch.Count > 0) Flush();
            FinalizeBefore(long.MaxValue);
        }

        public bool OnLine(long offset, int type, ReadOnlySpan<byte> line)
        {
            Span<int> buf = stackalloc int[256];
            var f = new LineFields(line, buf);
            var t = f.Ticks;
            if (t == 0) return true;
            var ev = Parse(type, f, t);
            if (ev is null) return true;
            // Lines logged out of order with an earlier timestamp join the current batch.
            if (batch.Count > 0 && t > batchT) Flush();
            if (batch.Count == 0) batchT = t;
            batch.Add(ev);
            return true;
        }

        private static float Hf(uint bits) => BitConverter.UInt32BitsToSingle(bits);

        private static long Num(in LineFields f, int i) => f.Has(i) && !f.IsEmpty(i) ? f.Long(i) : -1;

        private static float Flt(in LineFields f, int i) => f.Has(i) ? f.Float(i) : float.NaN;

        private Ev? Parse(int type, in LineFields f, long t)
        {
            switch (type)
            {
                case LineType.Ability or LineType.AoeAbility:
                {
                    if (f.DataCount <= F21.TargetCount) { Warnings++; return null; }
                    var a = new ActEv
                    {
                        T = t, Ts = Encoding.ASCII.GetString(f[1]), Src = f.Hex(F21.SourceId), Action = f.Hex(F21.ActionId),
                        Target = f.Hex(F21.TargetId), Seq = f.Hex(F21.Sequence), TX = Flt(f, F21.TargetX), TY = Flt(f, F21.TargetY),
                        SX = Flt(f, F21.SourceX), SY = Flt(f, F21.SourceY), THp = Num(f, F21.TargetHp), SHp = Num(f, F21.SourceHp),
                        TIndex = (int)Num(f, F21.TargetIndex), TCount = (int)Num(f, F21.TargetCount), Lock = Flt(f, F21.AnimationLock),
                    };
                    for (var i = 0; i < 8; i++)
                    {
                        a.Flags[i] = f.Hex(F21.FirstEffect + 2 * i);
                        a.Values[i] = f.Hex(F21.FirstEffect + 2 * i + 1);
                    }

                    return a;
                }
                case LineType.StatusAdd or LineType.StatusRemove:
                    return new StatusEv
                    {
                        T = t, Gain = type == LineType.StatusAdd, Status = f.Hex(F26.StatusId), Duration = Flt(f, F26.Duration),
                        Src = f.Hex(F26.SourceId), Target = f.Hex(F26.TargetId), Param = f.Hex(F26.Stacks),
                    };
                case LineType.Gauge:
                {
                    var g = new GaugeEv { T = t, Actor = f.Hex(2), Bytes = new byte[16] };
                    for (var w = 0; w < 4; w++)
                    {
                        var word = f.Hex(3 + w);
                        for (var b = 0; b < 4; b++) g.Bytes[4 * w + b] = (byte)(word >> (8 * b));
                    }

                    return g;
                }
                case LineType.EffectResult:
                {
                    var r = new ResultEv
                    {
                        T = t, Actor = f.Hex(F37.Id), Seq = f.Hex(F37.Sequence), Hp = Num(f, F37.Hp), MaxHp = Num(f, F37.MaxHp),
                        Mp = Num(f, F37.Mp), Shield = Num(f, F37.Shield), X = Flt(f, F37.X), Y = Flt(f, F37.Y),
                    };
                    // Enemies get a short form that ends at the heading: no field 15, no status entries.
                    if (f.DataCount > 15) r.Field15 = Encoding.ASCII.GetString(f[15]);
                    if (f.DataCount > 18)
                    {
                        var n = (int)f.Hex(18);
                        for (var k = 0; k < n; k++)
                        {
                            var b = 19 + 4 * k;
                            if (b + 3 >= f.DataCount) { Warnings++; break; }
                            r.Entries.Add((f.Hex(b), f.Hex(b + 1), Hf(f.Hex(b + 2)), f.Hex(b + 3)));
                        }
                    }

                    return r;
                }
                case LineType.StatusList:
                {
                    var l = new ListEv
                    {
                        // jobLevelData: sync cap (0 when unsynced), real level, synced level, job; one byte each.
                        T = t, Actor = f.Hex(F38.Id), Job = (byte)f.Hex(4), Level = (byte)(f.Hex(4) >> 8), Hp = Num(f, F38.Hp),
                        X = Flt(f, F38.X), Y = Flt(f, F38.Y),
                    };
                    if ((f.DataCount - 18) % 3 != 0) Warnings++;
                    for (var i = 18; i + 2 < f.DataCount; i += 3)
                    {
                        var packed = f.Hex(i);
                        if ((packed & 0xFFFF) == 0) continue;
                        l.Entries.Add((packed & 0xFFFF, packed >> 16, Hf(f.Hex(i + 1)), f.Hex(i + 2)));
                    }

                    return l;
                }
                case LineType.StartsCasting:
                    return new CastEv
                    {
                        T = t, Src = f.Hex(F20.SourceId), Action = f.Hex(F20.ActionId), CastTime = Flt(f, F20.CastTime),
                        X = Flt(f, F20.X), Y = Flt(f, F20.Y),
                    };
                case LineType.CancelAbility:
                    return new SimpleEv { T = t, Type = type, Id = f.Hex(F23.SourceId) };
                case LineType.AddCombatant:
                    return new SimpleEv
                    {
                        T = t, Type = type, Id = f.Hex(F03.Id), Job = (byte)f.Hex(F03.Job), Level = (byte)f.Hex(F03.Level), Hp = Num(f, F03.Hp),
                        X = Flt(f, F03.X),
                        Y = Flt(f, F03.Y),
                    };
                case LineType.ChangePrimaryPlayer:
                    return new SimpleEv { T = t, Type = type, Id = f.Hex(F02.Id) };
                case LineType.PartyList:
                {
                    var n = (int)Num(f, F11.Count);
                    var ids = new List<uint>();
                    for (var i = 0; i < n && F11.FirstId + i < f.DataCount; i++) ids.Add(f.Hex(F11.FirstId + i));
                    return new SimpleEv { T = t, Type = type, Ids = ids.ToArray() };
                }
                case LineType.PlayerStats:
                    return new SimpleEv { T = t, Type = type, Job = (byte)Num(f, F12.Job) };
                case LineType.UpdateHp:
                    return new SimpleEv { T = t, Type = type, Id = f.Hex(F39.Id), Hp = Num(f, F39.Hp), X = Flt(f, F39.X), Y = Flt(f, F39.Y) };
                case LineType.ActorMove:
                    return new SimpleEv { T = t, Type = type, Id = f.Hex(F270.Id), X = Flt(f, F270.X), Y = Flt(f, F270.Y) };
                case LineType.Death:
                    return new SimpleEv { T = t, Type = type, Id = f.Hex(F25.TargetId), Hp = 0 };
                default:
                    return null;
            }
        }

        private void Flush()
        {
            var created = new List<Pending>();
            foreach (var ev in batch)
                if (ev is ActEv a) Group(a, created);
            // Snapshots are taken before this batch is applied: lines sharing the action's timestamp are its response.
            foreach (var p in created) Snapshot(p);
            foreach (var ev in batch)
                if (ev is ResultEv r) AttachResult(r);
            foreach (var ev in batch)
            {
                if (ev is StatusEv s) AttachStatus(s);
                else if (ev is GaugeEv g) AttachGauge(g);
            }

            foreach (var ev in batch) Apply(ev);
            batch.Clear();
            FinalizeBefore(batchT - ResultWindow);
        }

        private void Group(ActEv a, List<Pending> created)
        {
            if (a.Src >> 24 != 0x10 || a.Action is 7 or 8 || a.Action > 0xFFFF) return;
            if (open.TryGetValue((a.Src, a.Seq), out var g) && Math.Abs(a.T - g.T) < TimeSpan.TicksPerSecond)
            {
                g.Lines.Add(a);
                if (g.Selected && !created.Contains(g) && a.Target != NoTarget && a.Target != g.Src)
                    g.StatusesBefore[a.Target] = Statuses(a.Target, g.T);
                return;
            }

            Actions++;
            var hist = history.TryGetValue(a.Src, out var h) ? h : history[a.Src] = [];
            var recent = new List<(uint, double)>();
            for (var i = hist.Count - 1; i >= 0 && recent.Count < 12; i--)
            {
                if (a.T - hist[i].T > RecentWindow) break;
                recent.Add((hist[i].Action, (a.T - hist[i].T) / 1e7));
            }

            hist.Add((a.T, a.Action));
            if (hist.Count > 64) hist.RemoveRange(0, 32);

            var job = jobs.GetValueOrDefault(a.Src);
            if (!IsCombatJob(job)) job = 0;
            var key = new Key(logIdx, a.Src, a.Seq, a.T);
            var p = new Pending
            {
                T = a.T, Ts = a.Ts, Src = a.Src, Seq = a.Seq, Action = a.Action, Job = job, Owner = a.Src == ownerId,
                Level = levels.TryGetValue(a.Src, out var lv) ? lv : null,
                Selected = job != 0 && keep?.Contains(key) == true, Lock = a.Lock, CX = a.SX, CY = a.SY, TCount = a.TCount,
                Recent = recent,
            };
            p.Lines.Add(a);
            open[(a.Src, a.Seq)] = p;
            if (job == 0) return;
            Candidates++;
            candidates?.Add(new Candidate(key, job, a.Action, p.Owner, p.Level));
            if (!p.Selected) return;

            if (lastCast.TryGetValue(a.Src, out var c) && c.Action == a.Action)
            {
                var end = c.T + (long)(c.CastTime * 1e7);
                if (a.T - end is > -TimeSpan.TicksPerSecond and < 2 * TimeSpan.TicksPerSecond)
                {
                    p.CastTime = c.CastTime;
                    lastCast.Remove(a.Src);
                }
            }

            created.Add(p);
            pendings.Add(p);
            (bySeq.TryGetValue(a.Seq, out var l) ? l : bySeq[a.Seq] = []).Add(p);
        }

        private void Snapshot(Pending p)
        {
            foreach (var id in p.Lines.Select(l => l.Target).Prepend(p.Src))
                if (id != NoTarget && !p.StatusesBefore.ContainsKey(id))
                    p.StatusesBefore[id] = Statuses(id, p.T);
            foreach (var id in party)
            {
                if (p.StatusesBefore.ContainsKey(id)) continue;
                var list = allParty ? Statuses(id, p.T) : Statuses(id, p.T).Where(s => s.Item4 == p.Src).ToList();
                if (allParty || list.Count > 0) p.StatusesBefore[id] = list;
            }
            p.Party = party.Select(id => (id, jobs.GetValueOrDefault(id), pos.TryGetValue(id, out var xy) ? xy : ((float, float)?)null,
                                          !hp.TryGetValue(id, out var v) || v > 0)).ToList();
            if (p.Owner && lastGauge is not null && lastGaugeActor == p.Src && lastGauge[0] == p.Job)
                p.GaugeBefore = Convert.ToHexString(lastGauge);
        }

        private List<(uint, uint, double?, uint)> Statuses(uint actor, long t)
        {
            if (!statuses.TryGetValue(actor, out var d)) return [];
            return d.Values.Where(e => e.Expiry > t).OrderBy(e => e.Status).ThenBy(e => e.Src)
                    .Select(e => (e.Status, e.Param, e.Expiry == Permanent ? (double?)null : Math.Round((e.Expiry - t) / 1e7, 2), e.Src))
                    .ToList();
        }

        private void AttachResult(ResultEv r)
        {
            if (!bySeq.TryGetValue(r.Seq, out var list)) return;
            Pending? best = null;
            foreach (var p in list)
                if (r.T >= p.T && r.T - p.T <= ResultWindow && (best is null || p.T > best.T))
                    best = p;
            best?.Results.Add(r);
        }

        private void AttachStatus(StatusEv s)
        {
            foreach (var p in pendings)
            {
                if (s.T < p.T) continue;
                var atAction = s.T - p.T <= SameMoment;
                // The client also logs unrelated status changes it notices while handling a 37, so only take those the 37 lists.
                var atResult = p.Results.Any(r => r.T == s.T && r.Actor == s.Target && r.Entries.Any(e => (e.Raw & 0xFFFF) == s.Status));
                if (s.Gain)
                {
                    if (s.Src == p.Src && (atAction || atResult)) p.Gained.Add((s, s.T - p.T));
                }
                else if (atAction && (s.Src == p.Src || p.Involves(s.Target)) || atResult)
                {
                    p.Lost.Add((s, s.T - p.T));
                }
            }
        }

        private void AttachGauge(GaugeEv g)
        {
            foreach (var p in pendings)
                if (p.Owner && g.Actor == p.Src && g.T >= p.T && g.T - p.T <= SameMoment)
                    p.GaugeAfter = Convert.ToHexString(g.Bytes);
        }

        private void SetPos(uint id, float x, float y)
        {
            if (id != NoTarget && id != 0 && !float.IsNaN(x) && !float.IsNaN(y)) pos[id] = (x, y);
        }

        private void SetHp(uint id, long v)
        {
            if (id != NoTarget && id != 0 && v >= 0) hp[id] = v;
        }

        private void SetJob(uint id, byte job)
        {
            if (job != 0 && id >> 24 == 0x10) jobs[id] = job;
        }

        // The newest of 38 and 03 wins: a 03 newer than the last 38 means the actor was re-added, e.g. on entering synced content.
        private void SetLevel(uint id, byte level)
        {
            if (level != 0 && id >> 24 == 0x10) levels[id] = level;
        }

        private Dictionary<(uint, uint), StatusEntry> StatusesOf(uint id) =>
            statuses.TryGetValue(id, out var d) ? d : statuses[id] = [];

        private static long Expiry(long t, float duration) =>
            float.IsNaN(duration) || duration == 0 || duration >= 9999 ? Permanent : t + (long)(Math.Abs(duration) * 1e7);

        private void Apply(Ev ev)
        {
            switch (ev)
            {
                case ActEv a:
                    SetPos(a.Src, a.SX, a.SY);
                    SetPos(a.Target, a.TX, a.TY);
                    SetHp(a.Src, a.SHp);
                    SetHp(a.Target, a.THp);
                    break;
                case StatusEv s:
                {
                    var d = StatusesOf(s.Target);
                    if (s.Gain)
                    {
                        d[(s.Status, s.Src)] = new StatusEntry { Status = s.Status, Src = s.Src, Param = s.Param, Expiry = Expiry(s.T, s.Duration) };
                    }
                    else if (!d.Remove((s.Status, s.Src)))
                    {
                        foreach (var k in d.Keys.Where(k => k.Item1 == s.Status).ToList()) d.Remove(k);
                    }

                    break;
                }
                case GaugeEv g:
                    lastGauge = g.Bytes;
                    lastGaugeActor = g.Actor;
                    SetJob(g.Actor, g.Bytes[0]);
                    break;
                case ResultEv r:
                {
                    SetHp(r.Actor, r.Hp);
                    SetPos(r.Actor, r.X, r.Y);
                    var d = StatusesOf(r.Actor);
                    foreach (var (raw, param, duration, src) in r.Entries)
                        if ((raw & 0xFFFF) != 0 && duration > 0)
                            d[(raw & 0xFFFF, src)] = new StatusEntry { Status = raw & 0xFFFF, Src = src, Param = param, Expiry = r.T + (long)(duration * 1e7) };
                    break;
                }
                case ListEv l:
                {
                    SetJob(l.Actor, l.Job);
                    SetLevel(l.Actor, l.Level);
                    SetHp(l.Actor, l.Hp);
                    SetPos(l.Actor, l.X, l.Y);
                    // A 38 is the actor's whole status list; some list every slot, empty ones as 0|0|0.
                    var d = new Dictionary<(uint, uint), StatusEntry>();
                    foreach (var (status, param, duration, src) in l.Entries)
                        d[(status, src)] = new StatusEntry { Status = status, Src = src, Param = param, Expiry = Expiry(l.T, duration) };
                    statuses[l.Actor] = d;
                    break;
                }
                case CastEv c:
                    lastCast[c.Src] = c;
                    SetPos(c.Src, c.X, c.Y);
                    break;
                case SimpleEv e:
                    switch (e.Type)
                    {
                        case LineType.CancelAbility: lastCast.Remove(e.Id); break;
                        case LineType.ChangePrimaryPlayer: ownerId = e.Id; break;
                        case LineType.PartyList: party = e.Ids; break;
                        case LineType.PlayerStats: SetJob(ownerId, e.Job); break;
                        default:
                            SetJob(e.Id, e.Job);
                            SetLevel(e.Id, e.Level);
                            SetHp(e.Id, e.Hp);
                            SetPos(e.Id, e.X, e.Y);
                            break;
                    }

                    break;
            }
        }

        private void FinalizeBefore(long limit)
        {
            for (var i = 0; i < pendings.Count; i++)
            {
                var p = pendings[i];
                if (p.T >= limit) continue;
                Write(p);
                pendings.RemoveAt(i--);
                if (bySeq.TryGetValue(p.Seq, out var l) && l.Remove(p) && l.Count == 0) bySeq.Remove(p.Seq);
            }

            if (open.Count > 64)
                foreach (var k in open.Where(kv => kv.Value.T < batchT - TimeSpan.TicksPerSecond).Select(kv => kv.Key).ToList())
                    open.Remove(k);
        }

        private static string Id(uint id) => id.ToString("X8");

        private static void Pos(Utf8JsonWriter w, string name, float x, float y)
        {
            w.WritePropertyName(name);
            if (float.IsNaN(x) || float.IsNaN(y)) { w.WriteNullValue(); return; }
            w.WriteStartArray();
            w.WriteNumberValue(Math.Round(x, 2));
            w.WriteNumberValue(Math.Round(y, 2));
            w.WriteEndArray();
        }

        private static void Num(Utf8JsonWriter w, string name, long v)
        {
            if (v < 0) w.WriteNull(name);
            else w.WriteNumber(name, v);
        }

        private void Write(Pending p)
        {
            if (output is null) return;
            var abw = new ArrayBufferWriter<byte>(4096);
            using (var w = new Utf8JsonWriter(abw))
            {
                w.WriteStartObject();
                w.WriteString("log", stem);
                w.WriteString("t", p.Ts);
                w.WriteString("seq", Id(p.Seq));
                w.WriteString("caster", Id(p.Src));
                w.WriteNumber("casterJob", p.Job);
                if (p.Level is { } level) w.WriteNumber("level", level);
                else w.WriteNull("level");
                w.WriteBoolean("owner", p.Owner);
                w.WriteNumber("action", p.Action);
                w.WriteString("actionHex", p.Action.ToString("X4"));
                if (float.IsNaN(p.Lock)) w.WriteNull("lock");
                else w.WriteNumber("lock", Math.Round(p.Lock, 3));
                w.WriteNumber("castTime", Math.Round(p.CastTime, 3));
                Pos(w, "casterPos", p.CX, p.CY);
                w.WriteNumber("targetCount", p.TCount);

                w.WriteStartArray("targets");
                var seen = new HashSet<uint>();
                foreach (var l in p.Lines.OrderBy(l => l.TIndex))
                {
                    if (l.Target == NoTarget || !seen.Add(l.Target)) continue;
                    w.WriteStartObject();
                    w.WriteString("id", Id(l.Target));
                    Pos(w, "pos", l.TX, l.TY);
                    w.WriteStartArray("effects");
                    for (var i = 0; i < 8; i++)
                    {
                        var (fl, v) = (l.Flags[i], l.Values[i]);
                        if (fl == 0 && v == 0) continue;
                        w.WriteStartObject();
                        w.WriteNumber("type", fl & 0xFF);
                        w.WriteNumber("param", (fl >> 8) & 0xFF);
                        w.WriteString("value", v.ToString("X8"));
                        w.WriteString("flags", fl.ToString("X"));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartArray("gained");
                foreach (var (s, dt) in p.Gained)
                {
                    w.WriteStartObject();
                    w.WriteString("target", Id(s.Target));
                    w.WriteNumber("status", s.Status);
                    w.WriteNumber("param", s.Param);
                    w.WriteNumber("duration", Math.Round(s.Duration, 2));
                    w.WriteString("source", Id(s.Src));
                    w.WriteNumber("dt", Math.Round(dt / 1e7, 3));
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartArray("lost");
                foreach (var (s, dt) in p.Lost)
                {
                    w.WriteStartObject();
                    w.WriteString("target", Id(s.Target));
                    w.WriteNumber("status", s.Status);
                    w.WriteString("source", Id(s.Src));
                    w.WriteNumber("dt", Math.Round(dt / 1e7, 3));
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteString("gaugeBefore", p.GaugeBefore);
                w.WriteString("gaugeAfter", p.GaugeAfter);

                w.WriteStartArray("results");
                foreach (var r in p.Results.OrderBy(r => r.T).ThenBy(r => r.Actor))
                {
                    w.WriteStartObject();
                    w.WriteString("target", Id(r.Actor));
                    w.WriteNumber("dt", Math.Round((r.T - p.T) / 1e7, 3));
                    Num(w, "hp", r.Hp);
                    Num(w, "maxHp", r.MaxHp);
                    Num(w, "mp", r.Mp);
                    Num(w, "shield", r.Shield);
                    w.WriteString("field15", r.Field15);
                    w.WriteStartArray("statuses");
                    foreach (var (raw, param, duration, src) in r.Entries)
                    {
                        w.WriteStartObject();
                        w.WriteString("raw", raw.ToString("X8"));
                        w.WriteNumber("status", raw & 0xFFFF);
                        w.WriteNumber("hi", raw >> 24);
                        w.WriteNumber("param", param);
                        w.WriteNumber("duration", Math.Round(duration, 3));
                        w.WriteString("source", Id(src));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartObject("statusesBefore");
                foreach (var (actor, list) in p.StatusesBefore)
                {
                    w.WriteStartArray(Id(actor));
                    foreach (var (status, param, remaining, src) in list)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("status", status);
                        w.WriteNumber("param", param);
                        if (remaining is { } rem) w.WriteNumber("remaining", rem);
                        else w.WriteNull("remaining");
                        w.WriteString("source", Id(src));
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();

                w.WriteStartArray("recent");
                foreach (var (action, ago) in p.Recent)
                {
                    w.WriteStartObject();
                    w.WriteNumber("action", action);
                    w.WriteNumber("ago", Math.Round(ago, 2));
                    w.WriteEndObject();
                }

                w.WriteEndArray();

                w.WriteStartArray("party");
                foreach (var (id, job, xy, alive) in p.Party)
                {
                    w.WriteStartObject();
                    w.WriteString("id", Id(id));
                    if (job == 0) w.WriteNull("job");
                    else w.WriteNumber("job", job);
                    if (xy is { } v) Pos(w, "pos", v.X, v.Y);
                    else w.WriteNull("pos");
                    w.WriteBoolean("alive", alive);
                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            output.Write(abw.WrittenSpan);
            output.WriteByte((byte)'\n');
            Written++;
        }
    }
}
