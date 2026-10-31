using System.Text;
using System.Text.RegularExpressions;
using RaidReplay.Core.Model;

/// <summary>
///     Coverage and healing metrics for a replayed pull (added 2026-10-21 for the Sage optimisation):
///     - sheet slots: the Sage column of the Ikuya Mitty sheets, matched to the pull's hits the way
///       scratchpad/mit/reconcile.py does it, and checked against the rules' presses and the real Sage's statuses;
///     - big hits: party-wide hits of 20%+ HP, with a Sage shield / any Sage mitigation up;
///     - healing: per segment between big hits, a counterfactual where the real Sage's effective healing is removed
///       from the logged HP and the rules' healing is added (capped at max HP).
///     Effects pressed by the rules count from LandMs after the press (the log shows buffs landing 0.5-0.7 s after
///     the press), so a press in the last 0.6 s before a hit does not count as covering it.
/// </summary>
internal static class Metrics
{
    public const int LandMs = 600;

    // ---------------------------------------------------------------- the rules' coverage

    private static IEnumerable<int> Presses(SageSim sim, string action) => sim.Log.Where(e => e.What == action).Select(e => e.T);

    public static bool SimUp(SageSim sim, string action, int durMs, int t) =>
        Presses(sim, action).Any(p => p + LandMs <= t && t < p + LandMs + durMs);

    /// <summary>
    ///     Panhaima: five stacks; each non-auto hit on the Sage takes one (one ability's simultaneous helper hits count
    ///     once) and restarts the 15 s timer, as the real Sage's four Panhaimas in the 2026-10-05 clear show (20.5, 21.2,
    ///     17.7 s with stacks taken, 15.0 s without; the 17:43 one carried through four Forsaken hits).
    /// </summary>
    public static bool SimPanhaimaUp(SageSim sim, World w, int t) => Presses(sim, "Panhaima").Any(p => PanhaimaUp(w, p + LandMs, t));

    /// <summary>
    ///     A Panhaima that landed at <paramref name="from"/>, at t: hits on the Sage within 200 ms of each other take one
    ///     stack, hits from an instance averaging under 3% of HP on the party take none (the real Sage's Panhaima kept
    ///     its stacks through most of the 1-6% Ultima Blasters and was still up at Cyclone), each stack taken restarts
    ///     the 15 s timer, and the fifth ends it. Checked against the real Sage's eight Panhaimas (PanhaimaCheck).
    /// </summary>
    public static bool PanhaimaUp(World w, int from, int t)
    {
        if (t < from || t >= from + 5 * 15000) return false;
        int stacks = 5, expire = from + 15000, last = -1_000_000;
        foreach (var h in w.HitsOnParty.Where(h => h.Target.Id == w.Me.Id && !h.Auto && h.InstPct >= 3 && h.T >= from && h.T < t))
        {
            if (h.T >= expire) break;
            if (h.T - last <= 200) continue;
            last = h.T;
            stacks--;
            expire = h.T + 15000;
            if (stacks == 0) return false;
        }
        return t < expire;
    }

    /// <summary> The model applied to the real Sage's Panhaimas (landing = the logged status start), against the logged status. </summary>
    public static string PanhaimaCheck(World w, List<StatusInterval> mine, List<Slot> slots)
    {
        var starts = mine.Where(s => s.Name == "Panhaima" && s.Target.Id == w.Me.Id).Select(s => s.StartMs).OrderBy(x => x).ToList();
        // group re-applications: a new instance starts when the previous one had ended
        var inst = new List<int>();
        foreach (var s in mine.Where(s => s.Name == "Panhaima" && s.Target.Id == w.Me.Id).OrderBy(s => s.StartMs))
            if (inst.Count == 0 || !mine.Any(o => o.Name == "Panhaima" && o.Target.Id == w.Me.Id && o.StartMs < s.StartMs && o.EndMs >= s.StartMs - 50))
                inst.Add(s.StartMs);
        int agree = 0, n = 0;
        var lines = new List<string>();
        foreach (var x in slots.Where(x => x.Label == "Panhaima"))
        {
            var model = inst.Any(f => PanhaimaUp(w, f, x.T));
            if (Environment.GetEnvironmentVariable("PANH_DEBUG") is not null)
                Console.WriteLine($"   panh-debug slot {x.T} inst [{string.Join(",", inst)}] hits [{string.Join(",", w.HitsOnParty.Where(h => h.Target.Id == w.Me.Id && !h.Auto && h.T >= x.T - 25000 && h.T < x.T).Select(h => $"{h.T}:{h.Name}:{h.InstPct:0.0}"))}]");
            var log = RealUp(w, mine, "Panhaima", x.T, 1);
            n++; if (model == log) agree++;
            else lines.Add($"{SageSim.F(x.T)} {x.Mech}: model {(model ? "up" : "--")}, log {(log ? "up" : "--")}");
        }
        return $"Panhaima model on the real Sage's presses agrees with the log at {agree}/{n} Panhaima slots" + (lines.Count > 0 ? " (" + string.Join("; ", lines) + ")" : "");
    }

