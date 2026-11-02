namespace RuleSim.Actions;

// Who an action lands on, from its sheet row: its single target, or a circle of EffectRange around
// the caster. The sheet doesn't say which side an action affects (Reprisal and Heart of Light differ
// only in radius), so each effect picks Friendly or Hostile.
internal static class Targets
{
    public static IReadOnlyList<IActor> Friendly(ResolveContext ctx)
    {
        if (ctx.Info is not { } info) return [];
        var party = ctx.World.Party.ToList();
        switch (info.CastType)
        {
            case CastType.SingleTarget:
                var target = party.FirstOrDefault(m => m.Id == ctx.TargetId);
                if (target != null && target.Id != ctx.Caster.Id && info.CanTargetParty) return [target];
                return info.CanTargetSelf ? [ctx.Caster] : [];
            case CastType.Circle:
                return party.Where(m => InRange(ctx.Caster, m, info.EffectRange)).ToList();
            default:
                ctx.Warnings.Add($"action {ctx.ActionId} has unsupported cast type {info.CastType}; applied to the caster only");
                return [ctx.Caster];
        }
    }

    public static IReadOnlyList<IActor> Hostile(ResolveContext ctx)
    {
        if (ctx.Info is not { } info) return [];
        var enemies = ctx.World.Enemies;
        switch (info.CastType)
        {
            case CastType.SingleTarget:
                return enemies.Where(e => e.Id == ctx.TargetId).ToList();
            case CastType.Circle:
                return enemies.Where(e => InRange(ctx.Caster, e, info.EffectRange)).ToList();
            default:
                ctx.Warnings.Add($"action {ctx.ActionId} has unsupported cast type {info.CastType}; no enemy hit");
                return [];
        }
    }

    public static IReadOnlyList<IActor> PartyAround(ResolveContext ctx, float range)
        => ctx.World.Party.Where(m => InRange(ctx.Caster, m, range)).ToList();

    // An AoE reaches a target whose hitbox edge is inside it, not just its centre.
    private static bool InRange(IActor caster, IActor target, float range)
    {
        var dx = target.Position.X - caster.Position.X;
        var dz = target.Position.Z - caster.Position.Z;
        var reach = range + target.HitboxRadius;
        return dx * dx + dz * dz <= reach * reach;
    }
}
