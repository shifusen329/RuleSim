using System.Numerics;
using RaidReplay.Core.Model;

/// <summary>
///     Everything the replay reads from the logged pull: the party and their HP and positions, the boss
///     (what you target and whom it attacks), enemy casts, other players' statuses, raidwide hits and deaths.
///     The simulated Sage's own actions and statuses are NOT taken from the log; they come from the rules.
/// </summary>
internal sealed class World
{
    public readonly PullReplay R;
    public readonly Actor Me;
    public readonly List<Actor> Party;
    public readonly List<Actor> Bosses;
    public readonly uint ZoneId;
    public double MoveSpeed = 1.5;   // y/s: faster than this over the last 200 ms counts as moving (calibrated in "calibrate")

    // enemy casts by normalized name: (start, end) and deduplicated starts (casts within 1 s count once, as Wrath does)
    private readonly Dictionary<string, List<(int Start, int End)>> casts = new();
    private readonly Dictionary<string, List<int>> starts = new();
    // statuses from anyone other than the user, by target id
    private readonly Dictionary<uint, List<StatusInterval>> others = new();
    // boss autos: (t, target)
    private readonly List<(int T, Actor Source, Actor Target)> autos = new();
    // the user's damaging hits on enemies (for "what is targeted")
    private readonly List<(int T, Actor Target)> myHits = new();
    public readonly List<(int T, Actor Target, bool Auto, string Name, double InstPct)> HitsOnParty = new();
    public readonly List<Raidwide> Raidwides = new();

    public sealed record Raidwide(int T, string Name, int Ordinal, int Players, double AvgPct, int DeathsAfter);

