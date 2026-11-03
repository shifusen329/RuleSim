using RaidReplay.Core.Model;
using WrathCombo.CustomRules;

/// <summary>
///     The rule functions whose meaning doesn't depend on the job: math, enemy casts, the zone, and the world's view
///     of the target, party and raid buffs. Shared by the Sage and Samurai replays (added 2026-10-21 with the Samurai;
///     the Sage's results are unchanged, checked on the 2026-10-05 clear). Same names and meanings as RuleBuiltins.cs.
/// </summary>
internal static class SharedBuiltins
{
    public static double B(bool b) => b ? 1 : 0;

    public static void Fn(Dictionary<string, RuleFunction> f, string name, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) =>
        f[name] = new RuleFunction(name, p, req, bind, "");

    public static void V(Dictionary<string, RuleFunction> f, string name, Func<double> get) => Fn(f, name, [], 0, _ => get);

    /// <summary> Math and logic: gauge, if, min, max, id, learned (everything is learned at level 100), level. </summary>
    public static void AddMath(Dictionary<string, RuleFunction> f)
    {
        ParamKind[] act = [ParamKind.Action];
        Fn(f, "learned", act, 1, _ => () => 1);
        Fn(f, "id", act, 1, a => { double id = a[0].Id; return () => id; });
        Fn(f, "gauge", [ParamKind.Gauge], 1, a => a[0].Value!);
        Fn(f, "if", [ParamKind.Number, ParamKind.Number, ParamKind.Number], 3, a => { Func<double> c = a[0].Value!, x = a[1].Value!, y = a[2].Value!; return () => RuleCompiler.Truthy(c()) ? x() : y(); });
        Fn(f, "min", [ParamKind.Number, ParamKind.Number], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Min(x(), y()); });
        Fn(f, "max", [ParamKind.Number, ParamKind.Number], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Max(x(), y()); });
        V(f, "level", () => 100);
    }

    /// <summary> Enemy casts by name (Wrath's EnemyCasts semantics, from the log) and the zone. </summary>
    public static void AddEnemyCasts(Dictionary<string, RuleFunction> f, World w, Func<int> now)
    {
        ParamKind[] nm = [ParamKind.Name];
        Fn(f, "enemyCast", nm, 1, a => { var n = World.Normalize(a[0].Name!); return () => B(w.Casting(n, now())); });
        Fn(f, "enemyCastIn", nm, 1, a => { var n = World.Normalize(a[0].Name!); return () => w.CastIn(n, now()); });
        Fn(f, "sinceCast", nm, 1, a => { var n = World.Normalize(a[0].Name!); return () => w.Since(n, now()); });
        Fn(f, "firstCast", nm, 1, a => { var n = World.Normalize(a[0].Name!); return () => w.SinceFirst(n, now()); });
        Fn(f, "castsSeen", nm, 1, a => { var n = World.Normalize(a[0].Name!); return () => w.Seen(n, now()); });
        V(f, "zone", () => w.ZoneId);
        V(f, "bossEncounter", () => 1);
        V(f, "inDuty", () => 1);
        // The replays are raids (Dancing Mad, Savage): never a dungeon, so a dungeon-only layer (DUN_AST.json) stands down.
        V(f, "dungeon", () => 0);
    }

    /// <summary> The target, combat and raid buffs as the log shows them. </summary>
    public static void AddWorld(Dictionary<string, RuleFunction> f, World w, Func<int> now, Func<Actor?> target)
    {
        V(f, "inCombat", () => B(now() >= 0));
        V(f, "combatTime", () => now() >= 0 ? now() / 1000.0 : 0);
        V(f, "hasTarget", () => B(target() is not null));
        V(f, "targetHp", () => target() is { } t ? w.HpPct(t, now()) : 0);
        // Time-to-kill is only read outside Dancing Mad (the dungeon checks stand down in zone 1363): unknown here.
        V(f, "ttk", () => 999);
        // The game's auto-target with nothing hostile targeted: the logged player always had the boss targeted when it mattered.
        V(f, "autoTarget", () => 0);
        V(f, "playerHp", () => w.HpPct(w.Me, now()));
        V(f, "boss", () => B(target()?.Kind == ActorKind.Boss));
        V(f, "raidBuffs", () => w.RaidBuffs(target(), now()).Count);
        V(f, "raidLeft", () => w.RaidBuffs(target(), now()).Left);
    }
}
