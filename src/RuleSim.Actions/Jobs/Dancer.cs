using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The dance: Standard Step deals two steps and Technical Step four (Emboite 1, Entrechat 2, Jete 3,
// Pirouette 4), the right step advances the step index, and a finish clears them. A finish executes
// as the variant for the steps danced: 16191/16192, 16193-16196, or 16003/16004 with none.
// Improvisation builds a Rising Rhythm stack every 3 s until the dancer moves or acts; Improvised
// Finish spends the stacks first, in its own effects.
internal static class Dancer
{
    private const uint StandardStep = 15997, TechnicalStep = 15998, Emboite = 15999, Pirouette = 16002;
    private const uint Improvisation = 16014;
    private const ushort ImprovisationStatus = 1827, RisingRhythmStatus = 2696;
    private const float RisingRhythmInterval = 3f;
    private const int RisingRhythmMax = 4;

    public static bool IsFinish(uint actionId) => actionId is 16003 or 16004 or >= 16191 and <= 16196;

    public static void OnAction(ResolveContext ctx)
    {
        if (ctx.ActionId == Improvisation) ctx.State.ImprovisationElapsed = 0f;
        else EndImprovisation(ctx);

        var g = ctx.Gauge;
        switch (ctx.ActionId)
        {
            case StandardStep: Deal(ctx, 2); break;
            case TechnicalStep: Deal(ctx, 4); break;
            case >= Emboite and <= Pirouette: Dance(g, (int)(ctx.ActionId - Emboite) + 1); break;
            default:
                if (IsFinish(ctx.ActionId)) Clear(g);
                break;
        }
    }

    public static void Tick(ResolveContext ctx, float dt, bool moving)
    {
        if (ctx.Find(ctx.Caster, ImprovisationStatus) is not { } dance)
        {
            ctx.State.ImprovisationElapsed = 0f;
            return;
        }
        if (moving)
        {
            EndImprovisation(ctx);
            return;
        }
        ctx.State.ImprovisationElapsed += dt;
        if (ctx.State.ImprovisationElapsed < RisingRhythmInterval) return;
        ctx.State.ImprovisationElapsed -= RisingRhythmInterval;
        if ((ctx.Find(ctx.Caster, RisingRhythmStatus)?.Param ?? 0) < RisingRhythmMax)
            ctx.AddStack(ctx.Caster, RisingRhythmStatus, dance.Remaining);
    }

    private static void EndImprovisation(ResolveContext ctx)
    {
        ctx.State.ImprovisationElapsed = 0f;
        ctx.Remove(ctx.Caster, ImprovisationStatus);
        ctx.Remove(ctx.Caster, RisingRhythmStatus);
    }

    // The steps are never the same twice in a row.
    private static void Deal(ResolveContext ctx, int count)
    {
        var g = ctx.Gauge;
        var previous = 0;
        for (var i = 0; i < 4; i++)
        {
            var step = 0;
            if (i < count)
            {
                do step = ctx.Rng.Next(4) + 1;
                while (step == previous);
                previous = step;
            }
            g[StepOffset(i)] = (byte)step;
        }
        DncStepIndex.Write(g, 0);
    }

    private static void Dance(byte[] g, int step)
    {
        var index = DncStepIndex.Read(g);
        if (index < 4 && g[StepOffset(index)] == step) DncStepIndex.Write(g, index + 1);
    }

    private static void Clear(byte[] g)
    {
        for (var i = 0; i < 4; i++) g[StepOffset(i)] = 0;
        DncStepIndex.Write(g, 0);
    }

    private static int StepOffset(int i) => DncStep0.Index + i;
}
