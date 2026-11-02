using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// The painted canvases (Pom 1, Wing 2, Claw 4, Maw 8, Weapon 16, Landscape 32) and the creature
// cycle (Pom 1, Wings 2, Claw 4, Moogle portrait 16, Madeen portrait 32). Palette, paint and the
// Aetherhues / Hammer Time / Starry statuses are in Rules.
internal static class Pictomancer
{
    private const int CanvasPom = 1, CanvasWing = 2, CanvasClaw = 4, CanvasMaw = 8, CanvasWeapon = 16, CanvasLandscape = 32;
    private const int Pom = 1, Wings = 2, Claw = 4, Moogle = 16, Madeen = 32;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        var canvas = PctCanvas.Read(g);
        var creature = PctCreature.Read(g);
        switch (ctx.ActionId)
        {
            case 34664: canvas |= CanvasPom; break;         // Pom Motif
            case 34665: canvas |= CanvasWing; break;        // Wing Motif
            case 34666: canvas |= CanvasClaw; break;        // Claw Motif
            case 34667: canvas |= CanvasMaw; break;         // Maw Motif
            case 34668: canvas |= CanvasWeapon; break;      // Hammer Motif
            case 34669: canvas |= CanvasLandscape; break;   // Starry Sky Motif

            // UNVERIFIED: the creature bits accumulate Pom → Wings → Claw; Winged Muse readies the
            // Moogle, Fanged Muse the Madeen and clears the cycle.
            case 34670:   // Pom Muse
                canvas &= ~CanvasPom;
                creature |= Pom;
                break;
            case 34671:   // Winged Muse
                canvas &= ~CanvasWing;
                creature |= Wings;
                if ((creature & Pom) != 0) creature |= Moogle;
                break;
            case 34672:   // Clawed Muse
                canvas &= ~CanvasClaw;
                creature |= Claw;
                break;
            case 34673:   // Fanged Muse
                canvas &= ~CanvasMaw;
                if ((creature & (Pom | Wings | Claw)) == (Pom | Wings | Claw)) creature |= Madeen;
                creature &= ~(Pom | Wings | Claw);
                break;

            case 34674: canvas &= ~CanvasWeapon; break;      // Striking Muse
            case 34675: canvas &= ~CanvasLandscape; break;   // Starry Muse
            case 34676: creature &= ~Moogle; break;          // Mog of the Ages
            case 34677: creature &= ~Madeen; break;          // Retribution of the Madeen
            default: return;
        }
        PctCanvas.Write(g, canvas);
        PctCreature.Write(g, creature);
    }
}