    // 2026-10-28 (tank-care audit): RaidReplay names Dancing Mad's boss autos "auto-attack" (Kefka, Chaos C252; Exdeath C250),
    // not "attack", so with the old check the boss's target was never known there (Kardia "on the boss's target" and every
    // targetsTarget rule never fired in the replay) and each auto broke a tank shield at once. SIM_AUTOS=1 counts them; off by
    // default so earlier saved numbers still reproduce.
    public static readonly bool AutoFix = Environment.GetEnvironmentVariable("SIM_AUTOS") == "1";
    public static bool IsAutoName(string n) => n.Equals("attack", StringComparison.OrdinalIgnoreCase) || AutoFix && n.Equals("auto-attack", StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static readonly HashSet<string> RaidSelfBuffs = ["Divination", "Battle Voice", "Radiant Finale", "Technical Finish",
        "Battle Litany", "Brotherhood", "Starry Muse", "Embolden", "Arcane Circle", "Searing Light"];
    public static readonly HashSet<string> RaidTargetDebuffs = ["Mug", "Dokumori", "Chain Stratagem"];
    // Rough damage multipliers for scoring only.
    public static readonly Dictionary<string, double> BuffValue = new()
    {
        ["Divination"] = 0.06, ["Battle Voice"] = 0.025, ["Radiant Finale"] = 0.06, ["Technical Finish"] = 0.05, ["Battle Litany"] = 0.025,
        ["Brotherhood"] = 0.05, ["Starry Muse"] = 0.05, ["Embolden"] = 0.05, ["Arcane Circle"] = 0.03, ["Searing Light"] = 0.05,
        ["Mug"] = 0.05, ["Dokumori"] = 0.05, ["Chain Stratagem"] = 0.025,
    };

    public World(PullReplay r, uint meId)
    {
        R = r;
        ZoneId = r.Summary.ZoneId;
        Me = r.Actors.First(a => a.Id == meId);
        Party = r.Party.Count > 0 ? r.Party.ToList() : r.Players.ToList();
        Bosses = r.Actors.Where(a => a.Kind == ActorKind.Boss).ToList();

        foreach (var c in r.Casts.Where(c => c.Source.Kind is ActorKind.Boss or ActorKind.Enemy or ActorKind.Helper))
        {
            var n = Normalize(c.Name);
            if (n.Length == 0) continue;
            var end = c.EndMs > c.StartMs ? c.EndMs : c.StartMs + c.DurationMs;
            (casts.TryGetValue(n, out var l) ? l : casts[n] = []).Add((c.StartMs, end));
        }
        foreach (var (n, l) in casts)
        {
            l.Sort();
            var s = starts[n] = [];
            foreach (var (st, _) in l)
                if (st >= -500 && (s.Count == 0 || Math.Abs(st - s[^1]) > 1000)) s.Add(st);
        }

        foreach (var st in r.Statuses.Where(s => s.Source is null || s.Source.Id != meId))
            (others.TryGetValue(st.Target.Id, out var l) ? l : others[st.Target.Id] = []).Add(st);

        var partyIds = Party.Select(p => p.Id).ToHashSet();
        // Abilities that never do damage to anyone in the pull (Tele-trouncing, the Path of Light, White Hole: mechanic
        // resolutions) cannot take a barrier, so they are not hits. Model change 2026-10-21; before it they broke shields.
        var raw = new List<(int T, Actor Target, bool Auto, string Name, double Pct)>();
        var damaging = r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet)
            .SelectMany(a => a.Hits.Where(h => partyIds.Contains(h.Target.Id) && h.Damage > 0).Select(_ => a.Name)).ToHashSet();
        foreach (var a in r.Actions)
        {
            if (a.Source.Id == meId)
            {
                // SIM_AUTOS=1 (2026-10-28): what the player had targeted, from single-target spells only.
                // Only single-target spells say what was targeted: in the 22:24 pull (Oct 28) Phlegma III, Toxikon II and Psyche
                // landed on Chaos while the Dosis III between them went to Exdeath and Kardia stayed on Exdeath's tank.
                if (AutoFix)
                {
                    if (a.Name is "Dosis III" or "Eukrasian Dosis III" or "Dosis II" or "Dosis" or "Eukrasian Dosis II" or "Eukrasian Dosis"
                        && a.Hits.FirstOrDefault(h => !h.Target.IsPlayer && h.Target.Kind != ActorKind.Pet) is { } sh)
                        myHits.Add((sh.T, sh.Target));
                }
                else
                foreach (var h in a.Hits)
                    if (h.Kind == HitKind.Damage && !h.Target.IsPlayer) { myHits.Add((h.T, h.Target)); break; }
                continue;
            }
            if (a.Source.IsPlayer || a.Source.Kind == ActorKind.Pet) continue;
            var isAuto = IsAutoName(a.Name);
            if (isAuto && a.Source.Kind == ActorKind.Boss)
                if ((a.PrimaryTarget ?? a.Hits.FirstOrDefault()?.Target) is { } tgt && partyIds.Contains(tgt.Id))
                    autos.Add((a.T, a.Source, tgt));
            if (!damaging.Contains(a.Name)) continue;
            foreach (var h in a.Hits)
                if (partyIds.Contains(h.Target.Id) && h.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried)
                    raw.Add((h.T, h.Target, isAuto, a.Name, h.MaxHp > 0 ? 100.0 * h.Damage / h.MaxHp : 0));
        }
        // Each hit also carries its instance's average damage (% of max HP over every party hit of that ability within 1.5 s).
        foreach (var g in raw.GroupBy(x => x.Name))
        {
            var l = g.OrderBy(x => x.T).ToList();
            for (var i = 0; i < l.Count;)
            {
                var j = i;
                while (j < l.Count && l[j].T - l[i].T <= 1500) j++;
                var avg = l.Skip(i).Take(j - i).Average(x => x.Pct);
                for (var k = i; k < j; k++) HitsOnParty.Add((l[k].T, l[k].Target, l[k].Auto, l[k].Name, avg));
                i = j;
            }
        }
        HitsOnParty.Sort((x, y) => x.T.CompareTo(y.T));

        // Raidwides: the same enemy ability hitting 5+ party members within 1.5 s.
        var ord = new Dictionary<string, int>();
        foreach (var g in r.Actions.Where(a => !a.Source.IsPlayer && a.Source.Kind != ActorKind.Pet && !IsAutoName(a.Name))
                     .GroupBy(a => a.Name).SelectMany(Cluster).OrderBy(x => x.T))
        {
            var hits = g.Hits.Where(h => partyIds.Contains(h.Target.Id) && h.Kind == HitKind.Damage).ToList();
            var players = hits.Select(h => h.Target.Id).Distinct().Count();
            if (players < 5) continue;
            ord[g.Name] = ord.GetValueOrDefault(g.Name) + 1;
            var pct = hits.Where(h => h.MaxHp > 0).Select(h => 100.0 * h.Damage / h.MaxHp).DefaultIfEmpty(0).Average();
            if (pct > 150) continue;   // mis-decoded big numbers / wipe mechanics
            var deaths = r.Deaths.Count(d => d.T >= g.T && d.T <= g.T + 4000);
            Raidwides.Add(new Raidwide(g.T, g.Name, ord[g.Name], players, pct, deaths));
        }
    }

