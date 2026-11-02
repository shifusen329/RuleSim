using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// Beast Chakra (three ordered slots, OpoOpo 1 / Raptor 2 / Coeurl 3, with packed per-type counts),
// the Nadi (Lunar 1 / Solar 2) and the blitz window. Chakra opens only inside Perfect Balance; the
// Perfect Balance status is already spent by the GCD that reads it, so its three GCDs are counted here.
internal static class Monk
{
    private const int OpoOpo = 1, Raptor = 2, Coeurl = 3;
    private const int Lunar = 1, Solar = 2;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        switch (ctx.ActionId)
        {
            case 69:   // Perfect Balance (UNVERIFIED: a 20 s window)
                ctx.State.BlitzBuildGcdsLeft = 3;
                MnkBlitzTimer.Write(g, 20000);
                break;
            case 53 or 36945 or 74 or 62 or 25767: OpenChakra(ctx, OpoOpo); break;
            case 54 or 36946 or 61 or 16473: OpenChakra(ctx, Raptor); break;
            case 56 or 36947 or 66 or 70: OpenChakra(ctx, Coeurl); break;
            case 25764 or 36948 or 25765 or 25768 or 25769: ReleaseBlitz(ctx); break;
        }
    }

    // UNVERIFIED: unspent Beast Chakra survives the window running out.
    public static void Tick(JobState state, float dt)
    {
        var g = state.Gauge;
        var ms = MnkBlitzTimer.Read(g);
        if (ms == 0) return;
        var left = ms - dt * 1000f;
        if (left <= 0f)
        {
            MnkBlitzTimer.Write(g, 0);
            state.BlitzBuildGcdsLeft = 0;
        }
        else MnkBlitzTimer.Write(g, (int)left);
    }

    private static void OpenChakra(ResolveContext ctx, int type)
    {
        if (ctx.State.BlitzBuildGcdsLeft <= 0) return;
        ctx.State.BlitzBuildGcdsLeft--;
        var g = ctx.Gauge;
        if (MnkBeastChakra1.Read(g) == 0) MnkBeastChakra1.Write(g, type);
        else if (MnkBeastChakra2.Read(g) == 0) MnkBeastChakra2.Write(g, type);
        else if (MnkBeastChakra3.Read(g) == 0) MnkBeastChakra3.Write(g, type);
        else return;
        Repack(g);
    }

    // All one type opens Lunar, all different Solar, two types whichever is still closed (Lunar
    // first). With both open it's Phantom Rush, which spends them (UNVERIFIED).
    private static void ReleaseBlitz(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        var nadi = MnkNadi.Read(g);
        if ((nadi & Lunar) != 0 && (nadi & Solar) != 0) MnkNadi.Write(g, 0);
        else
        {
            var distinct = Distinct(g);
            if (distinct <= 1) nadi |= Lunar;
            else if (distinct >= 3) nadi |= Solar;
            else nadi |= (nadi & Lunar) != 0 ? Solar : Lunar;
            MnkNadi.Write(g, nadi);
        }
        MnkBeastChakra1.Write(g, 0);
        MnkBeastChakra2.Write(g, 0);
        MnkBeastChakra3.Write(g, 0);
        MnkBeastChakraStacks.Write(g, 0);
        ctx.State.BlitzBuildGcdsLeft = 0;
    }

    private static int Distinct(byte[] g)
    {
        var a = MnkBeastChakra1.Read(g);
        var b = MnkBeastChakra2.Read(g);
        var c = MnkBeastChakra3.Read(g);
        var n = 0;
        if (a != 0) n++;
        if (b != 0 && b != a) n++;
        if (c != 0 && c != a && c != b) n++;
        return n;
    }

    private static void Repack(byte[] g)
    {
        int[] slots = [MnkBeastChakra1.Read(g), MnkBeastChakra2.Read(g), MnkBeastChakra3.Read(g)];
        var opo = slots.Count(s => s == OpoOpo);
        var raptor = slots.Count(s => s == Raptor);
        var coeurl = slots.Count(s => s == Coeurl);
        MnkBeastChakraStacks.Write(g, (opo & 3) | ((raptor & 3) << 2) | ((coeurl & 3) << 4));
    }
}
