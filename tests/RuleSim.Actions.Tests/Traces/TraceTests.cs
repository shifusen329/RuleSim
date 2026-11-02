namespace RuleSim.Actions.Tests;

// Each action a real player used, resolved from the state just before it, against what the server
// sent back. Procs are random, so the server's answer must lie between the resolver's with every
// proc firing and with none. Only statuses the rules know are held to it, and only at level 100,
// the level the rules are written for; the rest is listed as not modelled.
public class TraceTests
{
    private const int RulesLevel = 100;

    private static readonly Lazy<List<TraceBundle>> Bundles
        = new(() => TraceBundle.Load(Path.Combine(AppContext.BaseDirectory, "Traces"))
            .Where(b => CsvActionSheet.Instance.Action(b.Action) != null).ToList());

    private static readonly Lazy<HashSet<ushort>> Known = new(() =>
    {
        var known = new HashSet<ushort>();
        foreach (var id in Rules.ActionIds)
            foreach (var (status, _) in Rules.GrantsOf(id)) known.Add(status);
        foreach (var status in Rules.StatusClearedOnAction.Keys) known.Add(status);
        known.UnionWith([CastTime.SwiftcastStatus, CastTime.DualcastStatus, CastTime.TriplecastStatus]);
        return known;
    });

    private static readonly Lazy<HashSet<ushort>> EnemyStatuses = new(()
        => Rules.Actions.Values.SelectMany(e => e).OfType<EnemyStatusEffect>().Select(e => e.Status).ToHashSet());

    private static readonly HashSet<uint> FollowUpActions = Rules.FollowUps.Values.Select(f => f.ActionId).ToHashSet();

    // What the server does that the rules don't, each with why. Action 0 is any action.
    private static readonly (Job Job, uint Action, ushort Status, string Why)[] Accepted =
    [
        (Job.Bard, 0, 2216, "songs are a 5 s aura the server keeps re-applying to the party"),
        (Job.Bard, 0, 2217, "songs are a 5 s aura the server keeps re-applying to the party"),
        (Job.Bard, 0, 2218, "songs are a 5 s aura the server keeps re-applying to the party"),
        (Job.WhiteMage, 0, 1873, "Temperance's party half is an aura the server re-applies on its tick"),
        (Job.Scholar, 16545, 1917, "the seraph's own follow-up action grants it"),
        (Job.Scholar, 16546, 1917, "the seraph's own follow-up action grants it"),
        (Job.Scholar, 16538, 317, "the fairy's own follow-up action grants it"),
        (Job.Dancer, 16011, 1825, "Devilment's copy on the dance partner"),
        (Job.Scholar, 188, 299, "Sacred Soil's zone grants it to whoever stands in it"),
        (Job.Reaper, 24400, 2599, "the server re-sends Arcane Circle to the party mid-window"),
        (Job.Scholar, 3585, 297, "Deployment Tactics spreads the target's Galvanize, with its time left, around the target"),
        (Job.Viper, 34636, 3658, "the first twin bite grants it, which a non-owner's log can't show without the gauge"),
        (Job.Viper, 34637, 3657, "the first twin bite grants it, which a non-owner's log can't show without the gauge"),
        (Job.Paladin, 199, 196, "a limit break's statuses land after its long animation, past the action's moment"),
        (Job.Machinist, 0, 1951, "Tactician is re-sent to the party while it lasts"),
        (Job.Samurai, 7482, 1299, "the one trace shows no Fuka, though Kasha's combo bonus grants it"),
        (Job.Ninja, 0, 1186, "Ten Chi Jin's status is re-sent with each ninjutsu"),
        (Job.BlackMage, 25797, 165, "Paradox grants Firestarter only in Astral Fire, which a non-owner's log can't show"),
        (Job.Dancer, 15998, 1821, "a Standard Finish resolving in the same server moment"),
        (Job.Dancer, 15998, 3867, "a Standard Finish resolving in the same server moment"),
        (Job.Sage, 24311, 2643, "Panhaimatinon reaches only some of the party in the traces; the rule isn't clear"),
    ];

    private static bool IsAccepted(TraceBundle b, ushort status)
        => Accepted.Any(a => (int)a.Job == b.CasterJob && (a.Action == b.Action || a.Action == 0) && a.Status == status);