    /// <summary> A shield from the rules on the Sage at t: the last Eukrasian Prognosis (landed) within 30 s, not broken since. </summary>
    public static bool SimShieldUp(SageSim sim, World w, int t)
    {
        var last = sim.Log.LastOrDefault(e => e.What == "Eukrasian Prognosis II" && e.T + LandMs <= t);
        if (last is null || t - last.T - LandMs >= 30000) return false;
        var from = last.T + LandMs;
        var hits = w.HitsOnParty.Where(h => h.Target.Id == w.Me.Id && h.T >= from && h.T < t).ToList();
        return !(hits.Any(h => !h.Auto) || hits.Count(h => h.Auto) >= 2);
    }

    /// <summary> The rules' Eukrasian Prognosis on player p at t: the last press that reached them (20 y when it landed),
    /// under 30 s old, not broken since (one non-auto hit, or two autos). </summary>
    public static bool SimShieldOn(SageSim sim, World w, Actor p, int t)
    {
        foreach (var e in sim.Log.Where(e => e.What == "Eukrasian Prognosis II" && e.T + LandMs <= t).Reverse())
        {
            var from = e.T + LandMs;
            if (t - from >= 30000) return false;
            if (p.Id != w.Me.Id && w.Dist(w.Me, p, from) > 20.5) continue;
            var hits = w.HitsOnParty.Where(h => h.Target.Id == p.Id && h.T >= from && h.T < t).ToList();
            return !(hits.Any(h => !h.Auto) || hits.Count(h => h.Auto) >= 2);
        }
        return false;
    }

    /// <summary> Moments when 8 party members take damage within 1 s (any mix of abilities, failures over 150% left out):
    /// (moments, players hit, of them with the rules' shield, moments with all shielded). </summary>
    public static (int N, int Players, int Shielded, int All) AllEightCoverage(SageSim sim, World w)
    {
        var hits = w.HitsOnParty.Where(h => !h.Auto).ToList();
        int n = 0, pl = 0, sh = 0, all = 0, i = 0;
        while (i < hits.Count)
        {
            var t0 = hits[i].T;
            var grp = hits.Where(h => h.T >= t0 && h.T < t0 + 1000).ToList();
            var who = grp.Select(h => h.Target).DistinctBy(a => a.Id).ToList();
            if (who.Count >= 8 && grp.All(h => h.InstPct <= 150))
            {
                n++; var c = who.Count(p => SimShieldOn(sim, w, p, t0)); pl += who.Count; sh += c; all += c == who.Count ? 1 : 0;
                if (c < who.Count && Environment.GetEnvironmentVariable("ALL8_DETAIL") == "1")
                {
                    var last = sim.Log.LastOrDefault(e => e.What == "Eukrasian Prognosis II" && e.T + LandMs <= t0);
                    var miss = who.Where(p => !SimShieldOn(sim, w, p, t0)).Select(p =>
                    {
                        var why = last is null || t0 - last.T >= 30000 ? "no shield in 30 s"
                            : p.Id != w.Me.Id && w.Dist(w.Me, p, last.T + LandMs) > 20.5 ? $"out of range {w.Dist(w.Me, p, last.T + LandMs):0}y"
                            : "broken by " + string.Join("+", w.HitsOnParty.Where(h => h.Target.Id == p.Id && h.T >= last.T + LandMs && h.T < t0).Select(h => h.Auto ? "auto" : h.Name).Distinct());
                        return $"{p.Name.Split(' ')[0]}{(World.IsTank(p) ? "(T)" : "")}: {why}";
                    });
                    Console.WriteLine($"     miss {SageSim.F(t0)} {string.Join("+", grp.Select(h => h.Name).Distinct().Take(3))} (last shield {(last is null ? "-" : SageSim.F(last.T))}): {string.Join("; ", miss)}");
                }
                i = hits.FindIndex(h => h.T >= t0 + 1000); if (i < 0) break;
            }
            else i++;
        }
        return (n, pl, sh, all);
    }