    private static IEnumerable<(int T, string Name, List<HitEvent> Hits)> Cluster(IGrouping<string, ActionEvent> g)
    {
        var list = g.OrderBy(a => a.T).ToList();
        for (var i = 0; i < list.Count;)
        {
            var t0 = list[i].T;
            var hits = new List<HitEvent>();
            while (i < list.Count && list[i].T - t0 <= 1500) { hits.AddRange(list[i].Hits); i++; }
            yield return (t0, g.Key, hits);
        }
    }

    // ---------- time queries ----------

    public static bool IsTank(Actor a) => a.Job is 19 or 21 or 32 or 37 or 1 or 3;
    public static bool IsHealer(Actor a) => a.Job is 24 or 28 or 33 or 40 or 6 or 26;
    public static string RoleOf(Actor a) => IsTank(a) ? "tank" : IsHealer(a) ? "healer" : "dps";

    public bool Dead(Actor a, int t) =>
        R.Deaths.Any(d => d.Victim.Id == a.Id && d.T <= t && (d.RaisedMs < 0 || t < d.RaisedMs));

    public Vector2 Pos(Actor a, int t) => a.Track.TrySample(t, out var p, out _) ? p : new Vector2(100, 100);

    public double Dist(Actor a, Actor b, int t) =>
        Math.Max(0, Vector2.Distance(Pos(a, t), Pos(b, t)) - Radius(a) - Radius(b));

    public static float Radius(Actor a) => a.IsPlayer ? 0.5f : a.Radius;

    public double Speed(Actor a, int t)
    {
        var d = Vector2.Distance(Pos(a, t), Pos(a, t - 200));
        return d / 0.2;
    }

    public bool Moving(int t) => BurstModel ? MovingBurst(Me, t) : Speed(Me, t) > MoveSpeed;

    /// <summary>
    ///     Positions arrive about once a second, held while still. A displacement between two samples is taken as
    ///     running (RunSpeed y/s) for the time it needs, ending at the later sample: a 0.3 s step is 0.3 s of
    ///     movement, not a slow walk smeared over the whole gap.
    /// </summary>
    public bool BurstModel = true;
    public double RunSpeed = 5.0, MinStep = 0.25;   // calibrated: 84% of real cancels seen, 7% of completed casts wrongly cancelled (0.7 s slide)

    public bool MovingBurst(Actor a, int t)
    {
        var tr = a.Track;
        var i = tr.Floor(t);
        if (i < 0 || i + 1 >= tr.Count) return false;
        var j = i + 1;
        if ((tr.Flags[j] & PosFlags.Teleport) != 0) return false;
        var d = Math.Sqrt(Math.Pow(tr.X[j] - tr.X[i], 2) + Math.Pow(tr.Y[j] - tr.Y[i], 2));
        if (d < MinStep) return false;
        var dur = d / RunSpeed * 1000;
        return t >= tr.T[j] - dur;
    }

    public double HpPct(Actor a, int t)
    {
        if (a.MaxHp <= 0) return 100;
        var hp = a.Hp.At(t, a.MaxHp);
        return Math.Clamp(100.0 * hp / a.MaxHp, 0, 100);
    }

    /// <summary> What the user has targeted: the enemy their last damaging action hit (if still targetable), else the biggest targetable boss. </summary>
    public Actor? Target(int t)
    {
        var last = myHits.LastOrDefault(h => h.T <= t && h.T >= t - 15000);
        if (last.Target is { } lt && lt.IsPresent(t) && lt.IsTargetable(t)) return lt;
        return Bosses.Where(b => b.IsPresent(t) && b.IsTargetable(t) && !b.IsHidden(t)).MaxBy(b => b.MaxHp);
    }

    /// <summary> The boss's target: whoever its most recent auto-attack (within 6 s) hit. </summary>
    public Actor? BossTarget(Actor? boss, int t)
    {
        if (boss is null) return null;
        for (var i = autos.Count - 1; i >= 0; i--)
        {
            var a = autos[i];
            if (a.T > t) continue;
            if (t - a.T > 6000) break;
            if (a.Source.Id == boss.Id || a.Source.Name == boss.Name) return a.Target;
        }
        return null;
    }

    /// <summary> The latest time (ms) before <paramref name="t" /> any of <paramref name="party" /> lost the status; -1 if none. </summary>
    public int LastLost(IEnumerable<Actor> party, uint statusId, int t)
    {
        var last = -1;
        foreach (var p in party)
            if (others.TryGetValue(p.Id, out var l))
                foreach (var s in l)
                    if (s.StatusId == statusId && s.EndMs <= t && s.EndMs > last) last = s.EndMs;
        return last;
    }