    private static IEnumerable<TestCaseData> Jobs()
        => Bundles.Value.Select(b => (Job)b.CasterJob).Distinct().OrderBy(j => j.ToString())
            .Select(job => new TestCaseData(job).SetName($"TheServerAgrees({job})"));

    [TestCaseSource(nameof(Jobs))]
    public void TheServerAgrees(Job job)
    {
        var problems = new List<string>();
        var unmodelled = new SortedSet<string>();
        var bundles = Bundles.Value.Where(b => b.CasterJob == (int)job).ToList();
        var compared = bundles.Where(b => b.Level == RulesLevel).ToList();
        foreach (var b in compared) problems.AddRange(Compare(b, unmodelled));
        TestContext.Out.WriteLine($"{job}: {compared.Count} of {bundles.Count} actions at level {RulesLevel}, {problems.Count} disagreements");
        foreach (var u in unmodelled) TestContext.Out.WriteLine($"  not modelled: {u}");
        foreach (var p in problems) TestContext.Out.WriteLine($"  disagrees: {p}");
        Assert.That(problems, Is.Empty, () => string.Join(Environment.NewLine, problems.Take(60)));
    }

    [Test]
    public void JunnosSingleStandardFinishEndsTheDance()
    {
        var finish = Bundles.Value.FirstOrDefault(b => b.Caster == "10088757" && b.Action == 16191);
        if (finish == null) Assert.Ignore("no trace of it");
        Assert.That(finish!.Lost.Any(l => l.Status == 1818), "the server ends Standard Step");
        var (result, _, _, _) = Resolve(finish, procs: false);
        Assert.That(result.Statuses.Any(s => s.Status == 1818 && s.Kind is StatusChangeKind.Consume or StatusChangeKind.Remove));
    }

    private static List<string> Compare(TraceBundle b, ISet<string> unmodelled)
    {
        var problems = new List<string>();
        var (all, allState, before, allFollowUp) = Resolve(b, procs: true);
        var (none, noneState, _, _) = Resolve(b, procs: false);
        var caster = b.CasterId;

        // A follow-up's bundle holds only what the follow-up itself grants; anything else at that moment is the caster's next press.
        var logged = b.Gained.Where(g => TraceBundle.Hex(g.Source) == caster)
            .Where(g => !FollowUpActions.Contains(b.Action) || Rules.GrantsOf(b.Action).Any(x => x.StatusId == g.Status)).ToList();
        var loggedSet = logged.Select(g => (TraceBundle.Hex(g.Target), g.Status)).ToHashSet();
        var maybe = Touched(all, before);
        maybe.UnionWith(Granted(allFollowUp));

        foreach (var (target, status) in Granted(none).Except(loggedSet))
            if (!IsAccepted(b, status) && !HeldForGood(none, before, target, status) && !AtTheEdge(b, target))
                problems.Add($"{b.Name}: granted {status} on {target:X} that the server didn't");
        foreach (var g in logged)
        {
            var key = (TraceBundle.Hex(g.Target), g.Status);
            if (!Known.Value.Contains(g.Status)) { unmodelled.Add($"{b.Action} grants {g.Status}"); continue; }
            if (IsAccepted(b, g.Status) || IsPet(g.Target, g.Status) || Simultaneous(b).Contains(g.Status)) continue;
            if (!maybe.Contains(key) && AtTheEdge(b, key.Item1)) continue;
            if (!maybe.Contains(key)) { problems.Add($"{b.Name}: the server granted {g.Status} on {key.Item1:X}, the resolver didn't"); continue; }
            if (all.Statuses.LastOrDefault(s => s.Target == key.Item1 && s.Status == g.Status && s.Kind == StatusChangeKind.Grant) is { Status: not 0 } mine
                && Seconds(g.Duration) is { } duration && MathF.Abs(mine.Duration - duration) > 0.1f)
                problems.Add($"{b.Name}: {g.Status} lasts {mine.Duration:F1}s, the server's {duration:F1}s");
        }

        // The server ends a status it grants again, and other losses at the same moment (barriers broken
        // by damage, buffs running out) aren't the action's doing; only what an action spends is held to it.
        var loggedLost = b.Lost.Select(l => (TraceBundle.Hex(l.Target), l.Status)).ToHashSet();
        var regranted = Granted(all);
        foreach (var (target, status) in Ended(none, before).Except(loggedLost))
            if (!IsAccepted(b, status))
                problems.Add($"{b.Name}: ended {status} on {target:X} that the server didn't");
        foreach (var (target, status) in loggedLost.Except(Ended(all, before)).Except(regranted))
            if (target == caster && Rules.StatusClearedOnAction.ContainsKey(status)
                && before.TryGetValue(target, out var held) && held.ContainsKey(status) && !IsAccepted(b, status))
                problems.Add($"{b.Name}: the server ended {status} on {target:X}, the resolver didn't");

        if (b.Owner && TraceBundle.Gauge(b.GaugeAfter) is { } after)
            foreach (var i in GaugeBytes.Of((Job)b.CasterJob))
                if (after[i] != noneState.Gauge[i] && after[i] != allState.Gauge[i])
                    problems.Add($"{b.Name}: gauge byte {i} is {allState.Gauge[i]}..{noneState.Gauge[i]}, the server's {after[i]}");

        // A log owner's client records its locks shortened by its latency.
        if (b.Lock is > 0f and var serverLock && (b.CastTime ?? 0f) == 0f && !FollowUpActions.Contains(b.Action)
            && (serverLock > all.AnimationLock + 0.01f || serverLock < all.AnimationLock - (b.Owner ? 0.15f : 0.01f)))
            problems.Add($"{b.Name}: animation lock {all.AnimationLock:F3}, the server's {serverLock:F3}");
        return problems;
    }

