namespace RuleSim.Actions.Tests;

public class RulesTests
{
    [TestCase(24310u, (ushort)3003, 20f)]   // Holos
    [TestCase(24310u, (ushort)3365, 30f)]   // Holos → Holosakos
    [TestCase(37034u, (ushort)2609, 30f)]   // Eukrasian Prognosis II, through Zoe's boost
    [TestCase(37030u, (ushort)1921, 30f)]   // Helios Conjunction, under Neutral Sect
    [TestCase(25789u, (ushort)2697, 30f)]   // Improvised Finish, scaled by Rising Rhythm
    [TestCase(188u, (ushort)299, 15f)]      // Sacred Soil's zone
    [TestCase(16536u, (ushort)1873, 20f)]   // Temperance, on the party
    [TestCase(7560u, (ushort)1203, 15f)]    // Addle, on the enemy
    public void GrantsOfSeesThroughWrappers(uint actionId, ushort statusId, float duration)
    {
        Assert.That(Rules.GrantsOf(actionId), Does.Contain((statusId, duration)));
    }

    [TestCase(37011u, (ushort)3881)]   // Divine Caress spends Divine Grace
    [TestCase(37031u, (ushort)3895)]   // Sun Sign spends Suntouched
    [TestCase(34686u, (ushort)3686)]   // Tempera Grassa spends the Coat
    public void ARequiredStatusIsSpentByItsAction(uint actionId, ushort statusId)
    {
        Assert.That(Rules.StatusesSpentBy(actionId), Does.Contain(statusId));
    }

    [Test]
    public void EveryRuledActionIsAPlayerAction()
    {
        var missing = Rules.Actions.Keys.Where(id => CsvActionSheet.Instance.Action(id) == null).ToList();
        Assert.That(missing, Is.Empty);
    }
}
