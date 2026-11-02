using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The two oGCD-combo states. The serpent combo is (follow-up << 2) | uses left: the follow-up the
// Serpent's Tail / Twinfang / Twinblood buttons become, and how many of them remain. The dread combo
// is the coil or den used first after Vicewinder / Vicepit (both still open while it's 1 or 4), or
// the Reawaken generation; it runs on its own 30 s timer. The twin bite used first grants the other
// one's venom. Coils, tribute, offerings and the other venoms are in Rules.
internal static class Viper
{
    private const int DeathRattle = 1, LastLash = 2, FirstLegacy = 3, TwinBites = 7, TwinThreshes = 8, UncoiledTwins = 9;
    private const int Dreadwinder = 1, HuntersCoil = 2, SwiftskinsCoil = 3, PitOfDread = 4, HuntersDen = 5, SwiftskinsDen = 6;
    private const int Reawakened = 7;
    private const int DreadMs = 30000;
    private const ushort HuntersVenom = 3657, SwiftskinsVenom = 3658, FellhuntersVenom = 3659, FellskinsVenom = 3660;
    private const float VenomSeconds = 30f;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        switch (ctx.ActionId)
        {
            case 34610 or 34611 or 34612 or 34613: Serpent(g, DeathRattle, 1); break;   // the single-target finishers
            case 34618 or 34619: Serpent(g, LastLash, 1); break;                         // the AoE finishers
            case 34634 or 34635 or 34640 or 34641 or 34642 or 34643: Serpent(g, 0, 0); break;   // Death Rattle, Last Lash, the Legacies

            case 34620: Dread(g, Dreadwinder); break;   // Vicewinder
            case 34623: Dread(g, PitOfDread); break;    // Vicepit
            case 34621: Coil(g, Dreadwinder, HuntersCoil, TwinBites); break;
            case 34622: Coil(g, Dreadwinder, SwiftskinsCoil, TwinBites); break;
            case 34624: Coil(g, PitOfDread, HuntersDen, TwinThreshes); break;
            case 34625: Coil(g, PitOfDread, SwiftskinsDen, TwinThreshes); break;
            case 34636: Twin(ctx, SwiftskinsVenom); break;   // Twinfang Bite
            case 34637: Twin(ctx, HuntersVenom); break;      // Twinblood Bite
            case 34638: Twin(ctx, FellskinsVenom); break;    // Twinfang Thresh
            case 34639: Twin(ctx, FellhuntersVenom); break;  // Twinblood Thresh
            case 34633: Serpent(g, UncoiledTwins, 2); break; // Uncoiled Fury
            case 34644 or 34645: Twin(ctx, 0); break;        // Uncoiled Twinfang / Twinblood

            case 34626: Dread(g, Reawakened); break;         // Reawaken
            case >= 34627 and <= 34629:                      // First to Third Generation
                Dread(g, Reawakened + (int)(ctx.ActionId - 34626));
                Serpent(g, FirstLegacy + (int)(ctx.ActionId - 34627), 1);
                break;
            case 34630:                                      // Fourth Generation
                Dread(g, 0);
                Serpent(g, FirstLegacy + 3, 1);
                break;
            case 34631: Dread(g, 0); break;                  // Ouroboros
        }
    }

    private static void Serpent(byte[] g, int followUp, int uses) => VprSerpentCombo.Write(g, followUp << 2 | uses);

    private static void Dread(byte[] g, int state)
    {
        VprDreadCombo.Write(g, state);
        VprDreadTimer.Write(g, state is Dreadwinder or PitOfDread ? DreadMs : state == 0 ? 0 : VprDreadTimer.Read(g));
    }

    // The first of the pair leaves its own state for the other; the second ends the combo.
    private static void Coil(byte[] g, int opened, int coil, int twins)
    {
        if (VprDreadCombo.Read(g) == opened) VprDreadCombo.Write(g, coil);
        else Dread(g, 0);
        Serpent(g, twins, 2);
    }

    private static void Twin(ResolveContext ctx, ushort otherVenom)
    {
        var g = ctx.Gauge;
        var combo = VprSerpentCombo.Read(g);
        var uses = combo & 3;
        if (uses == 2 && otherVenom != 0) ctx.Grant(ctx.Caster, otherVenom, 0, VenomSeconds);
        VprSerpentCombo.Write(g, uses > 1 ? combo - 1 : 0);
    }
}
