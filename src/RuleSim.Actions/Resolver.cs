using RuleSim.Actions.Jobs;

namespace RuleSim.Actions;

// Resolves actions the way the server does. It reads actors through IWorld and returns what changed
// rather than applying it; the only state it writes is the caster's JobState.
public sealed class Resolver(IActionSheet sheet)
{
    // UNVERIFIED for a completed cast.
    public const float InstantLock = 0.6f;
    public const float CastLock = 0.1f;

    public IActionSheet Sheet => sheet;

    private const ushort MeikyoShisui = 1233;

    // In order: the cast-time enablers, the statuses the action spends, its gauge cost, its effects,
    // the job's own state, then the combo, which every earlier step reads as it was before.
    public ActionResult Resolve(IWorld world, ActionRequest request, JobState state, Random rng)
    {
        var result = new ActionResult { ActionId = request.ActionId, Caster = request.Caster };
        if (world.Find(request.Caster) is not { } caster)
        {
            result.Rejected = "caster not found";
            return result;
        }
        var ctx = new ResolveContext(world, sheet, caster, state, request.ActionId, request.Target, rng, result.Statuses)
        {
            ComboRole = Combos.RoleOf(sheet, state, request.ActionId, comboFree: state.Job == Job.Samurai && caster.Statuses.Any(s => s.Id == MeikyoShisui)),
        };
        var completesCast = state.PendingCast != 0 && state.PendingCast == request.ActionId;
        var costWaived = Rules.CostWaivedBy.TryGetValue(request.ActionId, out var waiver) && ctx.Has(caster, waiver);

        CastTime.Resolved(ctx);
        SpendStatuses(ctx);
        if (!costWaived) ApplyCost(ctx);
        if (Rules.Actions.TryGetValue(request.ActionId, out var effects))
            foreach (var effect in effects) effect.Apply(ctx);
        JobOnAction(ctx);
        Combos.Advance(ctx);

        result.AnimationTarget = world.Find(request.Target)?.Id ?? caster.Id;
        result.Targets.Add(result.AnimationTarget);
        foreach (var id in ctx.Hit)
            if (!result.Targets.Contains(id)) result.Targets.Add(id);
        result.Combo = ctx.ComboRole;
        result.Zones.AddRange(ctx.Zones);
        result.Warnings.AddRange(ctx.Warnings);
        if (ctx.GaugeChanged) result.Gauge = (byte[])state.Gauge.Clone();
        result.AnimationLock = Rules.AnimationLocks.TryGetValue(request.ActionId, out var animationLock) ? animationLock
            : completesCast ? CastLock : InstantLock;
        if (Rules.FollowUps.TryGetValue(request.ActionId, out var followUp)) result.FollowUp = followUp;
        return result;
    }

    // A hard cast began: what it spends now. Resolve follows when it completes.
    public ActionResult CastStarted(IWorld world, ActionRequest request, JobState state, Random rng)
    {
        var result = new ActionResult { ActionId = request.ActionId, Caster = request.Caster };
        if (world.Find(request.Caster) is not { } caster)
        {
            result.Rejected = "caster not found";
            return result;
        }
        var ctx = new ResolveContext(world, sheet, caster, state, request.ActionId, request.Target, rng, result.Statuses);
        CastTime.CastStarted(ctx);
        if (ctx.GaugeChanged) result.Gauge = (byte[])state.Gauge.Clone();
        return result;
    }

    public void Interrupted(JobState state) => CastTime.Interrupted(state);

    // A fresh pull: the gauge emptied, then seeded with what the job walks in with.
    public byte[] Begin(IWorld world, IActor actor, JobState state, Job job, Random rng)
    {
        state.Reset(job);
        if (Rules.StartingResources.TryGetValue(job, out var effects))
        {
            var ctx = new ResolveContext(world, sheet, actor, state, 0, 0, rng, []);
            foreach (var effect in effects) effect.Apply(ctx);
        }
        return (byte[])state.Gauge.Clone();
    }