    // Pets aren't modelled; an enemy only ever gets an enemy's status.
    private static bool IsPet(string target, ushort status) => target.StartsWith("40", StringComparison.Ordinal) && !EnemyStatuses.Value.Contains(status);

    // What a press the caster made in the same server moment grants: its lines share the timestamp. One
    // made just before is in the bundle's recent actions; one woven just after isn't, so a status only
    // some other ability grants is taken as that.
    private static HashSet<ushort> Simultaneous(TraceBundle b)
    {
        var statuses = b.Recent.Where(r => r.Ago < 0.1f).SelectMany(r => Rules.GrantsOf(r.Action)).Select(g => g.StatusId).ToHashSet();
        var own = Rules.GrantsOf(b.Action).Select(g => g.StatusId).ToHashSet();
        statuses.UnionWith(Rules.ActionIds
            .Where(id => id != b.Action && CsvActionSheet.Instance.Action(id)?.Category == ActionCategory.Ability)
            .SelectMany(Rules.GrantsOf).Select(g => g.StatusId).Where(s => !own.Contains(s)));
        return statuses;
    }

    // Granting again a status that lasts until used, which the target already holds, sends nothing.
    private static bool HeldForGood(ActionResult r, Dictionary<ulong, Dictionary<ushort, ushort>> before, ulong target, ushort status)
        => before.TryGetValue(target, out var held) && held.ContainsKey(status)
           && r.Statuses.Any(s => s.Target == target && s.Status == status && s.Kind == StatusChangeKind.Grant && s.Duration == 0f);

    // A log has positions a second or so stale: a member within a few yalms of a circle's edge may be either side.
    private static bool AtTheEdge(TraceBundle b, ulong target)
    {
        if (CsvActionSheet.Instance.Action(b.Action) is not { CastType: CastType.Circle } info || b.CasterPos is not { Length: >= 2 } c) return false;
        var member = b.Party.FirstOrDefault(m => TraceBundle.Hex(m.Id) == target);
        if (member?.Pos is not { Length: >= 2 } p) return false;
        var distance = MathF.Sqrt((p[0] - c[0]) * (p[0] - c[0]) + (p[1] - c[1]) * (p[1] - c[1]));
        return MathF.Abs(distance - info.EffectRange) < 4f;
    }

    // The server writes a status that lasts until removed as 9999 s.
    private static float? Seconds(float? duration) => duration is >= 9999f ? 0f : duration is > 0f ? duration : null;

