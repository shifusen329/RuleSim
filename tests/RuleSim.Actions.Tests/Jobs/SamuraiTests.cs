namespace RuleSim.Actions.Tests;

public class SamuraiTests
{
    private const int KaeshiIndex = 4, SenIndex = 7;

    [Test]
    public void ComboFinishersOpenSen()
    {
        var b = new Bench(Job.Samurai);
        b.Press(36963);   // Gyofu
        b.Press(7480);    // Yukikaze
        Assert.That(b.State.Gauge[SenIndex], Is.EqualTo(1));
    }

    [Test]
    public void AFinisherOutOfComboOpensNoSen()
    {
        var b = new Bench(Job.Samurai);
        b.Press(7480);
        Assert.That(b.State.Gauge[SenIndex], Is.Zero);
    }

    // Each Kaeshi costs the Tsubame-gaeshi status its iaijutsu grants.
    [TestCase(7488u, (ushort)3852, 16485u)]    // Tenka Goken → Kaeshi: Goken
    [TestCase(7487u, (ushort)4216, 16486u)]    // Midare Setsugekka → Kaeshi: Setsugekka
    [TestCase(36965u, (ushort)4217, 36967u)]   // Tendo Goken → Tendo Kaeshi Goken
    [TestCase(36966u, (ushort)4218, 36968u)]   // Tendo Setsugekka → Tendo Kaeshi Setsugekka
    public void KaeshiSpendsItsTsubameGaeshi(uint iaijutsu, ushort tsubame, uint kaeshi)
    {
        var b = new Bench(Job.Samurai);
        b.Press(iaijutsu);
        Assert.That(b.Player.Has(tsubame));
        Assert.That(b.State.Gauge[KaeshiIndex], Is.Not.Zero);
        b.Press(kaeshi);
        Assert.That(b.Player.Has(tsubame), Is.False);
        Assert.That(b.State.Gauge[KaeshiIndex], Is.Zero);
    }

    [Test]
    public void UnderMeikyoShisuiAFinisherGetsItsComboBonus()
    {
        var b = new Bench(Job.Samurai);
        b.Press(7499);   // Meikyo Shisui
        b.Press(7481);   // Gekko, out of combo
        Assert.That(b.Player.Has(1298), "Fugetsu");
        Assert.That(b.State.Gauge[SenIndex], Is.EqualTo(2), "Getsu");
        Assert.That(b.Player.StatusList.Single(s => s.Id == 1233).Param, Is.EqualTo(2), "one Meikyo stack spent");
    }
}