    public bool OthersHave(Actor target, uint statusId, int t) =>
        others.TryGetValue(target.Id, out var l) && l.Any(s => s.StatusId == statusId && s.Active(t));

    public bool OthersHaveNamed(Actor target, string name, int t, out double left)
    {
        left = 0;
        if (!others.TryGetValue(target.Id, out var l)) return false;
        var found = false;
        foreach (var s in l)
            if (s.Name == name && s.Active(t))
            {
                found = true;
                var exp = s.ExpiresAt(t) ?? s.EndMs;
                left = Math.Max(left, (exp - t) / 1000.0);
            }
        return found;
    }

    // ---------- added 2026-10-21 for the Samurai replay (new methods only; nothing above changed) ----------

    /// <summary> Seconds left on a status (by id) that someone other than the replayed player put on the target; 0 if none. </summary>
    public double OthersTime(Actor target, uint statusId, int t)
    {
        if (!others.TryGetValue(target.Id, out var l)) return 0;
        double left = 0;
        foreach (var s in l)
            if (s.StatusId == statusId && s.Active(t))
                left = Math.Max(left, ((s.ExpiresAt(t) ?? s.EndMs) - t) / 1000.0);
        return left;
    }

    /// <summary> Stacks of a status (by id) from someone else on the target; 0 if none. </summary>
    public int OthersStacks(Actor target, uint statusId, int t) =>
        others.TryGetValue(target.Id, out var l) ? l.Where(s => s.StatusId == statusId && s.Active(t)).Select(s => s.AppliedAt(t).Stacks).DefaultIfEmpty(0).Max() : 0;

    /// <summary> Facing (radians, 0 = +Y, as the game's rotation) of an actor at t, from its position track. </summary>
    public float Heading(Actor a, int t) => a.Track.TrySample(t, out _, out var h) ? h : 0;

    /// <summary>
    ///     Non-auto damaging hits from enemies on one party member: (t, ability, % of max HP), in time order. Hits whose
    ///     damage is 0 (fully absorbed numbers still count as damage in the log) are left out.
    /// </summary>
    public List<(int T, string Name, double Pct, Actor Source)> HitsOn(Actor who)
    {
        var res = new List<(int, string, double, Actor)>();
        foreach (var a in R.Actions)
        {
            if (a.Source.IsPlayer || a.Source.Kind == ActorKind.Pet || IsAutoName(a.Name)) continue;
            foreach (var h in a.Hits)
                if (h.Target.Id == who.Id && h.Damage > 0 && h.Kind is HitKind.Damage or HitKind.Blocked or HitKind.Parried)
                    res.Add((h.T, a.Name, h.MaxHp > 0 ? 100.0 * h.Damage / h.MaxHp : 0, a.Source));
        }
        res.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return res;
    }

    public (int Count, double Left, double Mult) RaidBuffs(Actor? target, int t)
    {
        int n = 0; double left = 0, mult = 1;
        foreach (var name in RaidSelfBuffs)
            if (OthersHaveNamed(Me, name, t, out var l)) { n++; left = Math.Max(left, l); mult *= 1 + BuffValue[name]; }
        if (target is not null)
            foreach (var name in RaidTargetDebuffs)
                if (OthersHaveNamed(target, name, t, out var l)) { n++; left = Math.Max(left, l); mult *= 1 + BuffValue[name]; }
        return (n, left, mult);
    }

    // ---------- enemy casts (Wrath's EnemyCasts semantics) ----------

    public bool Casting(string n, int t) => casts.TryGetValue(n, out var l) && l.Any(c => c.Start <= t && t < c.End);

    public double CastIn(string n, int t)
    {
        if (!casts.TryGetValue(n, out var l)) return 99;
        var best = 99.0;
        foreach (var c in l)
            if (c.Start <= t && t < c.End) best = Math.Min(best, (c.End - t) / 1000.0);
        return best;
    }

    public IEnumerable<int> CastStarts(string n) => starts.TryGetValue(World.Normalize(n), out var s) ? s : [];
    public int Seen(string n, int t) => starts.TryGetValue(n, out var s) ? s.Count(x => x <= t) : 0;

    public double Since(string n, int t)
    {
        if (!starts.TryGetValue(n, out var s)) return 999;
        var last = s.LastOrDefault(x => x <= t, int.MinValue);
        return last == int.MinValue ? 999 : (t - last) / 1000.0;
    }

    public double SinceFirst(string n, int t)
    {
        if (!starts.TryGetValue(n, out var s) || s.Count == 0 || s[0] > t) return 999;
        return (t - s[0]) / 1000.0;
    }
}
