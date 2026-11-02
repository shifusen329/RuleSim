using static RuleSim.Actions.Gauges;

namespace RuleSim.Actions.Jobs;

// Sen (Setsu 1 / Getsu 2 / Ka 4) drives the Iaijutsu button; Kaeshi the Tsubame-gaeshi mirror
// (Higanbana 1, Goken 2, Setsugekka 3, Namikiri 4).
internal static class Samurai
{
    private const int Setsu = 1, Getsu = 2, Ka = 4;
    private const int Higanbana = 1, Goken = 2, Setsugekka = 3, Namikiri = 4;

    public static void OnAction(ResolveContext ctx)
    {
        var g = ctx.Gauge;
        var combo = ctx.ComboRole == ComboRole.Continuation;
        switch (ctx.ActionId)
        {
            // Sen only from a combo finisher that continues the combo:
            case 7480 when combo: AddSen(g, Setsu); break;   // Yukikaze
            case 7481 when combo: AddSen(g, Getsu); break;   // Gekko
            case 7482 when combo: AddSen(g, Ka); break;      // Kasha
            case 7484 when combo: AddSen(g, Getsu); break;   // Mangetsu
            case 7485 when combo: AddSen(g, Ka); break;      // Oka
            // Iaijutsu spend all Sen and arm the matching Kaeshi:
            case 7489: Iaijutsu(g, Higanbana); break;
            case 7488 or 36965: Iaijutsu(g, Goken); break;        // Tenka Goken / Tendo Goken
            case 7487 or 36966: Iaijutsu(g, Setsugekka); break;   // Midare Setsugekka / Tendo Setsugekka
            case 25781: SamKaeshi.Write(g, Namikiri); break;      // Ogi Namikiri
            case 7495: SamSen.Write(g, 0); break;                 // Hagakure
            // Kaeshi: Goken / Setsugekka / Namikiri, Tendo Kaeshi Goken / Setsugekka:
            case 16485 or 16486 or 25782 or 36967 or 36968: SamKaeshi.Write(g, 0); break;
        }
    }

    private static void AddSen(byte[] g, int sen) => SamSen.Write(g, SamSen.Read(g) | sen);

    private static void Iaijutsu(byte[] g, int kaeshi)
    {
        SamSen.Write(g, 0);
        SamKaeshi.Write(g, kaeshi);
    }
}
