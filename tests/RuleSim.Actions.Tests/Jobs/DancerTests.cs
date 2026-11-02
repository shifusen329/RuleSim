namespace RuleSim.Actions.Tests;

public class DancerTests
{
    private const ushort StandardStep = 1818, TechnicalStep = 1819, StandardFinish = 1821, TechnicalFinish = 1822, LastDanceReady = 3867, FlourishingFinish = 2698;
    private const ushort Improvisation = 1827, RisingRhythm = 2696;
    private const int StepsIndex = 4, StepIndexIndex = 8;

    private static uint StepAction(Bench b, int slot) => 15998u + b.State.Gauge[StepsIndex + slot];

    private static void DanceAll(Bench b, int steps)
    {
        for (var i = 0; i < steps; i++) b.Press(StepAction(b, i));
    }

    [Test]
    public void StandardStepDealsTwoDifferentSteps()
    {
        var b = new Bench(Job.Dancer);
        b.Press(15997);
        Assert.That(b.Player.Has(StandardStep));
        var g = b.State.Gauge;
        Assert.That(g[StepsIndex], Is.InRange(1, 4));
        Assert.That(g[StepsIndex + 1], Is.InRange(1, 4).And.Not.EqualTo(g[StepsIndex]));
        Assert.That(g[StepsIndex + 2], Is.Zero);
        Assert.That(g[StepIndexIndex], Is.Zero);
    }

    [Test]
    public void OnlyTheRightStepAdvances()
    {
        var b = new Bench(Job.Dancer);
        b.Press(15997);
        var wrong = 15998u + (uint)(b.State.Gauge[StepsIndex] % 4 + 1);
        b.Press(wrong);
        Assert.That(b.State.Gauge[StepIndexIndex], Is.Zero);
        b.Press(StepAction(b, 0));
        Assert.That(b.State.Gauge[StepIndexIndex], Is.EqualTo(1));
    }

    [Test]
    public void ADoubleStandardFinishEndsTheDance()
    {
        var b = new Bench(Job.Dancer);
        b.Press(15997);
        DanceAll(b, 2);
        var result = b.Press(16192);
        Assert.That(b.Player.Has(StandardStep), Is.False);
        Assert.That(b.Player.Has(StandardFinish));
        Assert.That(b.Player.Has(LastDanceReady));
        Assert.That(b.State.Gauge[StepsIndex..(StepIndexIndex + 1)], Is.All.Zero);
        Assert.That(result.Gauge, Is.Not.Null);
    }

    [Test]
    public void AStandardFinishWithNoStepGrantsNoFinishBuff()
    {
        var b = new Bench(Job.Dancer);
        b.Press(15997);
        b.Press(16003);
        Assert.That(b.Player.Has(StandardStep), Is.False);
        Assert.That(b.Player.Has(StandardFinish), Is.False);
        Assert.That(b.Player.Has(LastDanceReady));
    }

    [TestCase(16193u)]
    [TestCase(16194u)]
    [TestCase(16195u)]
    [TestCase(16196u)]
    public void EveryTechnicalFinishVariantEndsTheDance(uint finish)
    {
        var b = new Bench(Job.Dancer);
        b.Press(15998);
        DanceAll(b, (int)(finish - 16192));
        var result = b.Press(finish);
        Assert.That(b.Player.Has(TechnicalStep), Is.False);
        Assert.That(b.Player.Has(FlourishingFinish));
        Assert.That(b.State.Gauge[StepsIndex..(StepIndexIndex + 1)], Is.All.Zero);
        Assert.That(result.FollowUp?.ActionId, Is.EqualTo(finish - 16193 + 33215), "the party buff follows as its own action");
    }

    [Test]
    public void TechnicalFinishsFollowUpBuffsThePartyAround()
    {
        var b = new Bench(Job.Dancer);
        var near = new FakeActor(0x10000002, Job.Warrior) { Position = new System.Numerics.Vector3(10f, 0f, 0f) };
        var far = new FakeActor(0x10000003, Job.Sage) { Position = new System.Numerics.Vector3(40f, 0f, 0f) };
        b.World.PartyList.AddRange([near, far]);
        b.Press(33218, Bench.PlayerId);
        Assert.That(b.Player.Has(TechnicalFinish));
        Assert.That(near.Has(TechnicalFinish));
        Assert.That(far.Has(TechnicalFinish), Is.False);
    }

    [Test]
    public void ImprovisationBuildsRisingRhythmUntilTheDancerMoves()
    {
        var b = new Bench(Job.Dancer);
        b.Press(16014);
        for (var i = 0; i < 70; i++) b.Tick(0.1f);
        Assert.That(b.Player.StatusList.Single(s => s.Id == RisingRhythm).Param, Is.EqualTo(2));
        b.Tick(0.1f, moving: true);
        Assert.That(b.Player.Has(Improvisation), Is.False);
        Assert.That(b.Player.Has(RisingRhythm), Is.False);
    }

    [Test]
    public void AnotherActionEndsImprovisation()
    {
        var b = new Bench(Job.Dancer);
        b.Press(16014);
        b.Press(15989);
        Assert.That(b.Player.Has(Improvisation), Is.False);
    }
}