    public static string SimCover(SageSim sim, World w, int t)
    {
        var parts = new List<string>();
        if (SimUp(sim, "Kerachole", 15000, t)) parts.Add("Kera");
        if (SimUp(sim, "Holos", 20000, t)) parts.Add("Holos");
        if (SimPanhaimaUp(sim, w, t)) parts.Add("Panh");
        if (SimUp(sim, "Philosophia", 20000, t)) parts.Add("Philo");
        if (SimUp(sim, "Physis II", 15000, t)) parts.Add("Phys");
        if (SimShieldUp(sim, w, t)) parts.Add("Shield");
        return string.Join(" ", parts);
    }

    // ---------------------------------------------------------------- the real Sage's coverage

    /// <summary> The real Sage's status on at least minTargets party members at t (reconcile.py's tolerance). </summary>
    public static bool RealUp(World w, List<StatusInterval> mine, string name, int t, int minTargets = 3)
    {
        var party = w.Party.Select(p => p.Id).ToHashSet();
        return mine.Where(s => s.Name == name && party.Contains(s.Target.Id) && s.StartMs <= t + 20 && s.EndMs >= t - 50)
            .Select(s => s.Target.Id).Distinct().Count() >= minTargets;
    }

    // ---------------------------------------------------------------- the sheet

    public sealed record SheetRow(string Phase, string Mech, int SheetMs, List<(string Label, bool Carry)> Sge);

    public sealed record Slot(string Phase, string Mech, int SheetMs, int T, string Label, bool Carry, bool Sim, bool Real);

    /// <summary> The sheet rows with one job's column (15 = Sage; 9 = White Mage, added 2026-10-21 for the WHM replay). </summary>
    public static List<SheetRow> LoadSheet(string dir, int col = 15)
    {
        var rows = new List<SheetRow>();
        if (!Directory.Exists(dir)) return rows;
        foreach (var path in Directory.GetFiles(dir, "Ikuya Mitty (DMU) - P*.csv").OrderBy(p => p))
        {
            var phase = Regex.Match(Path.GetFileName(path), @" - (P\d)").Groups[1].Value;
            foreach (var r in Csv(File.ReadAllText(path)))
            {
                if (r.Count < 26 || r[1].Trim().Length == 0 || r[1].Trim() == "Notes" || !r[3].Contains(':') || r[3].Contains('\n')) continue;
                var sge = new List<(string, bool)>();
                foreach (var raw in r[col].Split('\n'))
                {
                    var ln = raw.Trim();
                    if (ln.Length == 0) continue;
                    var carry = ln.StartsWith('➔');
                    ln = ln.TrimStart('➔', ' ').Trim();
                    foreach (var part in ln.Split('+'))
                    {
                        var p = Regex.Replace(part, @"\s*[¹²³⁴⁵⁶⁷⁸⁹⁰]+", "").Trim();
                        if (p.Length > 0) sge.Add((p, carry));
                    }
                }
                var ms = r[3].Trim().Split(':');
                rows.Add(new SheetRow(phase, r[1].Trim().Replace('\n', ' '), (int.Parse(ms[0]) * 60 + int.Parse(ms[1])) * 1000, sge));
            }
        }
        return rows;
    }

