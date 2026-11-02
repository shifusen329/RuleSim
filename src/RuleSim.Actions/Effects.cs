namespace RuleSim.Actions;

// What an action does, composed from leaves (Gauge, Status, ...) and wrappers that gate them
// (Combo, Random, ...). Authored through the helpers in Rules.
internal interface IEffect
{
    void Apply(ResolveContext ctx);

    // The statuses it grants wherever they land, with their durations.
    IEnumerable<(ushort StatusId, float Duration)> Grants => [];
}

internal sealed class GaugeEffect(ResourceGauge gauge, int amount) : IEffect
{
    public void Apply(ResolveContext ctx) => gauge.Add(ctx.Gauge, amount);
}

internal sealed class SetGaugeEffect(ResourceGauge gauge, int value) : IEffect
{
    public void Apply(ResolveContext ctx) => gauge.Add(ctx.Gauge, value - gauge.Read(ctx.Gauge));
}

// On the caster, replacing any copy. `stacks` is the status param: 0 for a plain buff.
internal sealed class StatusEffect(ushort status, float duration, int stacks = 0) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx) => ctx.Grant(ctx.Caster, status, stacks, duration, ctx.ShieldScale);
}

// On the action's friendly recipients. shieldScale sizes a barrier against its base.
internal sealed class TargetStatusEffect(ushort status, float duration, int stacks = 0, float shieldScale = 1f) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx)
    {
        foreach (var member in Targets.Friendly(ctx))
            ctx.Grant(member, status, stacks, duration, shieldScale * ctx.ShieldScale);
    }
}

// On every party member within `range` of the caster, for an action whose row only targets the
// caster (an aura, or a pet's party-wide follow-up).
internal sealed class PartyStatusEffect(ushort status, float duration, float range, float shieldScale = 1f) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx)
    {
        foreach (var member in Targets.PartyAround(ctx, range))
            ctx.Grant(member, status, 0, duration, shieldScale * ctx.ShieldScale);
    }
}

// On the action's friendly recipients other than the caster, who gets its own version (Embolden).
internal sealed class OthersStatusEffect(ushort status, float duration, int stacks = 0) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx)
    {
        foreach (var member in Targets.Friendly(ctx))
            if (member.Id != ctx.Caster.Id) ctx.Grant(member, status, stacks, duration, ctx.ShieldScale);
    }
}

// Ends `statuses` on the action's friendly recipients (Pepsis spending the Eukrasian barriers).
internal sealed class TargetRemoveEffect(ushort[] statuses) : IEffect
{
    public void Apply(ResolveContext ctx)
    {
        foreach (var member in Targets.Friendly(ctx))
            foreach (var status in statuses) ctx.Remove(member, status);
    }
}

// Adds `seconds` to a status the caster holds, up to `cap` (Surging Tempest); grantIfMissing
// starts it at `seconds` when the caster has none.
internal sealed class ExtendEffect(ushort status, float seconds, float cap, bool grantIfMissing) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => grantIfMissing ? [(status, seconds)] : [];

    public void Apply(ResolveContext ctx)
    {
        if (ctx.Find(ctx.Caster, status) is { } held)
            ctx.Grant(ctx.Caster, status, held.Param, MathF.Min(held.Remaining + seconds, cap));
        else if (grantIfMissing)
            ctx.Grant(ctx.Caster, status, 0, seconds);
    }
}

internal sealed class EnemyStatusEffect(ushort status, float duration, int stacks = 0) : IEffect
{
    public ushort Status => status;

    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx)
    {
        foreach (var enemy in Targets.Hostile(ctx))
            ctx.Grant(enemy, status, stacks, duration);
    }
}

// A circle at the caster that gives `status` to whoever stands in it (Sacred Soil).
internal sealed class GroundZoneEffect(ushort status, float radius, float duration) : IEffect
{
    public IEnumerable<(ushort StatusId, float Duration)> Grants => [(status, duration)];

    public void Apply(ResolveContext ctx) => ctx.Zones.Add(new GroundZone(ctx.Caster.Position, radius, status, duration));
}

internal abstract class WrapperEffect(IEffect[] inner) : IEffect
{
    protected IEffect[] Inner => inner;

    public IEnumerable<(ushort StatusId, float Duration)> Grants => inner.SelectMany(e => e.Grants);

    public abstract void Apply(ResolveContext ctx);

    protected void ApplyInner(ResolveContext ctx)
    {
        foreach (var e in inner) e.Apply(ctx);
    }
}

// Only while the caster holds `status` (Neutral Sect's barrier).
internal sealed class IfCasterHasEffect(ushort status, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        if (ctx.Has(ctx.Caster, status)) ApplyInner(ctx);
    }
}

// A held `status` multiplies the inner barriers by `factor` and is spent (Zoe).
internal sealed class BoostEffect(ushort status, float factor, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        var boosted = ctx.Has(ctx.Caster, status);
        if (boosted) ctx.ShieldScale *= factor;
        ApplyInner(ctx);
        if (!boosted) return;
        ctx.ShieldScale /= factor;
        ctx.Remove(ctx.Caster, status);
    }
}

// The caster's stacks of `status` pick the inner barriers' scale, then are spent (Improvised Finish
// on Rising Rhythm). scaleByStacks[n] is the scale at n stacks.
internal sealed class ScaleByStacksEffect(ushort status, float[] scaleByStacks, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        var stacks = ctx.Find(ctx.Caster, status)?.Param ?? 0;
        var scale = scaleByStacks[Math.Clamp((int)stacks, 0, scaleByStacks.Length - 1)];
        ctx.ShieldScale *= scale;
        ApplyInner(ctx);
        ctx.ShieldScale /= scale;
        ctx.Remove(ctx.Caster, status);
    }
}

// Only when the caster didn't hold `status` as the action began (no Monk form under Perfect Balance,
// even on the press that spends its last stack).
internal sealed class IfCasterLackedEffect(ushort status, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        if (!ctx.Caster.Statuses.Any(s => s.Id == status && s.Source == 0)) ApplyInner(ctx);
    }
}

// Only when the caster held `status` as the action began, before it spent anything.
internal sealed class IfCasterHadEffect(ushort status, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        if (ctx.Caster.Statuses.Any(s => s.Id == status && s.Source == 0)) ApplyInner(ctx);
    }
}

// Only when the action continues the caster's linear combo.
internal sealed class ComboEffect(IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        if (ctx.ComboRole == ComboRole.Continuation) ApplyInner(ctx);
    }
}

internal sealed class RandomEffect(float chance, IEffect[] inner) : WrapperEffect(inner)
{
    public override void Apply(ResolveContext ctx)
    {
        if (ctx.Rng.NextSingle() < chance) ApplyInner(ctx);
    }
}

// A condition on the action just used, for the statuses it spends.
internal interface IActionPredicate
{
    bool Matches(uint actionId, ActionInfo? info);
}

internal sealed class ActionIdPredicate(uint id) : IActionPredicate
{
    public uint Id => id;
    public bool Matches(uint actionId, ActionInfo? info) => actionId == id;
}

internal sealed class AnyWeaponskillPredicate : IActionPredicate
{
    public bool Matches(uint actionId, ActionInfo? info) => info?.Category == ActionCategory.Weaponskill;
}

internal sealed class AnyGcdPredicate : IActionPredicate
{
    public bool Matches(uint actionId, ActionInfo? info) => info?.IsGcd == true;
}
