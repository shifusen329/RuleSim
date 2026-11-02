namespace RuleSim.Actions;

// The statuses that make a spell instant, and what spends them:
//  - Swiftcast / Triplecast: the next cast-time spell, below Dualcast in priority, and not when a
//    spell-specific status (Acceleration, Firestarter, Thunderhead) already made it instant.
//  - Dualcast: any action but an ability (category 4), except a cast-time spell Acceleration made
//    instant, which spends Acceleration and keeps Dualcast. A Red Mage's completed hard cast grants it.
// A hard cast spends them when it starts; an instant action when it resolves.
internal static class CastTime
{
    public const ushort SwiftcastStatus = 167, DualcastStatus = 1249, TriplecastStatus = 1211;
    private const ushort Acceleration = 1238, Firestarter = 165, Thunderhead = 3870;
    private const uint SwiftcastAction = 7561, TriplecastAction = 7421, Foul = 7422;
    private const float DualcastSeconds = 15f;

    public static void CastStarted(ResolveContext ctx)
    {
        ctx.State.PendingCast = ctx.ActionId;
        ctx.State.PendingCastGrantsDualcast = Press(ctx);
    }

    public static void Interrupted(JobState state) => state.PendingCast = 0;

    public static void Resolved(ResolveContext ctx)
    {
        if (ctx.State.PendingCast != 0 && ctx.State.PendingCast == ctx.ActionId)
        {
            ctx.State.PendingCast = 0;
            if (ctx.State.PendingCastGrantsDualcast) ctx.Grant(ctx.Caster, DualcastStatus, 0, DualcastSeconds);
            return;
        }
        Press(ctx);
    }

    // Returns whether completing this cast grants Dualcast.
    private static bool Press(ResolveContext ctx)
    {
        var caster = ctx.Caster;
        if (ctx.ActionId == SwiftcastAction) { ctx.Grant(caster, SwiftcastStatus, 0, 10f); return false; }
        if (ctx.ActionId == TriplecastAction) { ctx.Grant(caster, TriplecastStatus, 3, 15f); return false; }

        var isAbility = ctx.Info?.Category == ActionCategory.Ability;
        var isCastTimeSpell = ctx.Info is { Category: ActionCategory.Spell, CastSeconds: > 0f } && ctx.ActionId != Foul;
        var accelerated = isCastTimeSpell && ctx.Has(caster, Acceleration) && ctx.ActionId is 25855 or 25856 or 16526;

        var dualcastSpent = false;
        if (ctx.Has(caster, DualcastStatus) && !isAbility && !accelerated)
        {
            ctx.Remove(caster, DualcastStatus);
            dualcastSpent = true;
        }

        if (isCastTimeSpell && !dualcastSpent && !SpellSpecific(ctx))
        {
            if (ctx.Has(caster, TriplecastStatus)) ctx.Consume(caster, TriplecastStatus);
            else if (ctx.Has(caster, SwiftcastStatus)) ctx.Remove(caster, SwiftcastStatus);
        }

        return isCastTimeSpell && !dualcastSpent && ctx.State.Job == Job.RedMage;
    }

    private static bool SpellSpecific(ResolveContext ctx)
    {
        var c = ctx.Caster;
        var id = ctx.ActionId;
        if (ctx.Has(c, Acceleration) && id is 25855 or 25856 or 16526) return true;
        if (ctx.Has(c, Firestarter) && id == 152) return true;
        if (ctx.Has(c, Thunderhead) && id is 144 or 7447 or 153 or 7420 or 36986 or 36987) return true;
        return false;
    }
}