    internal static List<List<string>> Csv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else if (c != '\r') cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // Sheet mechanic (lowercase prefix, first match wins) -> ability names in the log; as in reconcile.py.
    private static readonly (string Key, string[] Abilities)[] Match =
    [
        ("mystery magic", ["Flagrant Fire III", "Thrumming Thunder III"]), ("wave cannon", ["Wave Cannon"]),
        ("double-trouble trap", ["Double-trouble Trap"]), ("light of judg", ["Light of Judgment"]), ("gravitas", ["Gravitas"]),
        ("indulgent will", ["Indulgent Will"]), ("ultimate embra", ["Ultimate Embrace"]), ("forsaken bonds", ["Forsaken Bonds"]),
        ("forsaken null", []), ("forsaken", ["Forsaken"]), ("towers", ["Spellscatter", "Spellwave", "Spelldriver", "the Path of Light"]),
        ("wings of destruction", ["Wings of Destruction"]), ("bowels of agony", ["Bowels of Agony"]), ("stray flames", ["Stray Flames", "Tsunami"]),
        ("thunder iii", ["Thunder III"]), ("ultima blaster", ["Ultima Blaster"]), ("vacuum wave", ["Vacuum Wave"]), ("cyclone", ["Cyclone"]),
        ("the decisive battle", []), ("earthquake", ["Earthquake"]), ("shocking impact", ["Shocking Impact", "Shockwave"]),
        ("black holes", ["Nothingness"]), ("stomp-a-mole", ["Stomp-a-Mole", "Knock Down"]), ("grand cross", ["Grand Cross"]),
        ("inferno", ["Inferno", "Tsunami"]), ("flood of naught", ["Black Antilight", "White Antilight"]), ("death bolt", ["Death Bolt", "Death Wave"]),
        ("ultima upsurge", ["Ultima Upsurge"]), ("ultima repeater", ["Ultima Repeater"]), ("fell forces", ["Fell Forces"]),
        ("chaotic flood", ["Chaotic Flood"]), ("maddening orchestra", ["Flare", "Holy"]), ("celestriad", ["Blizzard III", "Fire III"]),
        ("stray entropy", ["Stray Entropy"]),
    ];

    private static readonly Dictionary<string, string> CastEnd = new() { ["the decisive battle"] = "the Decisive Battle", ["forsaken null"] = "Forsaken Null" };

    internal static Dictionary<string, List<int>> HitClusters(World w)
    {
        var party = w.Party.Select(p => p.Id).ToHashSet();
        var res = new Dictionary<string, List<int>>();
        foreach (var a in w.R.Actions.OrderBy(a => a.T))
        {
            if (a.Source.IsPlayer || a.Source.Kind == ActorKind.Pet || party.Contains(a.Source.OwnerId) || a.Name.Length == 0) continue;
            if (!a.Hits.Any(h => party.Contains(h.Target.Id)) && !(a.PrimaryTarget is { } pt && party.Contains(pt.Id))) continue;
            var l = res.TryGetValue(a.Name, out var x) ? x : res[a.Name] = [];
            if (l.Count == 0 || a.T - l[^1] >= 1500) l.Add(a.T);
        }
        return res;
    }

    internal static (int? T, string Key) FindHit(World w, Dictionary<string, List<int>> hits, string mech, int expect, HashSet<(string, int)> used)
    {
        var key = Match.FirstOrDefault(m => mech.ToLowerInvariant().StartsWith(m.Key));
        if (key.Key is null) return (null, "");
        if (CastEnd.TryGetValue(key.Key, out var castName))
        {
            var c = w.R.Casts.Where(c => c.Name == castName && !c.Source.IsPlayer).Select(c => c.StartMs + c.DurationMs)
                .Where(x => Math.Abs(x - expect) <= 6000 && !used.Contains(("cast", x))).OrderBy(x => Math.Abs(x - expect)).ToList();
            if (c.Count == 0) return (null, "");
            used.Add(("cast", c[0]));
            return (c[0], "cast");
        }
        (int T, string Ab)? best = null;
        foreach (var ab in key.Abilities)
            foreach (var t in hits.GetValueOrDefault(ab) ?? [])
                if (Math.Abs(t - expect) <= 6000 && !used.Contains((ab, t)) && (best is null || Math.Abs(t - expect) < Math.Abs(best.Value.T - expect)))
                    best = (t, ab);
        if (best is null) return (null, "");
        used.Add((best.Value.Ab, best.Value.T));
        return (best.Value.T, best.Value.Ab);
    }