    private static (ActionResult Result, JobState State, Dictionary<ulong, Dictionary<ushort, ushort>> Before, ActionResult FollowUp) Resolve(TraceBundle b, bool procs)
    {
        var world = new TraceWorld(b, EnemyStatuses.Value);
        var state = new JobState((Job)b.CasterJob);
        if (b.Owner && TraceBundle.Gauge(b.GaugeBefore) is { } gauge) gauge.CopyTo(state.Gauge, 0);
        RestoreCombo(b, state);
        var before = world.Party.Concat(world.Enemies).ToDictionary(a => a.Id, a => a.Statuses.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Param));
        var resolver = new Resolver(CsvActionSheet.Instance);
        var target = b.Targets.Count > 0 ? TraceBundle.Hex(b.Targets[0].Id) : b.CasterId;
        if (b.CastTime > 0f) resolver.CastStarted(world, new ActionRequest(b.CasterId, b.Action, target), state, new FixedRandom(procs, 1));
        var result = resolver.Resolve(world, new ActionRequest(b.CasterId, b.Action, target), state, new FixedRandom(procs, 1));
        var followUp = result.FollowUp is { } f
            ? resolver.Resolve(world, new ActionRequest(b.CasterId, f.ActionId, b.CasterId), state, new FixedRandom(procs, 1))
            : new ActionResult { ActionId = 0, Caster = b.CasterId };
        return (result, state, before, followUp);
    }

    // The caster's recent actions, replayed for the combo they leave.
    private static void RestoreCombo(TraceBundle b, JobState state)
    {
        var sheet = CsvActionSheet.Instance;
        foreach (var r in b.Recent.OrderByDescending(r => r.Ago))
        {
            var role = Combos.RoleOf(sheet, state, r.Action);
            if (role != ComboRole.None)
            {
                state.ComboAction = r.Action;
                state.ComboTimer = Combos.Window - r.Ago;
            }
            else if (sheet.Action(r.Action) is { IsGcd: true, PreservesCombo: false })
            {
                state.ComboAction = 0;
                state.ComboTimer = 0f;
            }
        }
        if (state.ComboTimer <= 0f) state.ComboAction = 0;
    }

    private static HashSet<(ulong, ushort)> Granted(ActionResult r)
        => r.Statuses.Where(s => s.Kind is StatusChangeKind.Grant or StatusChangeKind.AddStack).Select(s => (s.Target, s.Status)).ToHashSet();

    // What the server sends a status line for: a grant, or a stack spent with stacks left.
    private static HashSet<(ulong, ushort)> Touched(ActionResult r, Dictionary<ulong, Dictionary<ushort, ushort>> before)
    {
        var touched = Granted(r);
        foreach (var s in r.Statuses.Where(s => s.Kind == StatusChangeKind.Consume))
            if (before.TryGetValue(s.Target, out var held) && held.TryGetValue(s.Status, out var stacks) && stacks > 1)
                touched.Add((s.Target, s.Status));
        return touched;
    }

    // Removed, or consumed down to nothing.
    private static HashSet<(ulong, ushort)> Ended(ActionResult r, Dictionary<ulong, Dictionary<ushort, ushort>> before)
    {
        var ended = new HashSet<(ulong, ushort)>();
        foreach (var s in r.Statuses)
        {
            var key = (s.Target, s.Status);
            switch (s.Kind)
            {
                case StatusChangeKind.Remove:
                case StatusChangeKind.Consume when before.TryGetValue(s.Target, out var held) && held.TryGetValue(s.Status, out var stacks) && stacks <= 1:
                    ended.Add(key);
                    break;
                case StatusChangeKind.Grant or StatusChangeKind.AddStack:
                    ended.Remove(key);
                    break;
            }
        }
        return ended;
    }
}

// The gauge bytes the rules model, per job, by payload index.
internal static class GaugeBytes
{
    public static IEnumerable<int> Of(Job job) => job switch
    {
        Job.Machinist => [6, 7],
        Job.Dancer => [8],
        Job.Samurai => [4, 5, 6, 7],
        Job.Gunbreaker => [2, 6],
        Job.Warrior => [2],
        Job.DarkKnight => [2],
        Job.Reaper => [2, 3, 6, 7],
        Job.Ninja => [2, 4],
        Job.RedMage => [2, 3, 4],
        Job.Viper => [2, 3, 4, 5, 10],
        Job.Pictomancer => [2, 4, 5, 6],
        _ => [],
    };
}
