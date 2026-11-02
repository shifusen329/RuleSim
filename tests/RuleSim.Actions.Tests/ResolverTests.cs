namespace RuleSim.Actions.Tests;

public class ResolverTests
{
    private const int HeatIndex = 6, BatteryIndex = 7;

    [Test]
    public void AnUpgradedComboStepContinuesTheBaseCombo()
    {
        var b = new Bench(Job.Machinist);
        Assert.That(b.Press(7411).Combo, Is.EqualTo(ComboRole.Starter));
        Assert.That(b.Press(7412).Combo, Is.EqualTo(ComboRole.Continuation));
        Assert.That(b.Press(7413).Combo, Is.EqualTo(ComboRole.Continuation));
        Assert.That(b.State.Gauge[HeatIndex], Is.EqualTo(15));
        Assert.That(b.State.Gauge[BatteryIndex], Is.EqualTo(10));
    }

    [Test]
    public void AComboStepOutOfOrderGetsNoBonus()
    {
        var b = new Bench(Job.Machinist);
        Assert.That(b.Press(7413).Combo, Is.EqualTo(ComboRole.None));
        Assert.That(b.State.Gauge[BatteryIndex], Is.Zero);
    }

    [Test]
    public void TheComboRunsOut()
    {
        var b = new Bench(Job.Machinist);
        b.Press(7411);
        b.Tick(31f);
        Assert.That(b.Press(7412).Combo, Is.EqualTo(ComboRole.None));
    }

    [Test]
    public void AnotherGcdBreaksTheCombo()
    {
        var b = new Bench(Job.Machinist);
        b.Press(7411);
        b.Press(2870);   // Spread Shot
        Assert.That(b.Press(7412).Combo, Is.EqualTo(ComboRole.None));
    }

    [Test]
    public void AGcdTheSheetMarksAsPreservingKeepsTheCombo()
    {
        var b = new Bench(Job.Machinist);
        b.Press(7411);
        b.Press(16500);   // Air Anchor
        Assert.That(b.Press(7412).Combo, Is.EqualTo(ComboRole.Continuation));
    }

    [Test]
    public void TheEffectIsAimedAtTheTargetAndListsWhoItReached()
    {
        var b = new Bench(Job.Paladin);
        var tank = new FakeActor(0x10000002, Job.Warrior);
        b.World.PartyList.Add(tank);
        var result = b.Press(7382, tank.Id);   // Intervention
        Assert.That(result.AnimationTarget, Is.EqualTo(tank.Id));
        Assert.That(result.Targets, Is.EqualTo(new[] { tank.Id }));
        Assert.That(tank.Has(1174));
    }

    [Test]
    public void AnEnemyDebuffLandsOnTheTarget()
    {
        var b = new Bench(Job.Monk);
        var result = b.Press(7549);   // Feint
        Assert.That(result.AnimationTarget, Is.EqualTo(Bench.BossId));
        Assert.That(b.Boss.Has(1195));
    }

    [Test]
    public void AStackingBuffLosesOneStackPerUse()
    {
        var b = new Bench(Job.Paladin);
        b.Press(7383);   // Requiescat, 4 stacks
        b.Press(16459);  // Confiteor
        Assert.That(b.Player.StatusList.Single(s => s.Id == 1368).Param, Is.EqualTo(3));
    }

    [Test]
    public void ZoeBoostsTheNextPrognosisAndIsSpent()
    {
        var b = new Bench(Job.Sage);
        b.Press(24300);
        var result = b.Press(37034, Bench.PlayerId);
        Assert.That(result.Statuses.Single(s => s.Status == 2609).ShieldScale, Is.EqualTo(1.5f));
        Assert.That(b.Player.Has(2611), Is.False);
    }

    [Test]
    public void SwiftcastIsSpentByTheNextCastTimeSpell()
    {
        var b = new Bench(Job.WhiteMage);
        b.Press(7561);
        Assert.That(b.Player.Has(167));
        b.Press(135, Bench.PlayerId);   // Cure II
        Assert.That(b.Player.Has(167), Is.False);
    }

    [Test]
    public void ACompletedRedMageCastGrantsDualcast()
    {
        var b = new Bench(Job.RedMage);
        var request = new ActionRequest(Bench.PlayerId, 25855, Bench.BossId);   // Verthunder III
        b.Player.Apply(b.Resolver.CastStarted(b.World, request, b.State, b.Rng).Statuses);
        var result = b.Press(25855);
        Assert.That(b.Player.Has(1249));
        Assert.That(result.AnimationLock, Is.EqualTo(Resolver.CastLock));
    }

    [Test]
    public void AGaugeCostComesFromTheSheet()
    {
        var b = new Bench(Job.Machinist);
        b.State.Gauge[HeatIndex] = 60;
        b.Press(17209);   // Hypercharge, 50 Heat
        Assert.That(b.State.Gauge[HeatIndex], Is.EqualTo(10));
    }

    [Test]
    public void LiliesFillOnTheirTimer()
    {
        var b = new Bench(Job.WhiteMage);
        for (var i = 0; i < 41; i++) b.Tick(0.5f);
        Assert.That(b.State.Gauge[6], Is.EqualTo(1));
    }

    [Test]
    public void APaladinStartsWithAFullOathGauge()
    {
        var b = new Bench(Job.Paladin);
        var gauge = b.Resolver.Begin(b.World, b.Player, b.State, Job.Paladin, b.Rng);
        Assert.That(gauge[0], Is.EqualTo((byte)Job.Paladin));
        Assert.That(gauge[2], Is.EqualTo(100));
    }
}