    /// <summary> Matches each sheet row to a hit (one clock offset per phase, fitted as reconcile.py does) and checks the Sage column. </summary>
    public static List<Slot> Slots(World w, SageSim sim, List<StatusInterval> mine, List<(int T, string Name, string Target)> realEvents, List<SheetRow> sheet) =>
        SlotsWith(w, sheet, (label, T) =>
        {
            bool simUp, realUp;
            switch (label)
            {
                case "Kerachole": simUp = SimUp(sim, "Kerachole", 15000, T); realUp = RealUp(w, mine, "Kerachole", T); break;
                case "Holos": simUp = SimUp(sim, "Holos", 20000, T); realUp = RealUp(w, mine, "Holos", T); break;
                case "Panhaima": simUp = SimPanhaimaUp(sim, w, T); realUp = RealUp(w, mine, "Panhaima", T); break;
                case "Philosophia": simUp = SimUp(sim, "Philosophia", 20000, T); realUp = RealUp(w, mine, "Eudaimonia", T); break;
                case "Zoe Shields":
                    // reconcile.py: shields up and Zoe's status ended (consumed by a heal GCD, or ran out) in the 30 s before.
                    simUp = SimShieldUp(sim, w, T) && SimZoeEnds(sim).Any(e => e <= T && e >= T - 30000);
                    realUp = RealUp(w, mine, "Eukrasian Prognosis", T) && mine.Any(s => s.Name == "Zoe" && s.EndMs <= T && s.EndMs >= T - 30000);
                    break;
                default: return null;
            }
            return (simUp, realUp);
        });