    // One landed auto-attack swing; the gauge when it changed.
    public byte[]? AutoAttack(IWorld world, IActor actor, JobState state, Random rng)
    {
        if (!Rules.AutoAttacks.TryGetValue(state.Job, out var effects)) return null;
        var ctx = new ResolveContext(world, sheet, actor, state, 0, 0, rng, []);
        foreach (var effect in effects) effect.Apply(ctx);
        return ctx.GaugeChanged ? (byte[])state.Gauge.Clone() : null;
    }

    // `moving`: the actor moved or jumped this tick, which ends a dance like Improvisation.
    public TickResult Tick(IWorld world, IActor actor, JobState state, float dt, bool moving, Random rng)
    {
        var result = new TickResult();
        var ctx = new ResolveContext(world, sheet, actor, state, 0, 0, rng, result.Statuses);
        Combos.Tick(state, dt);
        TickTimedGauges(state, dt);
        switch (state.Job)
        {
            case Job.BlackMage: BlackMage.Tick(state, dt); break;
            case Job.Summoner: Summoner.Tick(state, dt); break;
            case Job.Monk: Monk.Tick(state, dt); break;
            case Job.Dancer: Dancer.Tick(ctx, dt, moving); break;
        }
        if (ctx.GaugeChanged) result.Gauge = (byte[])state.Gauge.Clone();
        return result;
    }

    private static void SpendStatuses(ResolveContext ctx)
    {
        foreach (var (status, predicates) in Rules.StatusClearedOnAction)
        {
            if (!ctx.Has(ctx.Caster, status)) continue;
            if (predicates.Any(p => p.Matches(ctx.ActionId, ctx.Info))) ctx.Consume(ctx.Caster, status);
        }
    }

    private static void ApplyCost(ResolveContext ctx)
    {
        if (ctx.Info is not { PrimaryCostValue: > 0 } info) return;
        if (!Rules.CostGauges.TryGetValue((ctx.State.Job, info.PrimaryCostType), out var gauges)) return;
        foreach (var gauge in gauges) gauge.Add(ctx.Gauge, -info.PrimaryCostValue);
    }

    private static void JobOnAction(ResolveContext ctx)
    {
        switch (ctx.State.Job)
        {
            case Job.Samurai: Samurai.OnAction(ctx); break;
            case Job.BlackMage: BlackMage.OnAction(ctx); break;
            case Job.Summoner: Summoner.OnAction(ctx); break;
            case Job.Astrologian: Astrologian.OnAction(ctx); break;
            case Job.Monk: Monk.OnAction(ctx); break;
            case Job.Pictomancer: Pictomancer.OnAction(ctx); break;
            case Job.Dancer: Dancer.OnAction(ctx); break;
            case Job.Viper: Viper.OnAction(ctx); break;
        }
    }

    // Regen fills while below the cap and pauses when full; decay restarts on every change.
    private static void TickTimedGauges(JobState state, float dt)
    {
        var g = state.Gauge;
        foreach (var tg in Rules.TimedGauges)
        {
            if (tg.Job != state.Job) continue;
            var (timer, last) = state.Timers.TryGetValue(tg, out var t) ? t : (0f, -1);
            if (tg.Decay)
            {
                var value = tg.Gauge.Read(g);
                if (value != last)
                {
                    if (value > 0) timer = tg.Seconds;
                    last = value;
                }
                if (value > 0)
                {
                    timer -= dt;
                    tg.TimerField?.Write(g, (int)(timer * 1000f));
                    if (timer <= 0f)
                    {
                        tg.Gauge.Write(g, 0);
                        last = 0;
                    }
                }
            }
            else if (tg.Gauge.Read(g) >= tg.Gauge.Max)
            {
                timer = 0f;
                tg.TimerField?.Write(g, 0);
            }
            else
            {
                timer += dt;
                if (timer >= tg.Seconds)
                {
                    timer -= tg.Seconds;
                    tg.Gauge.Add(g, tg.Amount);
                }
                tg.TimerField?.Write(g, (int)(timer * 1000f));
            }
            state.Timers[tg] = (timer, last);
        }
    }
}