    /// <summary>
    ///     Any job's column (generalised 2026-10-21 for the White Mage; the Sage's Slots runs through it unchanged): each sheet
    ///     row matched to its hit with one clock offset per phase, then <paramref name="check"/> gives (rules up, real up) for
    ///     each label at the hit, or null for a label it doesn't score.
    /// </summary>
    public static List<Slot> SlotsWith(World w, List<SheetRow> sheet, Func<string, int, (bool Sim, bool Real)?> check)
    {
        var hits = HitClusters(w);
        var used = new HashSet<(string, int)>();
        var slots = new List<Slot>();
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
                    var (t, _) = FindHit(w, hits, r.Mech, exp, copy);
                    if (t is { } tt) { n++; dev += Math.Abs(tt - exp); }
                }
                if (best is null || n > best.Value.N || n == best.Value.N && dev < best.Value.Dev) best = (d10 * 100, n, dev);
            }
            foreach (var r in rows)
            {
                var (t, _) = FindHit(w, hits, r.Mech, r.SheetMs + (int)best!.Value.D, used);
                if (t is not { } T) continue;
                foreach (var (label, carry) in r.Sge)
                    if (check(label, T) is { } c)
                        slots.Add(new Slot(r.Phase, r.Mech, r.SheetMs, T, label, carry, c.Sim, c.Real));
            }
        }
        return slots;
    }

    private static readonly HashSet<string> HealGcds = ["Eukrasian Prognosis II", "Pneuma", "Eukrasian Diagnosis", "Diagnosis", "Prognosis"];

    /// <summary> When each of the rules' Zoes ended: the next heal GCD after it (which takes it), else 30 s. </summary>
    private static IEnumerable<int> SimZoeEnds(SageSim sim)
    {
        foreach (var z in sim.Log.Where(e => e.What == "Zoe"))
            yield return sim.Log.FirstOrDefault(e => e.T >= z.T && e.T <= z.T + 30000 && HealGcds.Contains(e.What))?.T ?? z.T + 30000;
    }

    // ---------------------------------------------------------------- healing

    /// <summary> HP per potency, from the real Sage's direct heals of known potency in these pulls (crits and outside buffs included). </summary>
    public static readonly Dictionary<string, int> HealPotency = new()
    {
        ["Ixochole"] = 400, ["Holos"] = 300, ["Pneuma"] = 600, ["Prognosis"] = 300, ["Druochole"] = 600, ["Taurochole"] = 700, ["Eukrasian Diagnosis"] = 300,
        ["Diagnosis"] = 450,
    };

    public static (long Raw, long Pot) CalibrationSample(World w) => CalibrationSample(w, HealPotency);

    /// <summary> The same with another job's heal table (the White Mage replay passes White Mage and Astrologian heals). </summary>
    public static (long Raw, long Pot) CalibrationSample(World w, Dictionary<string, int> table)
    {
        long raw = 0, pot = 0;
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == w.Me.Id && table.ContainsKey(a.Name)))
            foreach (var h in a.Hits.Where(h => h.Kind == HitKind.Heal && h.Target.IsPlayer))
            { raw += h.Heal; pot += table[a.Name]; }
        return (raw, pot);
    }

    /// <summary> Raw healing by source for any replayed healer's heals, and the logged player's effective healing. </summary>
    public static string Breakdown(World w, IReadOnlyList<(int T, Actor Target, double Potency, string Src)> simHeals, double hpPerPot)
    {
        var rules = simHeals.GroupBy(h => h.Src).Select(g => (g.Key, V: g.Sum(h => h.Potency) * hpPerPot)).OrderByDescending(x => x.V);
        var real = RealHealsNamed(w).GroupBy(x => x.Src).Select(g => (g.Key, V: g.Sum(x => x.Eff))).OrderByDescending(x => x.V);
        return "rules (raw): " + string.Join(", ", rules.Select(x => $"{x.Key} {x.V / 1e6:0.00}M")) +
               "; logged (effective): " + string.Join(", ", real.Select(x => $"{x.Key} {x.V / 1e6:0.00}M"));
    }

    /// <summary> The real Sage's effective healing events: heal amount minus overheal, direct heals and HoT/Kardia ticks. </summary>
    public static List<(int T, Actor Target, double Eff)> RealHeals(World w) => RealHealsNamed(w).Select(x => (x.T, x.Target, x.Eff)).ToList();

    public static List<(int T, Actor Target, double Eff, string Src)> RealHealsNamed(World w)
    {
        var res = new List<(int, Actor, double, string)>();
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == w.Me.Id))
            foreach (var h in a.Hits.Where(h => h.Kind == HitKind.Heal && h.Target.IsPlayer))
            {
                var max = h.MaxHp > 0 ? h.MaxHp : h.Target.MaxHp;
                var eff = h.HpBefore > 0 && max > 0 ? Math.Min(h.Heal, Math.Max(0, max - h.HpBefore)) : h.Heal;
                res.Add((h.T, h.Target, eff, a.Name));
            }
        foreach (var t in w.R.Ticks.Where(t => t.IsHeal && t.Source?.Id == w.Me.Id && t.Target.IsPlayer))
        {
            var max = t.Target.MaxHp;
            var hp = t.Target.Hp.At(t.T - 1, max);
            res.Add((t.T, t.Target, max > 0 && hp > 0 ? Math.Min(t.Amount, Math.Max(0, max - hp)) : t.Amount,
                t.EffectId switch { 2604 => "Kardia", 3899 => "Eudaimonia", _ => "HoT" }));
        }
        return res.OrderBy(x => x.Item1).ToList();
    }

    /// <summary> Raw healing by source: the rules' (potency x HP per potency) and the real Sage's effective. </summary>
    public static string Breakdown(World w, SageSim sim, double hpPerPot)
    {
        var rules = sim.Heals.GroupBy(h => h.Src).Select(g => (g.Key, V: g.Sum(h => h.Potency) * hpPerPot)).OrderByDescending(x => x.V);
        var real = new Dictionary<string, double>();
        foreach (var a in w.R.Actions.Where(a => a.Source.Id == w.Me.Id))
            foreach (var h in a.Hits.Where(h => h.Kind == HitKind.Heal && h.Target.IsPlayer))
            {
                var max = h.MaxHp > 0 ? h.MaxHp : h.Target.MaxHp;
                real[a.Name] = real.GetValueOrDefault(a.Name) + (h.HpBefore > 0 && max > 0 ? Math.Min(h.Heal, Math.Max(0, max - h.HpBefore)) : h.Heal);
            }
        foreach (var t in w.R.Ticks.Where(t => t.IsHeal && t.Source?.Id == w.Me.Id && t.Target.IsPlayer))
        {
            var k = t.EffectId switch { 2604 => "Kardia", 3899 => "Eudaimonia", _ => $"tick {t.EffectId}" };
            var hp = t.Target.Hp.At(t.T - 1, t.Target.MaxHp);
            real[k] = real.GetValueOrDefault(k) + (hp > 0 ? Math.Min(t.Amount, Math.Max(0, t.Target.MaxHp - hp)) : t.Amount);
        }
        return "rules (raw): " + string.Join(", ", rules.Select(x => $"{x.Key} {x.V / 1e6:0.00}M")) +
               "\n   you (effective): " + string.Join(", ", real.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value / 1e6:0.00}M"));
    }

    public sealed record Segment(int Start, int End, string Label, double Real, double Rules, double LogMin, double CfMin, string Detail = "");

    /// <summary>
    ///     Segments start at each cluster of big hits (20%+ HP, hits under 10 s apart are one cluster). In each segment,
    ///     per player: counterfactual HP = logged HP - the real Sage's effective healing so far in the segment + the
    ///     rules' healing so far, each rules heal capped at the counterfactual missing HP. Compared per segment: the
    ///     rules' effective healing against the real Sage's, and the lowest counterfactual HP% against the lowest logged.
    /// </summary>
    public static List<Segment> Healing(World w, SageSim sim, double hpPerPot) => Healing(w, sim.Heals, hpPerPot);

    /// <summary> The same for any replayed healer's heals (generalised 2026-10-21 for the White Mage). </summary>
    public static List<Segment> Healing(World w, IReadOnlyList<(int T, Actor Target, double Potency, string Src)> simHeals, double hpPerPot)
    {
        var big = w.Raidwides.Where(r => r.AvgPct >= 20).OrderBy(r => r.T).ToList();
        var starts = new List<(int T, string Label)> { (0, "pull") };
        for (var i = 0; i < big.Count; i++)
            if (i == 0 || big[i].T - big[i - 1].T >= 10000) starts.Add((big[i].T, $"{big[i].Name} #{big[i].Ordinal}"));
        var real = RealHealsNamed(w);
        var rules = simHeals.Select(h => (h.T, h.Target, Amount: h.Potency * hpPerPot, h.Src)).OrderBy(h => h.T).ToList();
        var party = w.Party.Where(p => p.MaxHp > 0).ToList();
        var segs = new List<Segment>();
        for (var s = 0; s < starts.Count; s++)
        {
            var s0 = starts[s].T;
            var s1 = s + 1 < starts.Count ? starts[s + 1].T : w.R.EndMs;
            if (s1 <= s0) continue;
            var rcum = party.ToDictionary(p => p.Id, _ => 0.0);
            var qcum = party.ToDictionary(p => p.Id, _ => 0.0);
            var rEv = real.Where(e => e.T >= s0 && e.T < s1).ToList();
            var qEv = rules.Where(e => e.T >= s0 && e.T < s1).ToList();
            int ri = 0, qi = 0;
            double logMin = 100, cfMin = 100, qTot = 0;
            var rBy = new Dictionary<string, double>(); var qBy = new Dictionary<string, double>();
            string worst = "";
            for (var t = s0; t < s1; t += 250)
            {
                while (ri < rEv.Count && rEv[ri].T <= t)
                {
                    if (rcum.ContainsKey(rEv[ri].Target.Id)) { rcum[rEv[ri].Target.Id] += rEv[ri].Eff; rBy[rEv[ri].Src] = rBy.GetValueOrDefault(rEv[ri].Src) + rEv[ri].Eff; }
                    ri++;
                }
                while (qi < qEv.Count && qEv[qi].T <= t)
                {
                    var (qt, tgt, amt, src) = qEv[qi++];
                    if (!qcum.ContainsKey(tgt.Id) || w.Dead(tgt, qt)) continue;
                    var cf = tgt.Hp.At(qt, tgt.MaxHp) - rcum[tgt.Id] + qcum[tgt.Id];
                    var eff = Math.Max(0, Math.Min(amt, tgt.MaxHp - cf));
                    qcum[tgt.Id] += eff;
                    qTot += eff;
                    qBy[src] = qBy.GetValueOrDefault(src) + eff;
                }
                foreach (var p in party)
                {
                    if (w.Dead(p, t)) continue;
                    var hp = p.Hp.At(t, p.MaxHp);
                    if (hp <= 0) continue;
                    logMin = Math.Min(logMin, 100.0 * hp / p.MaxHp);
                    var cfp = 100.0 * Math.Min(p.MaxHp, hp - rcum[p.Id] + qcum[p.Id]) / p.MaxHp;
                    if (cfp < cfMin) { cfMin = cfp; worst = $"{p.Name} ({World.RoleOf(p)}) at {SageSim.F(t)}: logged {100.0 * hp / p.MaxHp:0}%, you healed them {rcum[p.Id] / 1000:0}k, rules {qcum[p.Id] / 1000:0}k"; }
                }
            }
            string Fmt(Dictionary<string, double> d) => string.Join(", ", d.OrderByDescending(x => x.Value).Where(x => x.Value >= 5000).Select(x => $"{x.Key} {x.Value / 1000:0}k"));
            segs.Add(new Segment(s0, s1, starts[s].Label, rcum.Values.Sum(), qTot, logMin, cfMin,
                $"you: {Fmt(rBy)} | rules: {Fmt(qBy)} | worst: {worst}"));
        }
        return segs;
    }
}
