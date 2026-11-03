using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using WrathCombo.CustomRules;

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) pass++;
    else
    {
        fail++;
        Console.WriteLine($"FAIL {name} {detail}");
    }
}

#region Fakes

var actions = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    { ["Senei"] = 1, ["Gekko"] = 2, ["Jinpu"] = 3, ["Hakaze"] = 4, ["Gyofu"] = 5 };
var statuses = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
    { ["Tendo"] = 10, ["Buffs.Tendo"] = 10 };
double kenki = 40;
var buffs = new HashSet<uint>();
var symbols = new FakeSymbols(actions, statuses, new() { ["Kenki"] = () => kenki });

double B(bool b) => b ? 1 : 0;
var functions = new Dictionary<string, RuleFunction>(StringComparer.OrdinalIgnoreCase);
void Fn(string name, ParamKind[] p, int req, Func<RuleArg[], Func<double>> bind) =>
    functions[name] = new RuleFunction(name, p, req, bind, "");
Fn("buff", [ParamKind.Status], 1, a => { var id = a[0].Id; return () => B(buffs.Contains(id)); });
Fn("id", [ParamKind.Action], 1, a => { double id = a[0].Id; return () => id; });
Fn("gauge", [ParamKind.Gauge], 1, a => a[0].Value!);
Fn("justUsed", [ParamKind.Action, ParamKind.Number], 1, a =>
{
    var s = a[1].Value; return () => s is null ? 3 : s();
});
Fn("if", [ParamKind.Number, ParamKind.Number, ParamKind.Number], 3, a =>
{
    Func<double> t = a[0].Value!, x = a[1].Value!, y = a[2].Value!;
    return () => RuleCompiler.Truthy(t()) ? x() : y();
});
Fn("boom", [], 0, _ => () => throw new InvalidOperationException("boom"));
Fn("nameLen", [ParamKind.Name], 1, a => { var n = a[0].Name!; return () => n.Length; });
Fn("gcd", [], 0, _ => () => 2.5);
// As RuleBuiltins' (for the truth tables of rule files that use them).
Fn("min", [ParamKind.Number, ParamKind.Number], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Min(x(), y()); });
Fn("max", [ParamKind.Number, ParamKind.Number], 2, a => { Func<double> x = a[0].Value!, y = a[1].Value!; return () => Math.Max(x(), y()); });

var deferredErrorChecks = new List<(string, string, string)>();
void ErrorContainsLater(string name, string src, string expected) => deferredErrorChecks.Add((name, src, expected));

double Eval(string src, Dictionary<string, string>? defines = null) =>
    new RuleCompiler(functions, symbols, defines).Compile(src)();

string Error(string src, Dictionary<string, string>? defines = null)
{
    try
    {
        var c = new RuleCompiler(functions, symbols, defines);
        if (defines is not null)
            foreach (var d in defines.Keys) c.GetDefine(d);
        c.Compile(src);
        return "(no error)";
    }
    catch (RuleCompileException e) { return e.Message; }
}

#endregion

#region Expression language

Check("precedence", Eval("1 + 2 * 3 == 7") == 1);
Check("left assoc -", Eval("10 - 2 - 3") == 5);
Check("left assoc /", Eval("8 / 2 / 2") == 2);
Check("&& over ||", Eval("1 || 0 && 0") == 1);
Check("parens", Eval("(1 || 0) && 0") == 0);
Check("unary not", Eval("!0 && 1") == 1);
Check("not binds tight", Eval("!1 == 0") == 1);
Check("unary minus", Eval("-2 < 1") == 1);
Check("decimal", Eval("0.5 + .25 == 0.75") == 1);
Check("true/false", Eval("true && !false") == 1);
Check("short-circuit &&", Eval("0 && boom") == 0);
Check("short-circuit ||", Eval("1 || boom") == 1);
Check("zero-arg call parens", Eval("gcd() * 2 == gcd * 2") == 1);
Check("optional arg omitted", Eval("justUsed(Senei)") == 3);
Check("optional arg given", Eval("justUsed(Senei, gcd * 3)") == 7.5);
Check("numeric action id", Eval("id(42)") == 42);
Check("case-insensitive", Eval("ID(senei) == Id(SENEI)") == 1);
Check("qualified status", Eval("buff(Buffs.Tendo) == buff(Tendo)") == 1);
Check("if()", Eval("if(1, 25, 0) + if(0, 50, 7)") == 32);
Check("name argument passed as text", Eval("nameLen(LightOfJudgment)") == 15 && Eval("nameLen(Senei)") == 5);
ErrorContainsLater("name argument must be a bare name", "nameLen(1 + 1)", "expects a name");

buffs.Add(10);
Check("buff live", Eval("buff(Tendo)") == 1);
buffs.Clear();
Check("buff live after change", Eval("buff(Tendo)") == 0);

var gaugeExpr = new RuleCompiler(functions, symbols).Compile("gauge(Kenki) >= 50");
kenki = 40;
Check("gauge live 40", gaugeExpr() == 0);
kenki = 75;
Check("gauge live 75", gaugeExpr() == 1);

var defs = new Dictionary<string, string> { ["a"] = "b + 1", ["b"] = "2", ["spare"] = "gauge(Kenki) - if(buff(Tendo), 25, 0)" };
Check("defines nest", Eval("a * 2", defs) == 6);
Check("define case-insensitive", Eval("A + B", defs) == 5);
kenki = 60; buffs.Add(10);
Check("define with gauge", Eval("spare", defs) == 35);
buffs.Clear();

#endregion

#region Error messages

void ErrorContains(string name, string src, string expected, Dictionary<string, string>? d = null)
{
    var msg = Error(src, d);
    Check(name, msg.Contains(expected, StringComparison.OrdinalIgnoreCase), $"-> \"{msg}\"");
}

foreach (var (n, s, e) in deferredErrorChecks) ErrorContains(n, s, e);
ErrorContains("unknown action", "id(Nope)", "Unknown action 'Nope'");
ErrorContains("bare action", "Senei && 1", "'Senei' is an action");
ErrorContains("bare status", "Tendo", "'Tendo' is a status");
ErrorContains("unknown name", "kenkii > 1", "Unknown name 'kenkii'");
ErrorContains("unknown function", "foo(1)", "Unknown function 'foo'");
ErrorContains("arity", "id()", "takes 1 argument");
ErrorContains("arity optional", "justUsed(Senei, 1, 2)", "takes 1-2 arguments");
ErrorContains("needs args", "id + 1", "needs arguments");
ErrorContains("single =", "gcd = 1", "Use '=='");
ErrorContains("single &", "1 & 1", "Use '&&'");
ErrorContains("chained cmp", "1 < 2 < 3", "can't be chained");
ErrorContains("gauge field", "gauge(Kenkii)", "not a gauge field");
ErrorContains("unclosed", "(1 + 2", "Expected ')'");
ErrorContains("trailing", "1 2", "Unexpected '2'");
ErrorContains("empty", "   ", "empty");
ErrorContains("column", "1 + $", "column 5");
ErrorContains("action arg kind", "id(1 + 1)", "expects an action");
ErrorContains("define cycle", "a", "Define cycle", new() { ["a"] = "b", ["b"] = "a" });
ErrorContains("define error names innermost", "x", "In define 'y'", new() { ["x"] = "y + 1", ["y"] = "nope" });

#endregion

#region Rule sets

var runtime = new FakeRuntime { Hooks = { [4] = 5 } };
var errors = new List<string>();
var file = new RuleSetFile
{
    Job = "SAM",
    Replaces = ["Hakaze", "Gyofu"],
    Define = new() { ["tendo"] = "buff(Tendo)" },
    Rules =
    [
        new RuleFile { Name = "Senei", Action = "Senei", Weave = true, When = "tendo" },
        new RuleFile { Name = "Off", Action = "Jinpu", Enabled = false },
        new RuleFile { Name = "Throws", Action = "Jinpu", When = "boom" },
        new RuleFile { Name = "Gekko", Action = "Gekko" },
        new RuleFile { Name = "Starter", Action = "Hakaze", Hook = true },
    ],
};
var set = RuleSetCompiler.Compile(file, "test", symbols, _ => functions, errors);
Check("compiles", set is not null && errors.Count == 0, string.Join("; ", errors));
Check("replaces resolved", set!.Replaces.SetEquals([4u, 5u]));

buffs.Add(10);
runtime.Ready = [1, 2, 3, 4, 5];
runtime.Weave = false;
var result = set.Evaluate(4, runtime);
Check("no weave -> skips oGCD", set.LastStatus[0] == RuleStatus.NoWeaveWindow);
Check("disabled", set.LastStatus[1] == RuleStatus.Disabled);
Check("throwing rule -> error, continues", set.LastStatus[2] == RuleStatus.Error && set.LastError!.Contains("boom"));
Check("first ready rule fires", result == 2 && set.LastStatus[3] == RuleStatus.Fired && set.LastFired == 3);
Check("rules below not reached", set.LastStatus[4] == RuleStatus.NotReached);

runtime.Weave = true;
runtime.WeaveCalls = 0;
Check("weave -> oGCD fires", set.Evaluate(4, runtime) == 1);
Check("canWeave asked once", runtime.WeaveCalls == 1, $"calls={runtime.WeaveCalls}");

buffs.Clear();
runtime.Ready = [1, 4, 5];
result = set.Evaluate(4, runtime);
Check("condition false", set.LastStatus[0] == RuleStatus.ConditionFalse);
Check("not ready", set.LastStatus[3] == RuleStatus.NotReady);
Check("hook applied", result == 5 && set.LastResult == 5);

runtime.Ready = [];
Check("nothing fires -> 0 (pass)", set.Evaluate(4, runtime) == 0 && set.LastFired == -1);

errors.Clear();
var broken = new RuleSetFile
{
    Job = "SAM",
    Replaces = ["Nope"],
    Define = new() { ["bad"] = "gauge(Nope)" },
    Rules = [new RuleFile { Action = "Missing" }, new RuleFile { Action = "Gekko", When = "1 +" }, new RuleFile { When = "1" }],
};
Check("broken set -> null", RuleSetCompiler.Compile(broken, "b", symbols, _ => functions, errors) is null);
Check("all errors collected", errors.Count == 5, string.Join(" | ", errors));

errors.Clear();
file.When = "buff(Tendo)";
var gated = RuleSetCompiler.Compile(file, "gated", symbols, _ => functions, errors)!;
runtime.Ready = [1, 2, 3, 4, 5];
buffs.Clear();
Check("set when false -> pass", gated.Evaluate(4, runtime) == 0 && gated.LastInactive && gated.LastFired == -1);
buffs.Add(10);
Check("set when true -> runs", gated.Evaluate(4, runtime) != 0 && !gated.LastInactive);
file.When = "nope";
errors.Clear();
Check("bad set when -> error", RuleSetCompiler.Compile(file, "bad", symbols, _ => functions, errors) is null && errors.Any(e => e.StartsWith("when:")), string.Join(" | ", errors));
file.When = null;

errors.Clear();
var layered = RuleSetCompiler.Compile(new RuleSetFile { Job = "SAM", Layer = true, Replaces = ["Hakaze"], Rules = [new RuleFile { Action = "Senei" }] }, "layer", symbols, _ => functions, errors)!;
Check("layer flag kept", layered is not null && layered.Layer && !set.Layer, string.Join(" | ", errors));

// Targets: parsed per rule, applied through the runtime when the rule fires.
errors.Clear();
var targeted = RuleSetCompiler.Compile(new RuleSetFile
{
    Job = "SAM", Replaces = ["Hakaze"],
    Rules =
    [
        new RuleFile { Name = "Heal", Action = "Senei", Target = "Heal", When = "buff(Tendo)" },
        new RuleFile { Name = "Tank", Action = "Gekko", Target = "tank" },
        new RuleFile { Name = "Plain", Action = "Jinpu" },
    ],
}, "targets", symbols, _ => functions, errors)!;
Check("targets compile", targeted is not null && errors.Count == 0, string.Join(" | ", errors));
Check("target parsed (case-insensitive)", targeted!.Rules[0].Target is [{ Kind: RuleTarget.Heal }] && targeted.Rules[1].Target is [{ Kind: RuleTarget.Tank }] && targeted.Rules[2].Target.Length == 0 && targeted.Rules[2].TargetSource is null);
runtime.Ready = [1, 2, 3]; runtime.Weave = true; runtime.Retargets.Clear();
buffs.Clear();
Check("target rule fires through Retarget", targeted.Evaluate(4, runtime) == 2 && runtime.Retargets is [(2, 4, RuleTarget.Tank)]);
runtime.Ready = [3];
Check("untargeted rule skips Retarget", targeted.Evaluate(4, runtime) == 3 && runtime.Retargets.Count == 1);
// A target list that finds nobody skips the rule (status NoTarget) and the next rule runs.
runtime.Ready = [2, 3]; runtime.Retargets.Clear(); runtime.NoTargetFor.Add(RuleTarget.Tank);
Check("target that finds nobody skips the rule", targeted.Evaluate(4, runtime) == 3 && targeted.LastStatus[1] == RuleStatus.NoTarget && targeted.LastFired == 2);
runtime.NoTargetFor.Clear();
errors.Clear();
Check("unknown target -> error", RuleSetCompiler.Compile(new RuleSetFile { Job = "SAM", Replaces = ["Hakaze"], Rules = [new RuleFile { Action = "Senei", Target = "nearest" }] }, "t", symbols, _ => functions, errors) is null
                                 && errors.Any(e => e.Contains("unknown target 'nearest'")), string.Join(" | ", errors));
Check("RuleTargets synonyms", RuleTargets.TryParse("lowestHp", symbols, out var lt, out _) && lt is [{ Kind: RuleTarget.Lowest }]
                              && RuleTargets.TryParse("", symbols, out var ct, out _) && ct.Length == 0
                              && RuleTargets.TryParse("current", symbols, out var ct2, out _) && ct2.Length == 0
                              && !RuleTargets.TryParse("x", symbols, out _, out _));
// Target lists with filters: "tank !shield, self !Tendo, dps".
Check("target list parses", RuleTargets.TryParse("tank !shield, self !Tendo, DPS Buffs.Tendo", symbols, out var chain, out var chainErr)
                            && chain.Length == 3 && chain[0].Kind == RuleTarget.Tank && chain[0].Filters is [{ Negate: true, AnyShield: true }]
                            && chain[1].Kind == RuleTarget.Self && chain[1].Filters is [{ Negate: true, StatusId: 10, AnyShield: false }]
                            && chain[2].Kind == RuleTarget.Dps && chain[2].Filters is [{ Negate: false, StatusId: 10 }], chainErr ?? "");
Check("target list: unknown status", !RuleTargets.TryParse("tank !Nope", symbols, out _, out var e1) && e1!.Contains("unknown status 'Nope'"), e1 ?? "");
Check("target list: current alone", !RuleTargets.TryParse("current, self", symbols, out _, out var e2) && e2!.Contains("'current'"), e2 ?? "");
Check("target list: dot(4)", RuleTargets.TryParse("dot(4)", symbols, out var dot4, out _) && dot4[0].Kind == RuleTarget.Dot && dot4[0].MaxEnemies == 4);
Check("target list: dot alone has no enemy limit", RuleTargets.TryParse("dot", symbols, out var dot0, out _) && dot0[0].Kind == RuleTarget.Dot && dot0[0].MaxEnemies == 0);
Check("target list: dot(0) refused", !RuleTargets.TryParse("dot(0)", symbols, out _, out var e3) && e3!.Contains("dot needs"), e3 ?? "");
// `enemy` (2026-11-01): your hostile hard target, so a ground action (Earthly Star) goes where the boss stands; "hostile" too.
Check("target list: enemy, self", RuleTargets.TryParse("enemy, self", symbols, out var enemySelf, out _) && enemySelf is [{ Kind: RuleTarget.Enemy }, { Kind: RuleTarget.Self }]
                                  && RuleTargets.TryParseKind("hostile", out var hostileKind) && hostileKind == RuleTarget.Enemy
                                  && RuleTargets.Names.Contains("enemy"));
errors.Clear();
var chained = RuleSetCompiler.Compile(new RuleSetFile { Job = "SAM", Replaces = ["Hakaze"], Rules = [new RuleFile { Action = "Senei", Target = " tank !shield, self " }] }, "c", symbols, _ => functions, errors)!;
Check("chained target compiles and keeps its source", chained is not null && chained.Rules[0].Target.Length == 2 && chained.Rules[0].TargetSource == "tank !shield, self", string.Join(" | ", errors));

#endregion

Console.WriteLine($"Unit: {pass} passed, {fail} failed");

#region End to end against the built plugin

var buildDir = args[0];
var hooksDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"XIVLauncher\addon\Hooks\dev");
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    foreach (var dir in new[] { buildDir, hooksDir })
    {
        var path = Path.Combine(dir, name.Name + ".dll");
        if (File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
    }
    return null;
};

var wrath = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(buildDir, "WrathCombo.dll"));
var engine = wrath.GetType("WrathCombo.CustomRules.RuleEngine", true)!;
var compileText = engine.GetMethod("CompileText", BindingFlags.NonPublic | BindingFlags.Static)!;

object Load(string text, string path) => compileText.Invoke(null, [text, path])!;
T Prop<T>(object o, string name) => (T)o.GetType().GetProperty(name)!.GetValue(o)!;
List<string> Errors(object loaded) => Prop<IReadOnlyList<string>>(loaded, "Errors").ToList();

var e2eFail = 0;
void E2E(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} {detail}");
    if (!ok) e2eFail++;
}

var samPath = args[1];
var sam = Load(File.ReadAllText(samPath), samPath);
var samErrors = Errors(sam);
E2E("SAM.json compiles", samErrors.Count == 0, string.Join(" | ", samErrors));
var samSet = sam.GetType().GetProperty("Set")!.GetValue(sam);
if (samSet is not null)
{
    var rules = ((IEnumerable)samSet.GetType().GetProperty("Rules")!.GetValue(samSet)!).Cast<object>().ToList();
    var ids = rules.ToDictionary(r => Prop<string>(r, "Name"), r => Prop<uint>(r, "ActionId"));
    E2E("rule count", rules.Count == 23, $"({rules.Count})");   // 23 since the auto-target rule (2026-10-28)
    E2E("Senei id", ids["Senei"] == 16481);
    E2E("role TrueNorth id", ids["True North"] == 7546);
    E2E("Iaijutsu id", ids["Higanbana"] == 7867);
    E2E("starter is Hakaze", ids["Combo starter"] == 7477);
    var replaces = (HashSet<uint>)samSet.GetType().GetProperty("Replaces")!.GetValue(samSet)!;
    E2E("replaces Hakaze+Gyofu", replaces.SetEquals([7477u, 36963u]));
    var defines = ((IEnumerable)samSet.GetType().GetProperty("Defines")!.GetValue(samSet)!).Cast<object>().Count();
    // 8 rotation defines + optBurstTrashHp and holdBurst (2026-10-21 trash burst hold).
    E2E("defines compiled", defines == 11, $"({defines})");   // 11 with optAutoTarget (2026-10-28)
}

var malPath = args[2];
var mal = Load(File.ReadAllText(malPath), malPath);
E2E("SAM_Lv100_SlowMAL.json compiles", Errors(mal).Count == 0, string.Join(" | ", Errors(mal)));
var malSet = mal.GetType().GetProperty("Set")!.GetValue(mal);
if (malSet is not null)
{
    E2E("set-level when kept", Prop<string>(malSet, "WhenSource") == "level >= 100");
    var malRules = ((IEnumerable)malSet.GetType().GetProperty("Rules")!.GetValue(malSet)!).Cast<object>().ToList();
    var wait = malRules.First(r => Prop<string>(r, "Name") == "Wait for the pull");
    E2E("Wait is Wrath's Cease", Prop<uint>(wait, "ActionId") == 1_000_004);
}

var symbolsType = wrath.GetType("WrathCombo.CustomRules.JobRuleSymbols", true)!;
var samSymbols = symbolsType.GetMethod("For")!.Invoke(null, ["SAM"])!;
var gaugeFields = Prop<IReadOnlyList<string>>(samSymbols, "GaugeFields");
E2E("SAM gauge fields", gaugeFields.Contains("Kenki") && gaugeFields.Contains("HasGetsu"), string.Join(", ", gaugeFields));

void ExpectError(string name, string json, string expected)
{
    var errs = Errors(Load(json, $"{name}.json"));
    E2E(name, errs.Any(e => e.Contains(expected, StringComparison.OrdinalIgnoreCase)), $"-> {string.Join(" | ", errs)}");
}

ExpectError("bad json", "{ \"job\": \"SAM\", ", "JSON:");
ExpectError("unknown job", "{ \"job\": \"XYZ\", \"replaces\": [\"Hakaze\"] }", "unknown job 'XYZ'");
ExpectError("class maps to job", "{ \"job\": \"LNC\", \"replaces\": [\"Senei\"] }", "unknown action 'Senei'");
ExpectError("gauge typo", "{ \"job\": \"SAM\", \"replaces\": [\"Hakaze\"], \"rules\": [ { \"action\": \"Shinten\", \"when\": \"gauge(Kenkii) > 50\" } ] }", "not a gauge field");
ExpectError("status typo", "{ \"job\": \"SAM\", \"replaces\": [\"Hakaze\"], \"rules\": [ { \"action\": \"Shinten\", \"when\": \"buff(Tendoo)\" } ] }", "Unknown status 'Tendoo'");
ExpectError("no replaces (no game data for a default)", "{ \"job\": \"SAM\", \"rules\": [] }", "single-target combo starter");

var drg = Load("{ \"job\": \"lnc\", \"replaces\": [\"TrueThrust\"], \"rules\": [ { \"action\": \"LanceCharge\", \"weave\": true } ] }", "drg.json");
E2E("class name normalised (LNC -> DRG)", Errors(drg).Count == 0 && Prop<object>(drg, "Job").ToString() == "DRG", string.Join(" | ", Errors(drg)));

E2E("example embedded", wrath.GetManifestResourceNames().Contains("WrathCombo.CustomRules.Examples.SAM_Simple.json") && wrath.GetManifestResourceNames().Contains("WrathCombo.CustomRules.Examples.SAM_Lv100_SlowMAL.json"), string.Join(", ", wrath.GetManifestResourceNames().Where(n => n.Contains("CustomRules"))));
// ---- The per-job "Custom Rules - Single Target" mode ----
var presetType = wrath.GetType("WrathCombo.Combos.Preset", true)!;
CustomAttributeData? Attr(string preset, string attrName) =>
    presetType.GetField(preset)!.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType.Name == attrName);
int Order(string preset) => (int)Attr(preset, "JobInfoAttribute")!.ConstructorArguments.Last().Value!;
IEnumerable<string> Conflicts(string preset) =>
    ((IEnumerable<CustomAttributeTypedArgument>)Attr(preset, "ConflictingCombosAttribute")!.ConstructorArguments[0].Value!)
    .Select(a => Enum.GetName(presetType, a.Value!)!);

E2E("SAM_ST_CustomRules = 15030", (int)Enum.Parse(presetType, "SAM_ST_CustomRules") == 15030);
E2E("marked [CustomRulesCombo]", Attr("SAM_ST_CustomRules", "CustomRulesCombo") is not null);
var replaceArgs = ((IEnumerable<CustomAttributeTypedArgument>)Attr("SAM_ST_CustomRules", "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
E2E("replaces Hakaze + Gyofu (same as Simple/Advanced)", replaceArgs.SequenceEqual([7477u, 36963u]));
var autoArgs = Attr("SAM_ST_CustomRules", "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).ToList();
E2E("AutoAction(single target, not heal) -> Single Target DPS custom action + Auto-Mode", autoArgs.SequenceEqual([false, false]));
E2E("UI order: Simple AoE < Custom Rules < Advanced ST",
    Order("SAM_AoE_SimpleMode") < Order("SAM_ST_CustomRules") && Order("SAM_ST_CustomRules") < Order("SAM_ST_AdvancedMode"),
    $"({Order("SAM_ST_SimpleMode")}, {Order("SAM_AoE_SimpleMode")}, {Order("SAM_ST_CustomRules")}, {Order("SAM_ST_AdvancedMode")})");
// Custom Rules runs in front of Simple/Advanced (higher preset value = evaluated first) and falls through, so no conflicts.
E2E("no conflicts on Custom Rules", Attr("SAM_ST_CustomRules", "ConflictingCombosAttribute") is null);
E2E("Simple/Advanced don't conflict with Custom Rules", !Conflicts("SAM_ST_SimpleMode").Contains("SAM_ST_CustomRules") && !Conflicts("SAM_ST_AdvancedMode").Contains("SAM_ST_CustomRules"));
E2E("Custom Rules evaluated before Simple/Advanced (higher preset value)", (int)Enum.Parse(presetType, "SAM_ST_CustomRules") > (int)Enum.Parse(presetType, "SAM_ST_AdvancedMode"));
E2E("old global preset removed", !Enum.GetNames(presetType).Contains("Custom_RuleRotation"));
var comboClass = wrath.GetType("WrathCombo.CustomRules.SAM_ST_CustomRules");
E2E("combo class found by ActionReplacer (direct CustomCombo subclass)", comboClass?.BaseType?.Name == "CustomCombo");
E2E("settings field CustomRuleSelections", wrath.GetType("WrathCombo.Core.Configuration")!.GetField("CustomRuleSelections") is not null);
E2E("settings field CustomRuleOptions", wrath.GetType("WrathCombo.Core.Configuration")!.GetField("CustomRuleOptions") is not null);

// ---- Rule set options ("opt..." defines) set from the settings ----
{
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    var planPath = Path.Combine(examples, "DMU_Mit_SAM.json");
    var planText = File.ReadAllText(planPath);
    object Opt(object loaded, string name) => ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().First(o => Prop<string>(o, "Name") == name);
    var plain = Load(planText, planPath);
    var slot = Opt(plain, "optMeleeSlot");
    E2E("option from the file: optMeleeSlot = 1, labelled, 2 choices",
        Prop<string>(slot, "Value") == "1" && Prop<string>(slot, "FileValue") == "1" && Prop<string>(slot, "Label") == "Melee slot"
        && ((IEnumerable)Prop<object>(slot, "Choices")).Cast<object>().Count() == 2);
    var set2 = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optMeleeSlot"] = "2" }])!;
    var slot2 = Opt(set2, "optMeleeSlot");
    E2E("option set in the settings replaces the file's value", Prop<string>(slot2, "Value") == "2" && Prop<string>(slot2, "FileValue") == "1" && Errors(set2).Count == 0);
    // Every bundled option has an entry now, so a file of its own for the fallback.
    const string bareText = "{ \"name\": \"bare\", \"job\": \"SGE\", \"replaces\": [\"Dosis\"],\n  \"define\": {\n    // 1 keeps the tank shield to moments you are moving.\n    \"optTankShieldOnlyMoving\": \"1\"\n  },\n  \"rules\": [ { \"name\": \"x\", \"action\": \"Dosis\", \"when\": \"optTankShieldOnlyMoving\" } ] }";
    var moving = Opt(Load(bareText, "bare.json"), "optTankShieldOnlyMoving");
    E2E("option without an entry: label from its name, help from the comment above",
        Prop<string>(moving, "Label") == "Tank shield only moving" && Prop<string>(moving, "Help").Contains("moving") && Prop<object?>(moving, "Choices") is null,
        $"({Prop<string>(moving, "Label")})");
}
// ---- Statuses the environment applies: buff() only sees statuses you applied ----
{
    // Accretion (1604), Earth Resistance Down II (3372) and Primordial Crust (5454) come from the environment (source E0000000),
    // so buff(id) is false even while you have one; buffAny(id) sees them. With buff(1604) the Sage plan healed the other
    // healer while the Sage's own Accretion ran out (2026-11-02 18:30, a wipe at Black Hole).
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    var envBuff = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z])buff\((1604|3372|5454)\)");
    var hits = Directory.GetFiles(examples, "*.json").Where(f => envBuff.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
    E2E("no rule file tests an environment status (Accretion, Earth Resistance Down II, Primordial Crust) with buff()", hits.Count == 0, string.Join(", ", hits));
    // Sage: Krasis takes a press before the Accretion heal, so only with 3.4 s of the Accretion left, and on you only under 70%.
    var sgePlan = File.ReadAllText(Path.Combine(examples, "DMU_Mit_SGE.json"));
    E2E("DMU_Mit_SGE: Accretion Krasis only with time left for the heal",
        sgePlan.Contains("\"accKrasis\": \"sinceCast(Earthquake) < 18.0 && (!buffAny(1604) || playerHp < 70)\"")
        && System.Text.RegularExpressions.Regex.Matches(sgePlan, "\"action\": \"Krasis\"[^}]*accKrasis").Count == 3
        && System.Text.RegularExpressions.Regex.Matches(sgePlan, "\"name\": \"Earthquake: Krasis").Count == 3);
}

// ---- Astrologian cards: the party member under your mouse in the party list first ----
{
    // Play I uses `card` (Wrath's pick, with its Quick Target Cards override); Play II and III put `mouseover` first, with the
    // card's own status filter, so a hover sends the card there (2026-11-03, the user's request).
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    var bad = new List<string>();
    var plays = 0;
    foreach (var f in Directory.GetFiles(examples, "*.json").Where(f => Path.GetFileName(f) is var n && (n.StartsWith("AST_") || n == "DMU_Mit_AST.json")))
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), "\\{ \"name\": \"([^\"]*)\", \"action\": \"(Play[123])\"[^}]*?\"target\": \"([^\"]*)\""))
        {
            plays++;
            var ok = m.Groups[2].Value == "Play1"
                ? m.Groups[3].Value.StartsWith("card")
                : System.Text.RegularExpressions.Regex.IsMatch(m.Groups[3].Value, "^mouseover [^,]*!(3888|3890|3891|3892)");
            if (!ok) bad.Add($"{Path.GetFileName(f)}: {m.Groups[1].Value}");
        }
    E2E("Astrologian cards: Play I on `card`, Play II and III on the hovered party member first", plays == 30 && bad.Count == 0,
        $"{plays} card rules; {string.Join("; ", bad)}");

    // Outside Dancing Mad the card cycle never stalls (the user, 2026-11-03: cards played by hand in dungeons; a draw waited
    // 64 s behind a Balance held 118 s for a Divination that trash kept holding). Truth table of the two defines, with the
    // builtins they read as plain values; and the rules that use them, in both DPS files.
    foreach (var f in new[] { "AST_ST_DPS.json", "AST_AoE_DPS.json" })
    {
        var t = File.ReadAllText(Path.Combine(examples, f));
        string Def(string n) => System.Text.RegularExpressions.Regex.Match(t, $"\"{n}\": \"([^\"]*)\"").Groups[1].Value;
        string Sub(string s) => s.Replace("cd(AstralDraw)", "cdDraw").Replace("cd(Divination)", "cdDiv");
        double PlayOne(int plan, int card, int divUp, double cdDraw, double cdDiv, int held) => Eval(Sub(Def("playOneNow")), new()
            { ["planZone"] = $"{plan}", ["card1"] = $"{card}", ["divUp"] = $"{divUp}", ["cdDraw"] = $"{cdDraw}", ["cdDiv"] = $"{cdDiv}", ["holdDiv"] = $"{held}" });
        double AtOnce(int opt, int plan, int bossEnc) => Eval(Def("cardsAtOnce"), new() { ["optCardsOnTrash"] = $"{opt}", ["planZone"] = $"{plan}", ["bossEncounter"] = $"{bossEnc}" });
        E2E($"{f}: outside Dancing Mad the Play I card goes before the next draw when Divination is over 10 s off or held; utility cards at once on trash",
            PlayOne(0, 1, 0, 1, 30, 0) == 1 && PlayOne(0, 1, 0, 1, 5, 0) == 0 && PlayOne(0, 1, 0, 1, 0, 1) == 1 && PlayOne(1, 1, 0, 1, 30, 0) == 0
            && PlayOne(0, 1, 1, 1, 100, 0) == 0 && PlayOne(0, 1, 0, 20, 30, 0) == 0 && PlayOne(0, 0, 0, 1, 30, 0) == 0
            && AtOnce(1, 0, 0) == 1 && AtOnce(1, 0, 1) == 0 && AtOnce(1, 1, 0) == 0 && AtOnce(0, 0, 0) == 0
            && t.Contains("\"when\": \"(canWeave || gcdHeld) && inCombat && playOneNow\"") && Def("optCardsOnTrash") == "1"
            && Def("lordNow").Contains("(cd(Divination) > 10 || !planZone && holdDiv)")
            && System.Text.RegularExpressions.Regex.Matches(t, @"\|\| cardsAtOnce\)").Count == 4);
    }
}
var res = (System.Resources.ResourceManager)wrath.GetType("WrathCombo.Resources.Localization.Presets.CustomComboPresets", true)!
    .GetProperty("ResourceManager", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(null)!;
E2E("UI name", res.GetString("SAM_ST_CustomRules_Name", System.Globalization.CultureInfo.InvariantCulture) == "Custom Rules - Single Target");

// ---- Sage: four modes, heal/raidwide functions, targets, and the four example files (args[3..]) ----
string[] sgeModes = ["SGE_ST_CustomRules", "SGE_AoE_CustomRules", "SGE_STHeal_CustomRules", "SGE_AoEHeal_CustomRules"];
(bool aoe, bool heal)[] sgeAuto = [(false, false), (true, false), (false, true), (true, true)];
uint[][] sgeButtons = [[24283u, 24306u, 24312u], [24297u, 24315u], [24284u], [24286u]];
for (var i = 0; i < sgeModes.Length; i++)
{
    var m = sgeModes[i];
    E2E($"{m} = {14090 + i}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == 14090 + i && Attr(m, "CustomRulesCombo") is not null);
    var auto = Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).ToList();
    E2E($"{m} AutoAction({sgeAuto[i].aoe}, {sgeAuto[i].heal})", auto.SequenceEqual([sgeAuto[i].aoe, sgeAuto[i].heal]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's buttons", rep.SequenceEqual(sgeButtons[i]), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("SGE Custom Rules modes have no conflicts", sgeModes.All(m => Attr(m, "ConflictingCombosAttribute") is null));
E2E("SGE Simple/Advanced don't conflict with Custom Rules", !Conflicts("SGE_ST_Simple_DPS").Contains("SGE_ST_CustomRules") && !Conflicts("SGE_ST_Advanced_DPS").Contains("SGE_ST_CustomRules") && !Conflicts("SGE_AoE_Advanced_Heal").Contains("SGE_AoEHeal_CustomRules") && !Conflicts("SGE_Retarget_Diagnosis").Contains("SGE_STHeal_CustomRules"));
E2E("SGE Custom Rules evaluated before Simple/Advanced", (int)Enum.Parse(presetType, "SGE_ST_CustomRules") > (int)Enum.Parse(presetType, "SGE_ST_Simple_DPS") && (int)Enum.Parse(presetType, "SGE_AoEHeal_CustomRules") > (int)Enum.Parse(presetType, "SGE_AoE_Advanced_Heal"));
E2E("UI order: SGE Custom Rules between Simple and Advanced", Order("SGE_AoE_Simple_Heal") < Order("SGE_ST_CustomRules") && Order("SGE_AoEHeal_CustomRules") < Order("SGE_ST_Advanced_DPS"));

var sgeSymbols = symbolsType.GetMethod("For")!.Invoke(null, ["SGE"])!;
var sgeGauge = Prop<IReadOnlyList<string>>(sgeSymbols, "GaugeFields");
E2E("SGE gauge fields", sgeGauge.Contains("Addersgall") && sgeGauge.Contains("Addersting") && sgeGauge.Contains("AddersgallTimer") && sgeGauge.Contains("Eukrasia"), string.Join(", ", sgeGauge));

var builtins = wrath.GetType("WrathCombo.CustomRules.RuleBuiltins", true)!.GetMethod("Create")!.Invoke(null, [24283u])!;
var fnNames = ((IEnumerable)builtins.GetType().GetProperty("Keys")!.GetValue(builtins)!).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
E2E("heal/party/raidwide functions registered", new[] { "partyAvgHp", "partyLowestHp", "partyBelow", "partyWith", "healHp", "healIsTank", "healHas", "tankHp", "tankHas", "ttIsParty", "ttIsTank", "ttIsSelf", "ttHp", "ttHas", "raidwide", "raidwideIn", "stackMarker", "tankbuster", "mp", "canRaise", "canEsuna" }.All(fnNames.Contains));

// ---- Red Mage: two modes ----
foreach (var (m, id, aoe, buttons) in new[] { ("RDM_ST_CustomRules", 13030, false, new uint[] { 7503, 7524, 37004 }), ("RDM_AoE_CustomRules", 13230, true, new uint[] { 7509, 16526 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's buttons", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("RDM Custom Rules modes have no conflicts and run first", Attr("RDM_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("RDM_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("RDM_ST_SimpleMode").Contains("RDM_ST_CustomRules") && !Conflicts("RDM_ST_DPS").Contains("RDM_ST_CustomRules") && !Conflicts("RDM_AoE_DPS").Contains("RDM_AoE_CustomRules")
                                && (int)Enum.Parse(presetType, "RDM_ST_CustomRules") > (int)Enum.Parse(presetType, "RDM_ST_DPS") && (int)Enum.Parse(presetType, "RDM_AoE_CustomRules") > (int)Enum.Parse(presetType, "RDM_AoE_DPS"));
E2E("UI order: RDM Custom Rules between Simple and Advanced", Order("RDM_AoE_SimpleMode") < Order("RDM_ST_CustomRules") && Order("RDM_AoE_CustomRules") < Order("RDM_ST_DPS"));
var rdmGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["RDM"])!, "GaugeFields");
E2E("RDM gauge fields", rdmGauge.Contains("WhiteMana") && rdmGauge.Contains("BlackMana") && rdmGauge.Contains("ManaStacks"), string.Join(", ", rdmGauge));
E2E("RDM examples embedded", new[] { "RDM_ST", "RDM_AoE" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    int Index(List<object> rs, string name) => rs.FindIndex(r => Prop<string>(r, "Name") == name);
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    Dictionary<string, string> Defines(string file) => ((IEnumerable)Prop<object>(Rules(file), "Defines")).Cast<object>()
        .ToDictionary(d => (string)d.GetType().GetField("Item1")!.GetValue(d)!, d => (string)d.GetType().GetField("Item2")!.GetValue(d)!);

    // The finisher follows the mana (2026-10-31, user): Verflare whenever white is higher, Verholy whenever black is,
    // whatever procs are up; at equal mana Verflare only to fish for a missing Verfire.
    var st = RuleList(Rules("RDM_ST.json"));
    var flare = Prop<string>(st[Index(st, "Verflare")], "WhenSource");
    bool Flare(int w, int b, int stone, int fire) => Eval(flare, new() { ["stacks"] = "3", ["white"] = $"{w}", ["black"] = $"{b}", ["stone"] = $"{stone}", ["fire"] = $"{fire}" }) == 1;
    var procs = new[] { (0, 0), (1, 0), (0, 1), (1, 1) };
    E2E("RDM_ST: Verflare whenever white is higher, never when black is, whatever procs are up",
        procs.All(p => Flare(47, 36, p.Item1, p.Item2) && Flare(20, 19, p.Item1, p.Item2) && !Flare(36, 47, p.Item1, p.Item2) && !Flare(19, 20, p.Item1, p.Item2)));
    E2E("RDM_ST: at equal mana Verflare only with Verstone Ready and no Verfire Ready", Flare(30, 30, 1, 0) && !Flare(30, 30, 0, 0) && !Flare(30, 30, 0, 1) && !Flare(30, 30, 1, 1));
    E2E("RDM_ST: Verholy right after Verflare at three stacks", Index(st, "Verholy") == Index(st, "Verflare") + 1
        && Prop<string>(st[Index(st, "Verholy")], "WhenSource") == "stacks == 3");
    var aoe = RuleList(Rules("RDM_AoE.json"));
    E2E("RDM_AoE: Verflare only when white is higher", Prop<string>(aoe[Index(aoe, "Verflare")], "WhenSource") == "stacks == 3 && !blackHigher"
        && Defines("RDM_AoE.json")["blackHigher"] == "black >= white");

    // DMU_Mit_RDM (2026-10-31, user): the first Addle lands before the first Double-trouble Trap (confetti) hit
    // (Mystery Magic #1 + 17.2) and lasts through the first Light of Judgment hit (+30.4): pressed from +15.0, right
    // before a cast too, and a hard cast that would start in the second before that window waits for it.
    var plan = RuleList(Rules("DMU_Mit_RDM.json"));
    var pd = Defines("DMU_Mit_RDM.json");
    E2E("DMU_Mit_RDM: confetti Addle window from Mystery Magic + 15.0, the hold from + 14.0",
        pd["dttMain"] == "castsSeen(MysteryMagic) == 1 && sinceCast(MysteryMagic) >= 15.0"
        && pd["dttSoon"] == "castsSeen(MysteryMagic) == 1 && sinceCast(MysteryMagic) >= 14.0 && sinceCast(MysteryMagic) < 15.0"
        && pd["dtt1"].StartsWith("dttMain || ") && pd["addleWhen"].StartsWith("dtt1 || "), $"{pd.GetValueOrDefault("dttMain")} | {pd.GetValueOrDefault("dttSoon")}");
    E2E("DMU_Mit_RDM: the confetti Addle may go right before a cast (shortAddle)", pd["shortAddle"].Split(" || ").Contains("dttMain"));
    var hold = Index(plan, "Hold the cast for Addle (plan)");
    E2E("DMU_Mit_RDM: a hard cast waits for the confetti Addle, right after the Addle rule", hold > 0 && hold == Index(plan, "Addle (plan)") + 1
        && Prop<uint>(plan[hold], "ActionId") == 1000004 && Prop<string>(plan[hold], "WhenSource").StartsWith("dttSoon && hardNext && "));
}

// ---- Reaper: two modes ----
foreach (var (m, id, aoe, buttons) in new[] { ("RPR_ST_CustomRules", 12030, false, new uint[] { 24373 }), ("RPR_AoE_CustomRules", 12230, true, new uint[] { 24376 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's button", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("RPR Custom Rules modes have no conflicts and run first", Attr("RPR_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("RPR_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("RPR_ST_SimpleMode").Contains("RPR_ST_CustomRules") && !Conflicts("RPR_ST_AdvancedMode").Contains("RPR_ST_CustomRules") && !Conflicts("RPR_AoE_AdvancedMode").Contains("RPR_AoE_CustomRules")
                                && (int)Enum.Parse(presetType, "RPR_ST_CustomRules") > (int)Enum.Parse(presetType, "RPR_ST_AdvancedMode") && (int)Enum.Parse(presetType, "RPR_AoE_CustomRules") > (int)Enum.Parse(presetType, "RPR_AoE_AdvancedMode"));
E2E("UI order: RPR Custom Rules between Simple and Advanced", Order("RPR_AoE_SimpleMode") < Order("RPR_ST_CustomRules") && Order("RPR_AoE_CustomRules") < Order("RPR_ST_AdvancedMode"));
var rprGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["RPR"])!, "GaugeFields");
E2E("RPR gauge fields", new[] { "Soul", "Shroud", "LemureShroud", "VoidShroud", "EnshroudedTimeRemaining" }.All(rprGauge.Contains), string.Join(", ", rprGauge));
E2E("RPR examples embedded", new[] { "RPR_ST", "RPR_AoE", "DMU_Mit_RPR" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    // DMU_Mit_RPR.json (2026-10-23): the Samurai plan's Feints unchanged, plus Arcane Crest windows; the job files'
    // raidwide Crest/Feint stand down in zone 1363.
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    object R(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    var planPath = Path.Combine(examples, "DMU_Mit_RPR.json");
    var planText = File.ReadAllText(planPath);
    var plan = Load(planText, planPath);
    var planSet = Prop<object>(plan, "Set");
    var planRules = RuleList(planSet);
    E2E("DMU_Mit_RPR: layer, zone 1363, replaces Slice + Spinning Scythe", Prop<bool>(planSet, "Layer") && Prop<string>(planSet, "WhenSource").Contains("1363")
        && ((HashSet<uint>)Prop<object>(planSet, "Replaces")).SetEquals([24373u, 24376u]));
    E2E("DMU_Mit_RPR: woven Feint (7549) and Arcane Crest (24404)", planRules.Count == 2 && Prop<uint>(R(planRules, "Feint (plan)"), "ActionId") == 7549
        && Prop<uint>(R(planRules, "Arcane Crest (plan)"), "ActionId") == 24404 && planRules.All(r => Prop<bool>(r, "Weave")));
    IEnumerable<string> FeintLines(string file) => File.ReadAllLines(Path.Combine(examples, file)).Select(l => l.Trim())
        .Where(l => l.StartsWith("\"d1") || l.StartsWith("\"d2") || l.StartsWith("\"feintWhen") || l.StartsWith("{ \"name\": \"Feint (plan)\""));
    E2E("DMU_Mit_RPR: Feint windows and rule are DMU_Mit_SAM's", FeintLines("DMU_Mit_RPR.json").Count() == 22 && FeintLines("DMU_Mit_RPR.json").SequenceEqual(FeintLines("DMU_Mit_SAM.json")));
    object Opt(object loaded, string name) => ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().First(o => Prop<string>(o, "Name") == name);
    var slot = Opt(plan, "optMeleeSlot");
    E2E("DMU_Mit_RPR options: optMeleeSlot = 1 with 2 choices, optCrest = 1", Prop<string>(slot, "Value") == "1" && Prop<string>(slot, "Label") == "Melee slot"
        && ((IEnumerable)Prop<object>(slot, "Choices")).Cast<object>().Count() == 2 && Prop<string>(Opt(plan, "optCrest"), "Value") == "1" && Prop<string>(Opt(plan, "optCrest"), "Label") == "Arcane Crest");
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var m2 = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optMeleeSlot"] = "2", ["optCrest"] = "0" }])!;
    E2E("DMU_Mit_RPR compiles with optMeleeSlot = 2, optCrest = 0", Errors(m2).Count == 0 && Prop<string>(Opt(m2, "optMeleeSlot"), "Value") == "2", string.Join(" | ", Errors(m2)));
    var st = RuleList(Rules("RPR_ST.json"));
    var aoe = RuleList(Rules("RPR_AoE.json"));
    E2E("RPR_ST / RPR_AoE: raidwide Arcane Crest and Feint stand down in zone 1363",
        Prop<string>(R(st, "Arcane Crest into a raidwide"), "WhenSource").Contains("!dmu") && Prop<string>(R(st, "Feint into a raidwide"), "WhenSource").Contains("!dmu")
        && Prop<string>(R(aoe, "Arcane Crest into a raidwide"), "WhenSource").Contains("zone != 1363"));
}

// ---- Gunbreaker: two modes ----
foreach (var (m, id, aoe, buttons) in new[] { ("GNB_ST_CustomRules", 7030, false, new uint[] { 16137 }), ("GNB_AoE_CustomRules", 7230, true, new uint[] { 16141 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's button", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("GNB Custom Rules modes have no conflicts and run first", Attr("GNB_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("GNB_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("GNB_ST_Simple").Contains("GNB_ST_CustomRules") && !Conflicts("GNB_ST_Advanced").Contains("GNB_ST_CustomRules") && !Conflicts("GNB_AoE_Advanced").Contains("GNB_AoE_CustomRules")
                                && (int)Enum.Parse(presetType, "GNB_ST_CustomRules") > (int)Enum.Parse(presetType, "GNB_ST_Advanced") && (int)Enum.Parse(presetType, "GNB_AoE_CustomRules") > (int)Enum.Parse(presetType, "GNB_AoE_Advanced"));
E2E("UI order: GNB Custom Rules between Simple and Advanced", Order("GNB_AoE_Simple") < Order("GNB_ST_CustomRules") && Order("GNB_AoE_CustomRules") < Order("GNB_ST_Advanced"));
var gnbGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["GNB"])!, "GaugeFields");
E2E("GNB gauge fields", new[] { "Ammo", "AmmoComboStep", "MaxTimerDuration" }.All(gnbGauge.Contains), string.Join(", ", gnbGauge));
E2E("GNB examples embedded", new[] { "GNB_ST", "GNB_AoE", "DMU_Mit_GNB" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    // DMU_Mit_GNB.json (2026-10-23): the sheet's Reprisal and Heart of Light (its Party Mit and GNB/DRK rows), Great Nebula,
    // Rampart, Camouflage and Heart of Corundum on the busters, Heart of Corundum on the other tank. Upgraded actions go
    // through "hook" as Wrath does (OriginalHook(Nebula), OriginalHook(HeartOfStone)): an alias id fails after the upgrade.
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    object R(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    bool Hooked(List<object> rs, string name, uint baseId) => Prop<bool>(R(rs, name), "Hook") && Prop<uint>(R(rs, name), "ActionId") == baseId;
    string Src(object r) => Prop<string>(r, "WhenSource");
    uint Id(object r) => Prop<uint>(r, "ActionId");
    var planPath = Path.Combine(examples, "DMU_Mit_GNB.json");
    var planText = File.ReadAllText(planPath);
    var plan = Load(planText, planPath);
    E2E("DMU_Mit_GNB compiles", Errors(plan).Count == 0, string.Join(" | ", Errors(plan)));
    var planSet = Prop<object>(plan, "Set");
    var planRules = RuleList(planSet);
    E2E("DMU_Mit_GNB: layer, zone 1363, replaces Keen Edge + Demon Slice", Prop<bool>(planSet, "Layer") && Prop<string>(planSet, "WhenSource").Contains("1363")
        && ((HashSet<uint>)Prop<object>(planSet, "Replaces")).SetEquals([16137u, 16141u]));
    E2E("DMU_Mit_GNB: the sheet's Heart of Light (16160) and Reprisal (7535)", Id(R(planRules, "Heart of Light (plan)")) == 16160 && Id(R(planRules, "Reprisal (plan)")) == 7535
        && Id(R(planRules, "Heart of Light (Phase 5, before Kefka is targetable)")) == 16160);
    E2E("DMU_Mit_GNB: Great Nebula through Nebula's hook, Heart of Corundum through Heart of Stone's, on the other tank with target tank",
        Hooked(planRules, "Great Nebula (buster)", 16148) && Hooked(planRules, "Great Nebula (buster, one)", 16148) && Hooked(planRules, "Heart of Corundum (buster)", 16161)
        && Hooked(planRules, "Heart of Corundum (other tank's buster)", 16161)
        && R(planRules, "Heart of Corundum (other tank's buster)").GetType().GetProperty("TargetSource")!.GetValue(R(planRules, "Heart of Corundum (other tank's buster)"))?.ToString() == "tank");
    E2E("DMU_Mit_GNB: every Nebula / Heart of Stone rule hooks; no alias ids (Great Nebula 36935, Heart of Corundum 25758)",
        planRules.Where(r => Id(r) is 16148 or 16161).All(r => Prop<bool>(r, "Hook")) && !planRules.Any(r => Id(r) is 36935 or 25758));
    E2E("DMU_Mit_GNB: Rampart (7531) and Camouflage (16140) on the busters", planRules.Count(r => Id(r) == 7531) == 2 && planRules.Count(r => Id(r) == 16140) == 2);
    var holLast = "Heart of Light (plan, last chance)";
    E2E("DMU_Mit_GNB: all woven except the OT's Phase 5 Heart of Light (no target, downtime) and its last-chance Heart of Light",
        planRules.Where(r => Prop<string>(r, "Name") is not ("Heart of Light (Phase 5, before Kefka is targetable)" or "Heart of Light (plan, last chance)")).All(r => Prop<bool>(r, "Weave"))
        && Src(R(planRules, "Heart of Light (Phase 5, before Kefka is targetable)")).Contains("!hasTarget") && Src(R(planRules, "Heart of Light (Phase 5, before Kefka is targetable)")).Contains("ot") && Src(R(planRules, "Heart of Light (Phase 5, before Kefka is targetable)")).Contains("optP5EarlyHol")
        && Src(R(planRules, holLast)) == "optSheetMit && ot && otHolLate" && Id(R(planRules, holLast)) == 16160);
    // The user (2026-11-01): as OT, Heart of Light follows the Dancer's Shield Samba (same 15 s, same 90 s cooldown):
    // DMU_Mit_DNC's ten windows, and its last-chance press outside a weave window (s2 at once: no dance to wait for).
    Dictionary<string, string> Defines(string file) => ((IEnumerable)Prop<object>(Rules(file), "Defines")).Cast<object>()
        .ToDictionary(d => (string)d.GetType().GetField("Item1")!.GetValue(d)!, d => (string)d.GetType().GetField("Item2")!.GetValue(d)!);
    var gd = Defines("DMU_Mit_GNB.json");
    var dd = Defines("DMU_Mit_DNC.json");
    var s8 = dd["s8"].Split(" || ", 2);
    E2E("DMU_Mit_GNB: the OT's Heart of Light windows are DMU_Mit_DNC's Shield Samba windows s1-s10",
        Enumerable.Range(1, 10).Where(k => k != 8).All(k => gd[$"otHol{k}"] == dd[$"s{k}"])
        && gd["otP5OpenHol"] == s8[0] && gd["otHol8"] == "otP5OpenHol || " + s8[1]
        && gd["otHol"] == string.Join(" || ", Enumerable.Range(1, 10).Select(k => $"otHol{k}")) && dd["sambaWhen"] == string.Join(" || ", Enumerable.Range(1, 10).Select(k => $"s{k}")),
        string.Join(", ", Enumerable.Range(1, 10).Where(k => k != 8 && gd.GetValueOrDefault($"otHol{k}") != dd[$"s{k}"]).Select(k => $"otHol{k}")));
    // The user (2026-11-01): No Mercy is held with Bloodfest at the end of Phase 1 under optP1BurstHoldHp, and (held, or
    // back after Phase 1's forced end at 198 s) until Phase 2's first 2-minute window, Forsaken + 13 s; meanwhile the
    // rotation counts down to that release. Same defines in both files.
    var holdNames = new[] { "p2Window", "p2Wait", "nmHold", "nmRelease", "nmCd" };
    var gst = Defines("GNB_ST.json");
    var gaoe = Defines("GNB_AoE.json");
    E2E("GNB_ST / GNB_AoE: the same No Mercy hold defines", holdNames.All(n => gst[n] == gaoe[n]), string.Join(", ", holdNames.Where(n => gst.GetValueOrDefault(n) != gaoe.GetValueOrDefault(n))));
    foreach (var f in new[] { "GNB_ST.json", "GNB_AoE.json" })
    {
        var rs = RuleList(Rules(f));
        E2E($"{f}: No Mercy waits while nmHold (p1Ending || p2Wait); Bloodfest keys on the held countdown", Src(R(rs, "No Mercy")).Contains("&& !nmHold &&")
            && Defines(f)["nmHold"] == "p1Ending || p2Wait" && Src(R(rs, "Bloodfest")).Contains("nmCd <= gcd + 1.0") && Defines(f)["p2Wait"].Contains("zone == 1363"));
    }
    // A truth table of the held countdown: the builtins it reads become plain values.
    string Sub(string s) => s.Replace("cd(NoMercy)", "cdNm").Replace("castsSeen(Forsaken)", "fs").Replace("sinceCast(Forsaken)", "fsT")
        .Replace("combatTime", "ct").Replace("zone == 1363", "dmu");
    // nmHpHold (optNoMercyHoldHp, 2026-11-03) is off here; its own check is with the other target-HP holds.
    var holdEnv = new Dictionary<string, string> { ["optP1BurstHoldHp"] = "20", ["dmu"] = "1", ["nmHpHold"] = "0" };
    foreach (var n in holdNames) holdEnv[n] = Sub(gst[n]);
    double NmIn(double ct, double cdNm, int p1, int fs, double fsT) => Eval("nmCd", new(holdEnv) { ["ct"] = $"{ct}", ["cdNm"] = $"{cdNm}", ["p1Ending"] = $"{p1}", ["fs"] = $"{fs}", ["fsT"] = $"{fsT}" });
    double Held(double ct, double cdNm, int p1, int fs, double fsT) => Eval("nmHold", new(holdEnv) { ["ct"] = $"{ct}", ["cdNm"] = $"{cdNm}", ["p1Ending"] = $"{p1}", ["fs"] = $"{fs}", ["fsT"] = $"{fsT}" });
    E2E("GNB_ST: No Mercy held at 3:09 under the set % and at Phase 2's start, released at Forsaken + 13 s; not held at 3:07 over it",
        Held(189, 0, 1, 0, 0) == 1 && Math.Abs(NmIn(189, 0, 1, 0, 0) - 52.8) < 0.01 && Held(208, 0, 0, 0, 0) == 1 && Math.Abs(NmIn(208, 0, 0, 0, 0) - 33.8) < 0.01
        && Math.Abs(NmIn(235, 0, 0, 1, 9.5) - 3.5) < 0.01 && Held(242, 0, 0, 1, 13.1) == 0 && NmIn(242, 0, 0, 1, 13.1) == 0
        && Held(187, 0, 0, 0, 0) == 0 && NmIn(187, 0, 0, 0, 0) == 0 && Held(150, 37, 0, 0, 0) == 0 && NmIn(150, 37, 0, 0, 0) == 37
        && Held(400, 20, 0, 3, 5) == 0,
        $"{NmIn(189, 0, 1, 0, 0)} {NmIn(208, 0, 0, 0, 0)} {NmIn(235, 0, 0, 1, 9.5)} {NmIn(242, 0, 0, 1, 13.1)}");
    E2E("GNB_ST: a No Mercy held under optNoMercyHoldHp reads as far off (999)", NmIn(150, 0, 0, 0, 0) == 0
        && Eval("nmCd", new(holdEnv) { ["ct"] = "150", ["cdNm"] = "0", ["p1Ending"] = "0", ["fs"] = "0", ["fsT"] = "0", ["nmHpHold"] = "1" }) == 999);

    // ---- Per-cooldown "Hold ... under target HP %" options (the user, 2026-11-03): off (0) by default, any fight ----
    // Each: the option (labelled, default 0), its hold define, the rules that must wait on it, and defines that must
    // read it (what waited on the cooldown carries on).
    (string File, string Opt, string Hold, (string Rule, string Has)[] Rules, (string Define, string Has)[] Defs)[] hpHolds =
    [
        ("PLD_ST.json", "optFoFHoldHp", "fofHpHold", [("Fight or Flight", "!fofHpHold"), ("Imperator", "cd(FightOrFlight) > 50")], [("fofCd", "if(fofHpHold || holdBurst, 999, cd(FightOrFlight))")]),
        ("PLD_AoE.json", "optFoFHoldHp", "fofHpHold", [("Fight or Flight", "!fofHpHold"), ("Imperator", "cd(FightOrFlight) > 50")], [("fofCd", "if(fofHpHold || holdBurst, 999, cd(FightOrFlight))")]),
        ("GNB_ST.json", "optNoMercyHoldHp", "nmHpHold", [("No Mercy", "!nmHpHold"), ("Bloodfest", "!nmHpHold")], [("nmCd", "if(nmHpHold, 999,")]),
        ("GNB_AoE.json", "optNoMercyHoldHp", "nmHpHold", [("No Mercy", "!nmHpHold"), ("Bloodfest", "!nmHpHold")], [("nmCd", "if(nmHpHold, 999,")]),
        ("RDM_ST.json", "optEmboldenHoldHp", "embHpHold", [("Embolden", "!embHpHold"), ("Manafication (between combos)", "cdE <= 5 && !embHpHold")], [("startCombo", "holdBurst || embHpHold")]),
        ("RDM_ST.json", "optManaficationHoldHp", "manaHpHold", [("Manafication (between combos)", "!manaHpHold")], [("startCombo", "ready(Manafication) && !manaHpHold")]),
        ("RDM_AoE.json", "optEmboldenHoldHp", "embHpHold", [("Embolden", "!embHpHold"), ("Manafication (between combos)", "cdE <= 5 && !embHpHold")], []),
        ("RDM_AoE.json", "optManaficationHoldHp", "manaHpHold", [("Manafication (between combos)", "!manaHpHold")], []),
        ("DNC_ST.json", "optTechHoldHp", "techHpHold", [("Technical Step", "!holdTech")], [("holdTech", "techHpHold")]),
        ("DNC_ST.json", "optDevilmentHoldHp", "devHpHold", [("Devilment", "!devHpHold"), ("Hold the weave for Devilment", "!devHpHold")], []),
        ("DNC_ST.json", "optFlourishHoldHp", "flHpHold", [("Flourish", "!flHpHold")], []),
        ("DNC_AoE.json", "optTechHoldHp", "techHpHold", [("Technical Step", "!holdTech")], [("holdTech", "techHpHold")]),
        ("DNC_AoE.json", "optDevilmentHoldHp", "devHpHold", [("Devilment", "!devHpHold"), ("Hold the weave for Devilment", "!devHpHold")], []),
        ("DNC_AoE.json", "optFlourishHoldHp", "flHpHold", [("Flourish", "!flHpHold")], []),
        ("WHM_ST_DPS.json", "optPomHoldHp", "pomHpHold", [("Presence of Mind", "!pomHpHold")], []),
        ("RPR_ST.json", "optArcaneCircleHoldHp", "acHpHold", [("Arcane Circle", "!acHpHold")], [("acCd", "if(acHpHold, 999, cd(ArcaneCircle))"), ("shroudWanted", "acHpHold")]),
        ("RPR_ST.json", "optEnshroudHoldHp", "enHpHold", [("Enshroud", "(host || shroudWanted && !p1Ending && !enHpHold")], []),
        ("RPR_AoE.json", "optArcaneCircleHoldHp", "acHpHold", [("Arcane Circle", "!acHpHold")], [("acCd", "if(acHpHold, 999, cd(ArcaneCircle))"), ("shroudWanted", "acHpHold")]),
        ("RPR_AoE.json", "optEnshroudHoldHp", "enHpHold", [("Enshroud", "(host || shroudWanted && !p1Ending && !enHpHold")], []),
        ("WAR_ST.json", "optInnerReleaseHoldHp", "irHpHold", [("Inner Release", "!irHpHold")], [("irSoon", "!irHpHold")]),
        ("WAR_AoE.json", "optInnerReleaseHoldHp", "irHpHold", [("Inner Release", "!irHpHold")], []),
        ("AST_ST_DPS.json", "optDivinationHoldHp", "divHpHold", [("Lightspeed (burst)", "!holdDiv")], [("holdDiv", "holdBurst || divHpHold"), ("divNow", "!holdDiv"), ("lsSpendable", "|| holdDiv)")]),
        ("AST_AoE_DPS.json", "optDivinationHoldHp", "divHpHold", [("Lightspeed (burst)", "!holdDiv")], [("holdDiv", "holdBurst || divHpHold"), ("divNow", "!holdDiv"), ("lsSpendable", "|| holdDiv)")]),
    ];
    foreach (var h in hpHolds)
    {
        var d = Defines(h.File);
        var rs = RuleList(Rules(h.File));
        var text = File.ReadAllText(Path.Combine(examples, h.File));
        var holdSrc = d.GetValueOrDefault(h.Hold) ?? "";
        // Gunbreaker's (No Mercy and Bloodfest) holds on trash only, as holdBurst (the user, 2026-11-03); the rest in any fight.
        var trash = h.File.StartsWith("GNB_");
        var want = trash ? $"{h.Opt} > 0 && hasTarget && !boss && !bossEncounter && targetHp < {h.Opt}" : $"{h.Opt} > 0 && hasTarget && targetHp < {h.Opt}";
        double HoldAt(int opt, int hasT, double hp, int boss = 0, int bossEnc = 0) =>
            Eval(holdSrc.Replace("hasTarget", "ht").Replace("targetHp", "thp").Replace("bossEncounter", "be").Replace("!boss ", "!bs "),
                new() { [h.Opt] = $"{opt}", ["ht"] = $"{hasT}", ["thp"] = $"{hp}", ["bs"] = $"{boss}", ["be"] = $"{bossEnc}" });
        var bossesOk = trash ? HoldAt(20, 1, 15, boss: 1) == 0 && HoldAt(20, 1, 15, bossEnc: 1) == 0 : HoldAt(20, 1, 15, boss: 1, bossEnc: 1) == 1;
        var missing = h.Rules.Where(x => rs.All(r => Prop<string>(r, "Name") != x.Rule) || !Src(R(rs, x.Rule)).Contains(x.Has)).Select(x => x.Rule)
            .Concat(h.Defs.Where(x => !d.GetValueOrDefault(x.Define, "").Contains(x.Has)).Select(x => x.Define)).ToList();
        E2E($"{h.File}: {h.Opt} (default 0, labelled) holds via {h.Hold} under the target's HP % with a target{(trash ? ", on trash only" : ", bosses included")}; off at 0",
            d.GetValueOrDefault(h.Opt) == "0" && text.Contains($"\"{h.Opt}\": {{ \"label\": \"Hold ") && holdSrc == want && bossesOk
            && HoldAt(20, 1, 15) == 1 && HoldAt(20, 1, 25) == 0 && HoldAt(20, 0, 0) == 0 && HoldAt(0, 1, 5) == 0 && missing.Count == 0,
            string.Join(", ", missing));
    }

    // A value option at 0 or 1 showed as a checkbox, with no way to type a % (the user, 2026-11-03): every option whose
    // label reads as a value (an HP %, seconds, a minute, a count) and whose file value is 0 or 1 says "number": true.
    var valueLabel = new System.Text.RegularExpressions.Regex(@"%|\(s\)|at minute|needed|kept for movement");
    var unmarked = new List<string>();
    var marked = 0;
    foreach (var f in Directory.GetFiles(examples, "*.json"))
    {
        var loaded = Load(File.ReadAllText(f), f);
        foreach (var o in ((IEnumerable?)loaded.GetType().GetProperty("Options")!.GetValue(loaded))?.Cast<object>() ?? [])
        {
            var number = Prop<bool>(o, "Number");
            if (number) marked++;
            if (Prop<string>(o, "FileValue") is "0" or "1" && valueLabel.IsMatch(Prop<string>(o, "Label")) && !number)
                unmarked.Add($"{Path.GetFileName(f)}: {Prop<string>(o, "Name")}");
        }
    }
    E2E("value options at 0 or 1 are \"number\" (a text field in the settings, not a checkbox)", unmarked.Count == 0 && marked == 45,
        $"{marked} marked; {string.Join(", ", unmarked)}");

    // The trash burst hold in every job's DPS files (the user, 2026-11-03: "Apply the hold 2min burst for trash to all
    // rulesets"; Paladin and Warrior got it then, for Fight or Flight and Inner Release). Sage has no burst cooldown; the
    // heal files and the Dancing Mad plans press none.
    var burstFiles = Directory.GetFiles(examples, "*.json").Select(Path.GetFileName).Where(n => n is not null
        && System.Text.RegularExpressions.Regex.IsMatch(n, @"^(AST_(ST|AoE)_DPS|DNC_(ST|AoE)|GNB_(ST|AoE)|PLD_(ST|AoE)|RDM_(ST|AoE)|RPR_(ST|AoE)|SAM_\w+|WAR_(ST|AoE)|WHM_ST_DPS)\.json$")).Cast<string>().ToList();
    var noTrash = burstFiles.Where(n =>
    {
        var d = Defines(n);
        var text = File.ReadAllText(Path.Combine(examples, n));
        return !(d.GetValueOrDefault("optBurstTrashHp") is { } v && double.TryParse(v, out var pct) && pct > 0 && text.Contains("\"optBurstTrashHp\": { \"label\": \"Hold ")
            && d.GetValueOrDefault("holdBurst", "").Contains("!boss && !bossEncounter") && d.GetValueOrDefault("holdBurst", "").Contains("< optBurstTrashHp"));
    }).ToList();
    var pldWar = new[] { ("PLD_ST.json", "Fight or Flight"), ("PLD_AoE.json", "Fight or Flight"), ("WAR_ST.json", "Inner Release"), ("WAR_AoE.json", "Inner Release") };
    E2E("every job's DPS files hold the burst on trash (optBurstTrashHp, labelled, on by default); Paladin's and Warrior's burst rules wait on it",
        burstFiles.Count == 17 && noTrash.Count == 0 && pldWar.All(p => Src(R(RuleList(Rules(p.Item1)), p.Item2)).Contains("!holdBurst"))
        && Defines("PLD_ST.json")["fofCd"].Contains("holdBurst") && Defines("WAR_ST.json")["irSoon"].Contains("!holdBurst"),
        $"{burstFiles.Count} files; missing: {string.Join(", ", noTrash)}");
    E2E("DMU_Mit_GNB: the OT's last-chance Heart of Light is the Dancer's sambaLate (s2 whole), before the woven plan rule",
        gd["otHolLate"].Replace("otHol", "s") == dd["sambaLate"].Replace("s2 && cd(TechnicalStep) < 1", "s2")
        && planRules.IndexOf(R(planRules, holLast)) == planRules.IndexOf(R(planRules, "Heart of Light (plan)")) - 1
        && gd["holWhen"] == "optSheetMit && if(ot, otHol, mtHol)", gd.GetValueOrDefault("otHolLate") ?? "");
    E2E("DMU_Mit_GNB: no healing in the Accretions (Great Nebula, Heart of Corundum, Aurora)", planRules.Where(r => Id(r) is 16148 or 16161 or 16151).All(r => Src(r).Contains("!noHeal"))
        && planText.Contains("partyWith(1604)"));
    E2E("DMU_Mit_GNB: buster mitigation waits while Superbolide is up", planRules.Where(r => Id(r) is 16148 or 7531 or 16140 || Prop<string>(r, "Name") == "Heart of Corundum (buster)").All(r => Src(r).Contains("noBolide"))
        && planText.Contains("\"noBolide\": \"!buff(Superbolide)\""));
    var manual = new uint[] { 7533, 7537 }; // Provoke, Shirk
    E2E("GNB files never press Provoke or Shirk",
        new[] { "GNB_ST.json", "GNB_AoE.json", "DMU_Mit_GNB.json" }.All(f => RuleList(Rules(f)).All(r => !manual.Contains(Id(r)))));
    // The user's request (2026-10-23): Aurora last in the big-buster stack; Superbolide optional, off by default.
    E2E("GNB_ST / GNB_AoE never press Superbolide or Aurora", new[] { "GNB_ST.json", "GNB_AoE.json" }.All(f => RuleList(Rules(f)).All(r => Id(r) is not (16152 or 16151))));
    var aurora = planRules.Single(r => Id(r) == 16151);
    E2E("DMU_Mit_GNB: Aurora last in the stack (after Heart of Corundum and the sheet's presses, once the stack is in), on you",
        planRules.IndexOf(aurora) > planRules.IndexOf(R(planRules, "Heart of Corundum (buster)")) && planRules.IndexOf(aurora) > planRules.IndexOf(R(planRules, "Heart of Light (plan)"))
        && planRules.IndexOf(aurora) > planRules.IndexOf(R(planRules, "Reprisal (plan)")) && Src(aurora).Contains("stackDone") && Src(aurora).Contains("bigMit")
        && aurora.GetType().GetProperty("TargetSource")!.GetValue(aurora)?.ToString() == "self");
    var bolide = planRules.Single(r => Id(r) == 16152);
    E2E("DMU_Mit_GNB: Superbolide only with optSuperbolide (default 0), first rule", planRules.IndexOf(bolide) == 0 && Src(bolide).Contains("bolideWhen")
        && planText.Contains("\"optSuperbolide\": \"0\"") && planText.Contains("\"bolideWhen\": \"optSuperbolide &&"));
    object Opt(object loaded, string name) => ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().First(o => Prop<string>(o, "Name") == name);
    var slot = Opt(plan, "optTankSlot");
    E2E("DMU_Mit_GNB option optTankSlot = 1 (MT), labelled, 2 choices", Prop<string>(slot, "Value") == "1" && Prop<string>(slot, "Label") == "Tank slot"
        && ((IEnumerable)Prop<object>(slot, "Choices")).Cast<object>().Count() == 2);
    var boss = Opt(plan, "optP3Boss");
    E2E("DMU_Mit_GNB option optP3Boss = 0 (by tank slot), 3 choices", Prop<string>(boss, "Value") == "0" && ((IEnumerable)Prop<object>(boss, "Choices")).Cast<object>().Count() == 3);
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var ot = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optTankSlot"] = "2" }])!;
    E2E("DMU_Mit_GNB compiles with optTankSlot = 2 (OT)", Errors(ot).Count == 0 && Prop<string>(Opt(ot, "optTankSlot"), "Value") == "2", string.Join(" | ", Errors(ot)));
    var ex = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optTankSlot"] = "1", ["optP3Boss"] = "2", ["optStackBigBusters"] = "0", ["optCoTankHoC"] = "0", ["optP5EarlyHol"] = "0" }])!;
    E2E("DMU_Mit_GNB compiles with optP3Boss = 2, optStackBigBusters = 0, optCoTankHoC = 0, optP5EarlyHol = 0", Errors(ex).Count == 0 && Prop<string>(Opt(ex, "optP3Boss"), "Value") == "2", string.Join(" | ", Errors(ex)));
    var opts = ((IEnumerable)Prop<object>(plan, "Options")).Cast<object>().ToList();
    foreach (var slotValue in new[] { "1", "2" })
    {
        var sb = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optTankSlot"] = slotValue, ["optSuperbolide"] = "1" }])!;
        E2E($"DMU_Mit_GNB compiles with optSuperbolide = 1, optTankSlot = {slotValue}", Errors(sb).Count == 0, string.Join(" | ", Errors(sb)));
    }
    E2E("DMU_Mit_GNB.json: every option labelled with help", opts.Count == 8 && opts.All(o => !string.IsNullOrEmpty(Prop<string>(o, "Help")) && Prop<string>(o, "Label") != Prop<string>(o, "Name")),
        string.Join(", ", opts.Select(o => Prop<string>(o, "Name"))));
}

// ---- Paladin: two modes ----
foreach (var (m, id, aoe, buttons) in new[] { ("PLD_ST_CustomRules", 11030, false, new uint[] { 9 }), ("PLD_AoE_CustomRules", 11230, true, new uint[] { 7381 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's button", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("PLD Custom Rules modes have no conflicts and run first", Attr("PLD_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("PLD_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("PLD_ST_SimpleMode").Contains("PLD_ST_CustomRules") && !Conflicts("PLD_ST_AdvancedMode").Contains("PLD_ST_CustomRules") && !Conflicts("PLD_AoE_AdvancedMode").Contains("PLD_AoE_CustomRules")
                                && (int)Enum.Parse(presetType, "PLD_ST_CustomRules") > (int)Enum.Parse(presetType, "PLD_ST_AdvancedMode") && (int)Enum.Parse(presetType, "PLD_AoE_CustomRules") > (int)Enum.Parse(presetType, "PLD_AoE_AdvancedMode")
                                && (int)Enum.Parse(presetType, "PLD_ST_CustomRules") > (int)Enum.Parse(presetType, "PLD_ST_SimpleMode") && (int)Enum.Parse(presetType, "PLD_AoE_CustomRules") > (int)Enum.Parse(presetType, "PLD_AoE_SimpleMode"));
E2E("UI order: PLD Custom Rules between Simple and Advanced", Order("PLD_AoE_SimpleMode") < Order("PLD_ST_CustomRules") && Order("PLD_AoE_CustomRules") < Order("PLD_ST_AdvancedMode"));
var pldGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["PLD"])!, "GaugeFields");
E2E("PLD gauge fields", pldGauge.Contains("OathGauge"), string.Join(", ", pldGauge));
E2E("PLD examples embedded", new[] { "PLD_ST", "PLD_AoE", "DMU_Mit_PLD" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    // Upgraded actions: the rules must follow the game's upgrade (Sheltron -> Holy Sheltron, Sentinel -> Guardian,
    // Confiteor -> the Blades, Atonement -> Supplication/Sepulchre) through "hook", or name the upgraded action itself.
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    var st = Rules("PLD_ST.json");
    var stRules = RuleList(st);
    object R(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    E2E("PLD_ST: Imperator / Blade of Honor / Expiacion / Royal Authority are the level 100 ids", Prop<uint>(R(stRules, "Imperator"), "ActionId") == 36921 && Prop<uint>(R(stRules, "Blade of Honor"), "ActionId") == 36922
        && Prop<uint>(R(stRules, "Expiacion"), "ActionId") == 25747 && Prop<uint>(R(stRules, "Royal Authority"), "ActionId") == 3539);
    E2E("PLD_ST: Holy Sheltron, Confiteor combo and the Atonement chain follow the game's upgrade (hook)",
        Prop<bool>(R(stRules, "Holy Sheltron (Oath full)"), "Hook") && Prop<uint>(R(stRules, "Holy Sheltron (Oath full)"), "ActionId") == 3542
        && Prop<bool>(R(stRules, "Confiteor combo"), "Hook") && Prop<bool>(R(stRules, "Sepulchre (Fight or Flight)"), "Hook"));
    E2E("PLD_ST: Intervention targets the boss's target", R(stRules, "Intervention (Oath full, co-tank)").GetType().GetProperty("TargetSource")!.GetValue(R(stRules, "Intervention (Oath full, co-tank)"))?.ToString() == "targetsTarget");
    var planPath = Path.Combine(examples, "DMU_Mit_PLD.json");
    var planText = File.ReadAllText(planPath);
    var plan = Load(planText, planPath);
    var planSet = Prop<object>(plan, "Set");
    var planRules = RuleList(planSet);
    E2E("DMU_Mit_PLD: layer, zone 1363, replaces Fast Blade + Total Eclipse", Prop<bool>(planSet, "Layer") && Prop<string>(planSet, "WhenSource").Contains("1363")
        && ((HashSet<uint>)Prop<object>(planSet, "Replaces")).SetEquals([9u, 7381u]));
    E2E("DMU_Mit_PLD: Guardian through Sentinel's hook, Intervention on the other tank", Prop<bool>(R(planRules, "Guardian (buster)"), "Hook") && Prop<uint>(R(planRules, "Guardian (buster)"), "ActionId") == 17
        && R(planRules, "Intervention (other tank's buster)").GetType().GetProperty("TargetSource")!.GetValue(R(planRules, "Intervention (other tank's buster)"))?.ToString() == "tank");
    var manual = new uint[] { 30, 7385, 27, 7533, 7537 }; // Hallowed Ground, Passage of Arms, Cover, Provoke, Shirk
    E2E("PLD files never press Hallowed Ground, Passage of Arms, Cover, Provoke or Shirk",
        new[] { "PLD_ST.json", "PLD_AoE.json", "DMU_Mit_PLD.json" }.All(f => RuleList(Rules(f)).All(r => !manual.Contains(Prop<uint>(r, "ActionId")))));
    // Bulwark (22) on the tankbusters (the user's request, 2026-10-22): the DMU plan's busters and the marker elsewhere.
    E2E("DMU_Mit_PLD presses Bulwark on the busters", Prop<uint>(R(planRules, "Bulwark (buster)"), "ActionId") == 22);
    E2E("PLD_ST / PLD_AoE press Bulwark on the tankbuster marker",
        new[] { "PLD_ST.json", "PLD_AoE.json" }.All(f => RuleList(Rules(f)).Any(r => Prop<uint>(r, "ActionId") == 22 && Prop<string>(r, "Name") == "Bulwark (tankbuster marker)")));
    object Opt(object loaded, string name) => ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().First(o => Prop<string>(o, "Name") == name);
    var slot = Opt(plan, "optTankSlot");
    E2E("DMU_Mit_PLD option optTankSlot = 1 (MT), labelled, 2 choices", Prop<string>(slot, "Value") == "1" && Prop<string>(slot, "Label") == "Tank slot"
        && ((IEnumerable)Prop<object>(slot, "Choices")).Cast<object>().Count() == 2);
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var ot = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optTankSlot"] = "2" }])!;
    E2E("DMU_Mit_PLD compiles with optTankSlot = 2 (OT)", Errors(ot).Count == 0 && Prop<string>(Opt(ot, "optTankSlot"), "Value") == "2", string.Join(" | ", Errors(ot)));
}

// ---- Dancer: two modes (2026-10-23) ----
foreach (var (m, id, aoe, buttons) in new[] { ("DNC_ST_CustomRules", 4200, false, new uint[] { 15989 }), ("DNC_AoE_CustomRules", 4230, true, new uint[] { 15993 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's button", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("DNC Custom Rules modes have no conflicts", Attr("DNC_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("DNC_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("DNC_ST_SimpleMode").Contains("DNC_ST_CustomRules") && !Conflicts("DNC_ST_AdvancedMode").Contains("DNC_ST_CustomRules") && !Conflicts("DNC_AoE_AdvancedMode").Contains("DNC_AoE_CustomRules"));
E2E("UI order: DNC Custom Rules between Simple and Advanced", Order("DNC_AoE_SimpleMode") < Order("DNC_ST_CustomRules") && Order("DNC_AoE_CustomRules") < Order("DNC_ST_AdvancedMode"));
// Every other preset on Cascade / Windmill (Simple, Advanced, MultiButton, Procc_Windmill...) has a lower id (the highest id runs first).
E2E("DNC Custom Rules ids are the highest on Cascade / Windmill",
    Enum.GetNames(presetType).Where(p => p.StartsWith("DNC_") && !p.EndsWith("_CustomRules") && Attr(p, "ReplaceSkillAttribute") is { } a
        && ((IEnumerable<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).Any(x => (uint)x.Value! is 15989 or 15993))
        .All(p => (int)Enum.Parse(presetType, p) < 4200));
var dncGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["DNC"])!, "GaugeFields");
E2E("DNC gauge fields", new[] { "Feathers", "Esprit", "CompletedSteps", "NextStep", "IsDancing" }.All(dncGauge.Contains), string.Join(", ", dncGauge));
E2E("DNC examples embedded", new[] { "DNC_ST", "DNC_AoE", "DMU_Mit_DNC" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
E2E("partner target kind", RuleTargets.TryParseKind("partner", out var partnerKind) && partnerKind == RuleTarget.Partner && RuleTargets.TryParseKind("dancepartner", out _));
E2E("mouseover target kind", RuleTargets.TryParseKind("mouseover", out var mouseKind) && mouseKind == RuleTarget.MouseOver && RuleTargets.TryParseKind("UIMouseOver", out _));
{
    var castOk = RuleTargets.TryParse("castTarget(Revolting Ruin III) !shield, tank", symbols, out var castSpecs, out var castErr);
    E2E("castTarget(Name) target kind: cast name normalized, filters kept, fallback", castOk && castSpecs.Length == 2 && castSpecs[0].Kind == RuleTarget.CastTarget
        && castSpecs[0].Cast == "revoltingruiniii" && castSpecs[0].Filters.Count == 1 && castSpecs[1].Kind == RuleTarget.Tank, castErr ?? "");
    E2E("castTarget() without a name is an error", !RuleTargets.TryParse("castTarget()", symbols, out _, out var emptyErr) && emptyErr!.Contains("cast name"), emptyErr ?? "");
}
{
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    object R(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    string? Target(object r) => r.GetType().GetProperty("TargetSource")!.GetValue(r)?.ToString();
    // Sage: a party-list hover takes every Eukrasian Diagnosis first (not the plan's Accretion Diagnoses).
    {
        var dps = RuleList(Rules("SGE_ST_DPS.json"));
        var heal = RuleList(Rules("SGE_ST_Heal.json"));
        var eDiag = new[] { R(dps, "Pre-pull Eukrasian Diagnosis on a tank"), R(dps, "Eukrasian Diagnosis on the boss's target (under 2 Addersting)"),
            R(dps, "Eukrasian Diagnosis (spare Eukrasia)"), R(heal, "Eukrasian Diagnosis (Eukrasia is up)") };
        E2E("SGE: Eukrasian Diagnosis tries the mouseover first", eDiag.All(r => Target(r)!.StartsWith("mouseover, ")),
            string.Join(" | ", eDiag.Select(Target)));
    }
    // White Mage: Revolting Ruin III's Aquaveil and Benisons go to the tank it is cast on; the DPS file's own Benison and
    // Aquaveil stand down in Dancing Mad; Solace before Tetragrammaton on the heal button.
    {
        var whmPlan = RuleList(Rules("DMU_Mit_WHM.json"));
        var dps = RuleList(Rules("WHM_ST_DPS.json"));
        var heal = RuleList(Rules("WHM_ST_Heal.json"));
        E2E("WHM plan: Revolting Ruin III's Aquaveil and Benison on castTarget(RevoltingRuinIII)",
            Target(R(whmPlan, "Aquaveil on Revolting Ruin III's tank")) == "castTarget(RevoltingRuinIII) !82 !409 !810 !811 !3255 !1836"
            && Target(R(whmPlan, "Divine Benison on Revolting Ruin III's tank (one for each hit)")) == "castTarget(RevoltingRuinIII) !DivineBenison !82 !409 !810 !811 !3255 !1836");
        var names = heal.Select(r => Prop<string>(r, "Name")).ToList();
        E2E("WHM heal button: Afflatus Solace before Tetragrammaton", names.IndexOf("Afflatus Solace") >= 0 && names.IndexOf("Afflatus Solace") < names.IndexOf("Tetragrammaton (after Afflatus Solace)"));
        E2E("WHM DPS: Tetragrammaton after Solace, Solace as the spot heal", dps.Any(r => Prop<string>(r, "Name") == "Tetragrammaton (spot heal, after Afflatus Solace)")
            && dps.Any(r => Prop<string>(r, "Name") == "Afflatus Solace (spot heal)"));
        // Level-100 dungeon tuning (2026-10-27): Holy III right before Glare III; the pack switch and the DoT check
        // stand down in Dancing Mad.
        var dpsNames = dps.Select(r => Prop<string>(r, "Name")).ToList();
        E2E("WHM DPS: Holy III for a pack right before Glare III", dpsNames.IndexOf("Holy III (a pack)") >= 0
            && dpsNames.IndexOf("Holy III (a pack)") + 1 == dpsNames.IndexOf("Glare III"));
        string Text(string file) => File.ReadAllText(Path.Combine(examples, file));
        E2E("dungeon pack switch and DoT check are off in Dancing Mad",
            Text("WHM_ST_DPS.json").Contains("\"aoeNow\": \"!planZone && ") && Text("WHM_ST_DPS.json").Contains("\"dotWorth\": \"planZone || ")
            && Text("SGE_ST_DPS.json").Contains("\"aoeNow\": \"!planZone && ") && Text("SGE_ST_DPS.json").Contains("\"dotWorth\": \"planZone || ")
            && Text("SGE_ST_DPS.json").Contains("\"dyskMove\": \"!planZone && ") && Text("SGE_AoE_DPS.json").Contains("|| zone != 1363 && optDotCheck && ttk < 12.75"));
        E2E("ttk value registered", fnNames.Contains("ttk"));
    }
    foreach (var f in new[] { "DNC_ST.json", "DNC_AoE.json" })
    {
        var rs = RuleList(Rules(f));
        E2E($"{f}: Closed Position on the partner target kind", Target(R(rs, "Closed Position")) == "partner" && Prop<uint>(R(rs, "Closed Position"), "ActionId") == 16006);
        E2E($"{f}: the four steps by id, each gated on the gauge's next step", new[] { ("Emboite", 15999u), ("Entrechat", 16000u), ("Jete", 16001u), ("Pirouette", 16002u) }
            .All(x => Prop<uint>(R(rs, x.Item1), "ActionId") == x.Item2 && Prop<string>(R(rs, x.Item1), "WhenSource").Contains("gauge(NextStep)")));
        E2E($"{f}: finishes through Standard / Technical Step's hook", Prop<bool>(R(rs, "Standard Finish"), "Hook") && Prop<uint>(R(rs, "Standard Finish"), "ActionId") == 15997
            && Prop<bool>(R(rs, "Technical Finish"), "Hook") && Prop<uint>(R(rs, "Technical Finish"), "ActionId") == 15998);
        E2E($"{f}: a held finish waits (Wait) rather than falling through to a GCD the dance blocks", Prop<uint>(R(rs, "Hold the finish (no enemy in range)"), "ActionId") == 1000004);
    }
    var planPath = Path.Combine(examples, "DMU_Mit_DNC.json");
    var planText = File.ReadAllText(planPath);
    var plan = Load(planText, planPath);
    var planSet = Prop<object>(plan, "Set");
    var planRules = RuleList(planSet);
    E2E("DMU_Mit_DNC: layer, zone 1363, replaces Cascade + Windmill", Prop<bool>(planSet, "Layer") && Prop<string>(planSet, "WhenSource").Contains("1363")
        && ((HashSet<uint>)Prop<object>(planSet, "Replaces")).SetEquals([15989u, 15993u]));
    E2E("DMU_Mit_DNC: Improvised Finish first, through Improvisation's hook, not woven", Prop<string>(planRules[0], "Name") == "Improvised Finish"
        && Prop<bool>(planRules[0], "Hook") && Prop<uint>(planRules[0], "ActionId") == 16014 && !Prop<bool>(planRules[0], "Weave"));
    E2E("DMU_Mit_DNC: Shield Samba woven, Improvisation woven", Prop<uint>(R(planRules, "Shield Samba (plan)"), "ActionId") == 16012 && Prop<bool>(R(planRules, "Shield Samba (plan)"), "Weave")
        && Prop<uint>(R(planRules, "Improvisation (plan)"), "ActionId") == 16014 && Prop<bool>(R(planRules, "Improvisation (plan)"), "Weave"));
    var withOptionsDnc = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    E2E("DNC files never press En Avant",
        new[] { "DNC_ST.json", "DNC_AoE.json", "DMU_Mit_DNC.json" }.All(f => RuleList(Rules(f)).All(r => Prop<uint>(r, "ActionId") != 16010)));
    E2E("DNC files: Curing Waltz only woven, under optWaltzHp party average, never with an Accretion up; not in the plan file",
        new[] { "DNC_ST.json", "DNC_AoE.json" }.All(f => RuleList(Rules(f)).Where(r => Prop<uint>(r, "ActionId") == 16015).ToList() is [var w]
            && Prop<bool>(w, "Weave") && Prop<string>(w, "WhenSource").Contains("partyAvgHp < optWaltzHp") && Prop<string>(w, "WhenSource").Contains("!accretion")
            && File.ReadAllText(Path.Combine(examples, f)) is var t && t.Contains("\"optWaltzHp\": \"40\"") && t.Contains("\"accretion\": \"zone == 1363 && partyWith(1604) > 0\""))
        && RuleList(planSet).All(r => Prop<uint>(r, "ActionId") != 16015));
    foreach (var f in new[] { "DNC_ST.json", "DNC_AoE.json" })
    {
        var p = Path.Combine(examples, f);
        var errs = Errors(Load(File.ReadAllText(p), p));
        E2E($"{f} on disk compiles with no errors", errs.Count == 0, string.Join(" | ", errs));
        var off0 = withOptionsDnc.Invoke(null, [File.ReadAllText(p), p, new Dictionary<string, string> { ["optWaltzHp"] = "0" }])!;
        E2E($"{f} compiles with optWaltzHp = 0", Errors(off0).Count == 0, string.Join(" | ", Errors(off0)));
    }
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var off = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optSamba"] = "0", ["optImprovisation"] = "0" }])!;
    E2E("DMU_Mit_DNC compiles with optSamba = 0, optImprovisation = 0", Errors(off).Count == 0, string.Join(" | ", Errors(off)));
    var stPath = Path.Combine(examples, "DNC_ST.json");
    var guide = withOptions.Invoke(null, [File.ReadAllText(stPath), stPath, new Dictionary<string, string> { ["optEspritHold"] = "80", ["optFeatherWindow"] = "0", ["optDmuOpener"] = "0" }])!;
    E2E("DNC_ST compiles with the guide's pooling (optEspritHold 80, optFeatherWindow 0) and optDmuOpener 0", Errors(guide).Count == 0, string.Join(" | ", Errors(guide)));
}

// ---- Warrior: two modes ----
foreach (var (m, id, aoe, buttons) in new[] { ("WAR_ST_CustomRules", 18030, false, new uint[] { 31 }), ("WAR_AoE_CustomRules", 18230, true, new uint[] { 41 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, false)", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, false]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's button", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
}
E2E("WAR Custom Rules modes have no conflicts and run first", Attr("WAR_ST_CustomRules", "ConflictingCombosAttribute") is null && Attr("WAR_AoE_CustomRules", "ConflictingCombosAttribute") is null
                                && !Conflicts("WAR_ST_Simple").Contains("WAR_ST_CustomRules") && !Conflicts("WAR_ST_Advanced").Contains("WAR_ST_CustomRules") && !Conflicts("WAR_AoE_Advanced").Contains("WAR_AoE_CustomRules")
                                && (int)Enum.Parse(presetType, "WAR_ST_CustomRules") > (int)Enum.Parse(presetType, "WAR_ST_Advanced") && (int)Enum.Parse(presetType, "WAR_AoE_CustomRules") > (int)Enum.Parse(presetType, "WAR_AoE_Advanced")
                                && (int)Enum.Parse(presetType, "WAR_ST_CustomRules") > (int)Enum.Parse(presetType, "WAR_ST_Simple") && (int)Enum.Parse(presetType, "WAR_AoE_CustomRules") > (int)Enum.Parse(presetType, "WAR_AoE_Simple"));
E2E("UI order: WAR Custom Rules between Simple and Advanced", Order("WAR_AoE_Simple") < Order("WAR_ST_CustomRules") && Order("WAR_AoE_CustomRules") < Order("WAR_ST_Advanced"));
// Every other preset on Heavy Swing / Overpower has a lower id (the highest id runs first).
E2E("WAR Custom Rules ids are the highest on Heavy Swing / Overpower",
    Enum.GetNames(presetType).Where(p => p.StartsWith("WAR_") && !p.EndsWith("_CustomRules") && Attr(p, "ReplaceSkillAttribute") is { } a
        && ((IEnumerable<CustomAttributeTypedArgument>)a.ConstructorArguments[0].Value!).Any(x => (uint)x.Value! is 31 or 41))
        .All(p => (int)Enum.Parse(presetType, p) < 18030));
var warGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["WAR"])!, "GaugeFields");
E2E("WAR gauge fields", warGauge.Contains("BeastGauge"), string.Join(", ", warGauge));
E2E("WAR examples embedded", new[] { "WAR_ST", "WAR_AoE", "DMU_Mit_WAR" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    // Upgraded buttons follow the game through "hook" (an alias id fails after the upgrade, the Tengentsu lesson):
    // Fell Cleave -> Inner Chaos, Decimate -> Chaotic Cyclone, Inner Release -> Primal Wrath, Primal Rend -> Primal
    // Ruination, Raw Intuition -> Bloodwhetting, Vengeance -> Damnation.
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    object Rules(string file) => Prop<object>(Load(File.ReadAllText(Path.Combine(examples, file)), Path.Combine(examples, file)), "Set");
    List<object> RuleList(object set) => ((IEnumerable)Prop<object>(set, "Rules")).Cast<object>().ToList();
    object R(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    bool Hooked(List<object> rs, string name, uint baseId) => Prop<bool>(R(rs, name), "Hook") && Prop<uint>(R(rs, name), "ActionId") == baseId;
    var st = RuleList(Rules("WAR_ST.json"));
    var aoe = RuleList(Rules("WAR_AoE.json"));
    // As Wrath does: Fell Cleave / Inner Chaos through Inner Beast's hook, Primal Wrath and Primal Ruination by their own ids.
    bool Own(List<object> rs, string name, uint id) => !Prop<bool>(R(rs, name), "Hook") && Prop<uint>(R(rs, name), "ActionId") == id;
    E2E("WAR_ST: Inner Chaos and Fell Cleaves through Inner Beast's hook, Primal Wrath / Ruination by id, Bloodwhetting through Raw Intuition's",
        Hooked(st, "Inner Chaos", 49) && Hooked(st, "Fell Cleave (Inner Release)", 49) && Hooked(st, "Fell Cleave (Inner Release stacks running out)", 49)
        && Hooked(st, "Fell Cleave (raid buffs)", 49) && Own(st, "Primal Wrath", 36924) && Own(st, "Primal Ruination", 36925) && Hooked(st, "Bloodwhetting (tankbuster marker)", 3551));
    E2E("WAR_AoE: Chaotic Cyclone and Decimates through Decimate's hook, Inner Chaos through Inner Beast's, Primal Wrath / Ruination by id",
        Hooked(aoe, "Chaotic Cyclone", 3550) && Hooked(aoe, "Decimate (Inner Release)", 3550) && Hooked(aoe, "Decimate (gauge)", 3550) && Hooked(aoe, "Inner Chaos", 49)
        && Own(aoe, "Primal Wrath", 36924) && Own(aoe, "Primal Ruination", 36925));
    E2E("WAR_ST: level 100 ids (Inner Release, Primal Rend, Upheaval, Storm's Eye, Tomahawk)", Prop<uint>(R(st, "Inner Release"), "ActionId") == 7389 && Prop<uint>(R(st, "Primal Rend"), "ActionId") == 25753
        && Prop<uint>(R(st, "Upheaval"), "ActionId") == 7387 && Prop<uint>(R(st, "Storm's Eye"), "ActionId") == 45 && Prop<uint>(R(st, "Tomahawk (out of melee)"), "ActionId") == 46);
    E2E("WAR_ST / WAR_AoE: last rule is Wait (Wrath's Cease)", new[] { st, aoe }.All(rs => Prop<string>(rs[^1], "Name") == "Nothing to do" && Prop<uint>(rs[^1], "ActionId") == 1_000_004));
    E2E("WAR_ST / WAR_AoE: Nascent Flash on the marker targets the boss's target",
        new[] { st, aoe }.All(rs => R(rs, "Nascent Flash (tankbuster marker, co-tank)").GetType().GetProperty("TargetSource")!.GetValue(R(rs, "Nascent Flash (tankbuster marker, co-tank)"))?.ToString() == "targetsTarget"));
    var planPath = Path.Combine(examples, "DMU_Mit_WAR.json");
    var planText = File.ReadAllText(planPath);
    var plan = Load(planText, planPath);
    var planSet = Prop<object>(plan, "Set");
    var planRules = RuleList(planSet);
    E2E("DMU_Mit_WAR: layer, zone 1363, replaces Heavy Swing + Overpower", Prop<bool>(planSet, "Layer") && Prop<string>(planSet, "WhenSource").Contains("1363")
        && ((HashSet<uint>)Prop<object>(planSet, "Replaces")).SetEquals([31u, 41u]));
    E2E("DMU_Mit_WAR: Damnation through Vengeance's hook, Bloodwhetting through Raw Intuition's, Nascent Flash on the other tank, Shake It Off first",
        Hooked(planRules, "Damnation (buster)", 44) && Hooked(planRules, "Damnation (buster, one)", 44) && Hooked(planRules, "Bloodwhetting (buster)", 3551)
        && R(planRules, "Nascent Flash (other tank's buster)").GetType().GetProperty("TargetSource")!.GetValue(R(planRules, "Nascent Flash (other tank's buster)"))?.ToString() == "tank"
        && Prop<uint>(planRules[0], "ActionId") == 7388 && planRules.All(r => Prop<bool>(r, "Weave")));
    // Shake It Off dispels Damnation / Thrill / Bloodwhetting: those wait while it is due (sioFirst); it waits while they are up.
    E2E("DMU_Mit_WAR: Damnation, Thrill and Bloodwhetting wait for a due Shake It Off; Shake It Off waits while they are up",
        planRules.Where(r => Prop<uint>(r, "ActionId") is 44 or 40 or 3551).All(r => Prop<string>(r, "WhenSource").Contains("!sioFirst"))
        && Prop<string>(R(planRules, "Shake It Off (plan)"), "WhenSource").Contains("!dispellable"));
    E2E("DMU_Mit_WAR: no healing in the Accretions (Bloodwhetting, Thrill, Nascent Flash, Shake It Off)",
        planRules.Where(r => Prop<uint>(r, "ActionId") is 40 or 3551 or 16464).All(r => Prop<string>(r, "WhenSource").Contains("!noHeal"))
        && File.ReadAllText(planPath).Contains("\"sioWhen\": \"optSheetMit && if(ot, otSio, mtSio) && !noHeal\""));
    var manual = new uint[] { 43, 7533, 7537, 3552 }; // Holmgang, Provoke, Shirk, Equilibrium
    E2E("WAR files never press Holmgang, Provoke, Shirk or Equilibrium",
        new[] { "WAR_ST.json", "WAR_AoE.json", "DMU_Mit_WAR.json" }.All(f => RuleList(Rules(f)).All(r => !manual.Contains(Prop<uint>(r, "ActionId")))));
    // Onslaught (7386) is a dash: always keep one charge for the user's own gap closes, and only from the hitbox ring.
    var onslaught = new[] { st, aoe, planRules }.SelectMany(rs => rs).Where(r => Prop<uint>(r, "ActionId") == 7386).ToList();
    var warDefs = new[] { "WAR_ST.json", "WAR_AoE.json" }.Select(f => File.ReadAllText(Path.Combine(examples, f))).ToList();
    E2E("WAR: every Onslaught rule requires charges(Onslaught) >= 2 and the hitbox ring, standing still",
        onslaught.Count == 2 && onslaught.All(r => Prop<string>(r, "WhenSource").Contains("onsSafe"))
        && warDefs.All(t => t.Contains("\"onsSafe\": \"optOnslaught && inCombat && hasTarget && distance <= 0 && !moving && stoodStill >= 1 && !justUsed(Onslaught, 1.5) && charges(Onslaught) >= 2\"")));
    E2E("WAR: Primal Rend from range only through optRendAtRange (default 0)",
        new[] { st, aoe }.SelectMany(rs => rs).Where(r => Prop<uint>(r, "ActionId") == 25753 && !Prop<bool>(r, "Hook"))
            .All(r => Prop<string>(r, "WhenSource").Contains("inMelee && !moving") || Prop<string>(r, "WhenSource").StartsWith("optRendAtRange"))
        && warDefs.All(t => t.Contains("\"optRendAtRange\": \"0\"")));
    object Opt(object loaded, string name) => ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().First(o => Prop<string>(o, "Name") == name);
    var slot = Opt(plan, "optTankSlot");
    E2E("DMU_Mit_WAR option optTankSlot = 1 (MT), labelled, 2 choices", Prop<string>(slot, "Value") == "1" && Prop<string>(slot, "Label") == "Tank slot"
        && ((IEnumerable)Prop<object>(slot, "Choices")).Cast<object>().Count() == 2);
    var withOptions = engine.GetMethod("CompileWithOptions", BindingFlags.NonPublic | BindingFlags.Static)!;
    var ot = withOptions.Invoke(null, [planText, planPath, new Dictionary<string, string> { ["optTankSlot"] = "2" }])!;
    E2E("DMU_Mit_WAR compiles with optTankSlot = 2 (OT)", Errors(ot).Count == 0 && Prop<string>(Opt(ot, "optTankSlot"), "Value") == "2", string.Join(" | ", Errors(ot)));
    // Every "opt..." define has an options entry with a label and help.
    foreach (var f in new[] { "WAR_ST.json", "WAR_AoE.json", "DMU_Mit_WAR.json" })
    {
        var loaded = Load(File.ReadAllText(Path.Combine(examples, f)), Path.Combine(examples, f));
        var opts = ((IEnumerable)Prop<object>(loaded, "Options")).Cast<object>().ToList();
        E2E($"{f}: every option labelled with help", opts.Count > 0 && opts.All(o => !string.IsNullOrEmpty(Prop<string>(o, "Help")) && Prop<string>(o, "Label") != Prop<string>(o, "Name")),
            string.Join(", ", opts.Select(o => Prop<string>(o, "Name"))));
    }
}

// ---- White Mage: four modes ----
foreach (var (m, id, aoe, heal, buttons) in new[] {
    ("WHM_ST_CustomRules", 19900, false, false, new uint[] { 119, 127, 3568, 7431, 16533, 25859 }),
    ("WHM_AoE_CustomRules", 19901, true, false, new uint[] { 139, 25860 }),
    ("WHM_STHeal_CustomRules", 19902, false, true, new uint[] { 120 }),
    ("WHM_AoEHeal_CustomRules", 19903, true, true, new uint[] { 124 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, {heal})", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, heal]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's buttons", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
    if (heal) E2E($"{m} is [Retargeted]", Attr(m, "RetargetedAttribute") is not null);
}
E2E("WHM Custom Rules modes have no conflicts and run first", new[] { "WHM_ST_CustomRules", "WHM_AoE_CustomRules", "WHM_STHeal_CustomRules", "WHM_AoEHeal_CustomRules" }.All(m => Attr(m, "ConflictingCombosAttribute") is null)
                                && !Conflicts("WHM_ST_Simple_DPS").Contains("WHM_ST_CustomRules") && !Conflicts("WHM_ST_MainCombo").Contains("WHM_ST_CustomRules") && !Conflicts("WHM_STHeals").Contains("WHM_STHeal_CustomRules") && !Conflicts("WHM_AoEHeals").Contains("WHM_AoEHeal_CustomRules")
                                && (int)Enum.Parse(presetType, "WHM_ST_CustomRules") > (int)Enum.Parse(presetType, "WHM_ST_MainCombo") && (int)Enum.Parse(presetType, "WHM_STHeal_CustomRules") > (int)Enum.Parse(presetType, "WHM_STHeals") && (int)Enum.Parse(presetType, "WHM_AoEHeal_CustomRules") > (int)Enum.Parse(presetType, "WHM_AoEHeals"));
E2E("UI order: WHM Custom Rules between Simple and Advanced", Order("WHM_Simple_AoEHeals") < Order("WHM_ST_CustomRules") && Order("WHM_AoEHeal_CustomRules") < Order("WHM_ST_MainCombo"));
var whmGauge = Prop<IReadOnlyList<string>>(symbolsType.GetMethod("For")!.Invoke(null, ["WHM"])!, "GaugeFields");
E2E("WHM gauge fields", new[] { "Lily", "BloodLily", "LilyTimer" }.All(whmGauge.Contains), string.Join(", ", whmGauge));
E2E("WHM examples embedded", new[] { "WHM_ST_DPS", "WHM_ST_Heal", "WHM_AoE_Heal", "DMU_Mit_WHM" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));

// ---- Astrologian: four modes, the four example files and the plan ----
foreach (var (m, id, aoe, heal, buttons) in new[] {
    ("AST_ST_CustomRules", 1198, false, false, new uint[] { 3596, 3598, 7442, 16555, 25871 }),
    ("AST_AoE_CustomRules", 1199, true, false, new uint[] { 3615, 25872 }),
    ("AST_STHeal_CustomRules", 1200, false, true, new uint[] { 3594 }),
    ("AST_AoEHeal_CustomRules", 1201, true, true, new uint[] { 3600 }) })
{
    E2E($"{m} = {id}, [CustomRulesCombo]", (int)Enum.Parse(presetType, m) == id && Attr(m, "CustomRulesCombo") is not null);
    E2E($"{m} AutoAction({aoe}, {heal})", Attr(m, "AutoActionAttribute")!.ConstructorArguments.Select(a => a.Value).SequenceEqual([aoe, heal]));
    var rep = ((IEnumerable<CustomAttributeTypedArgument>)Attr(m, "ReplaceSkillAttribute")!.ConstructorArguments[0].Value!).Select(a => (uint)a.Value!).ToList();
    E2E($"{m} replaces the Simple mode's buttons", rep.SequenceEqual(buttons), string.Join(",", rep));
    E2E($"{m} combo class is a direct CustomCombo subclass", wrath.GetType($"WrathCombo.CustomRules.{m}")?.BaseType?.Name == "CustomCombo");
    E2E($"{m} UI name", !string.IsNullOrEmpty(res.GetString($"{m}_Name", System.Globalization.CultureInfo.InvariantCulture)));
    if (heal) E2E($"{m} is [Retargeted]", Attr(m, "RetargetedAttribute") is not null);
}
int V(string preset) => (int)Enum.Parse(presetType, preset);
E2E("AST Custom Rules modes have no conflicts and run first", new[] { "AST_ST_CustomRules", "AST_AoE_CustomRules", "AST_STHeal_CustomRules", "AST_AoEHeal_CustomRules" }.All(m => Attr(m, "ConflictingCombosAttribute") is null)
                                && V("AST_ST_CustomRules") > V("AST_ST_Simple_DPS") && V("AST_ST_CustomRules") > V("AST_ST_DPS") && V("AST_AoE_CustomRules") > V("AST_AOE_Simple_DPS") && V("AST_AoE_CustomRules") > V("AST_AOE_DPS")
                                && V("AST_STHeal_CustomRules") > V("AST_Simple_ST_Heals") && V("AST_STHeal_CustomRules") > V("AST_ST_Heals") && V("AST_AoEHeal_CustomRules") > V("AST_Simple_AoE_Heals") && V("AST_AoEHeal_CustomRules") > V("AST_AoE_Heals"));
E2E("UI order: AST Custom Rules between Simple and Advanced", Order("AST_Simple_AoE_Heals") < Order("AST_ST_CustomRules") && Order("AST_AoEHeal_CustomRules") < Order("AST_ST_DPS"));
E2E("AST examples embedded", new[] { "AST_ST_DPS", "AST_AoE_DPS", "AST_ST_Heal", "AST_AoE_Heal", "DMU_Mit_AST", "DUN_AST" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));
{
    var examples = Path.GetDirectoryName(Path.GetFullPath(samPath))!;
    List<object> AstRules(string f) => ((IEnumerable)Prop<object>(Prop<object>(Load(File.ReadAllText(Path.Combine(examples, f)), Path.Combine(examples, f)), "Set"), "Rules")).Cast<object>().ToList();
    string Sig(object r) => string.Join(" | ", new[] { "ActionName", "WhenSource", "TargetSource", "Weave", "Hook" }.Select(n => r.GetType().GetProperty(n)?.GetValue(r)?.ToString() ?? ""));
    var st = AstRules("AST_ST_DPS.json").GroupBy(r => Prop<string>(r, "Name")).ToDictionary(g => g.Key, g => Sig(g.First()));
    var aoeRules = AstRules("AST_AoE_DPS.json");
    var shared = aoeRules.Where(r => st.ContainsKey(Prop<string>(r, "Name"))).ToList();
    var differ = shared.Where(r => st[Prop<string>(r, "Name")] != Sig(r)).Select(r => Prop<string>(r, "Name")).ToList();
    // AST_AoE_DPS.json copies the single-target file's burst, card, heal and moving rules: every rule both name alike is identical.
    E2E($"AST_AoE_DPS: {shared.Count} rules shared with AST_ST_DPS, all identical", shared.Count >= 40 && differ.Count == 0, string.Join(", ", differ));
    E2E("AST_AoE_DPS spreads Combust with dot(4)", aoeRules.Any(r => Prop<string>(r, "Name") == "Combust (spread on the pack)" && r.GetType().GetProperty("TargetSource")!.GetValue(r) as string == "dot(4)"));

    // 2026-11-01, the five top FFLogs Astrologians (Dancing Mad) against the rules:
    var stRules = AstRules("AST_ST_DPS.json");
    object AR(List<object> rs, string name) => rs.First(r => Prop<string>(r, "Name") == name);
    string When(List<object> rs, string name) => Prop<string>(AR(rs, name), "WhenSource");
    string? Target(List<object> rs, string name) => AR(rs, name).GetType().GetProperty("TargetSource")!.GetValue(AR(rs, name)) as string;
    Dictionary<string, string> AstDefines(string f) => ((IEnumerable)Prop<object>(Prop<object>(Load(File.ReadAllText(Path.Combine(examples, f)), Path.Combine(examples, f)), "Set"), "Defines")).Cast<object>()
        .ToDictionary(d => (string)d.GetType().GetField("Item1")!.GetValue(d)!, d => (string)d.GetType().GetField("Item2")!.GetValue(d)!);
    // The burst goes while the GCD is held for movement too (weave-only, the rules' Divinations came 0.3-5.7 s after theirs).
    var burst = new[] { "Divination", "Play I (The Balance or The Spear) in Divination", "Lightspeed (burst)", "Lord of Crowns", "The Spire before the next draw",
        "The Ewer before the next draw", "The Arrow before the next draw", "The Bole before the next draw", "Lady of Crowns before the next draw", "Draw", "Oracle" };
    var notHeld = burst.Where(n => !When(stRules, n).StartsWith("(canWeave || gcdHeld) && ") || Prop<bool>(AR(stRules, n), "Weave")).ToList();
    E2E("AST_ST_DPS: the burst also while the GCD is held (canWeave || gcdHeld, not weave-only)", notHeld.Count == 0, string.Join(", ", notHeld));
    // Earthly Star where the boss stands (their stars 0-3.5 y from it, healing 7.8-8.0 of 8), where you stand with the option off.
    E2E("AST_ST_DPS: Earthly Star on the enemy (optStarOnTarget), where you stand otherwise",
        Target(stRules, "Earthly Star (on cooldown)") == "enemy, self" && When(stRules, "Earthly Star (on cooldown)").Contains("&& optStarOnTarget &&")
        && Target(stRules, "Earthly Star (on cooldown, where you stand)") == "self" && When(stRules, "Earthly Star (on cooldown, where you stand)").Contains("&& !optStarOnTarget &&")
        && Target(stRules, "Earthly Star on the countdown") == "enemy, self" && Target(stRules, "Earthly Star on the countdown (where you stand)") == "self"
        && AstDefines("AST_ST_DPS.json")["optStarOnTarget"] == "1");
    E2E("AST_AoE_DPS: the pack's Earthly Star on the enemy too",
        Target(aoeRules, "Earthly Star (a gathered pack, or on cooldown in boss fights)") == "enemy, self"
        && Target(aoeRules, "Earthly Star (a gathered pack, or on cooldown in boss fights; where you stand)") == "self");
    // A charged star goes off inside Divination when it would go off by itself in its last second or after it.
    var inBurst = When(stRules, "Stellar Detonation (charged, before Divination runs out)");
    E2E("AST_ST_DPS: Stellar Detonation inside Divination (optStarInBurst)", inBurst.Contains("optStarInBurst && ") && inBurst.Contains("buff(GiantDominance) && divUp && buffTime(GiantDominance) > buffTime(Buffs.Divination) - 1")
        && inBurst.EndsWith("&& !eqHold") && AstDefines("AST_ST_DPS.json")["optStarInBurst"] == "1", inBurst);
    // Dancing Mad Phase 3: the star is placed until 7 s after the second Decisive Battle (charged by the plan's pre-Accretion
    // detonation at Earthquake + 5.2, Earthquake being 12.3 s after it); the top five placed theirs 0.8 s before to 4.1 s after.
    var stDef = AstDefines("AST_ST_DPS.json");
    E2E("AST_ST_DPS: Phase 3 star held from the second Decisive Battle + 7 s", stDef["starKeep"] == "zone == 1363 && castsSeen(TheDecisiveBattle) >= 2 && sinceCast(TheDecisiveBattle) >= 7 && castsSeen(Earthquake) == 0 || eqHold"
        && 7 + 10 <= 12.26 + 5.2, stDef["starKeep"]);
    // DMU_Mit_AST: no cast starts in the 1.5 s before the Ultima Repeaters' 1.4 s Collective Unconscious window (a cast from
    // +2.8 s covered it in the fourth one's replay).
    var planDef = AstDefines("DMU_Mit_AST.json");
    E2E("DMU_Mit_AST: Ultima Repeater Collective Unconscious look-ahead", planDef["cuRepeaterSoon"].Contains("sinceCast(UltimaRepeater) >= 1.5 && sinceCast(UltimaRepeater) < 3")
        && planDef["cuRepeater"].Contains("sinceCast(UltimaRepeater) >= 3 &&") && planDef["mitSoon"].Contains("|| cuRepeaterSoon && cd(CollectiveUnconscious) < 3)"), planDef["mitSoon"]);
    // Earthquake: Microcosmos heals the last Accretion once the first removal's vulnerability (3372) is gone and 3.5 s have
    // passed (four of the five removed the second so, 4.0-7.2 s after the first); never while two are up.
    var planRules = AstRules("DMU_Mit_AST.json");
    var lastAcc = planDef["microLastAcc"];
    E2E("DMU_Mit_AST: Microcosmos for the last Accretion, safe from the first removal", lastAcc.Contains("buff(Buffs.Macrocosmos) && accretion && !accretions2 && partyWith(3372) == 0 && partyLost(1604) >= 3.5")
        && Prop<string>(AR(planRules, "Earthquake: Microcosmos for the last Accretion"), "ActionName") == "Macrocosmos"
        && planRules.FindIndex(r => Prop<string>(r, "Name") == "Earthquake: Microcosmos for the last Accretion") < planRules.FindIndex(r => Prop<string>(r, "Name") == "Sun Sign (plan, last chance)"), lastAcc);
    // Earthquake's Macrocosmos from 2.6 s into the cast (its own heal at +17.6 or later, past that Microcosmos window; from 1 s
    // it fell on the last holder 2.3 s after the first removal in one of the user's pulls), and no GCD from 1.1 s so its
    // window (1.8 s, shorter than a GCD) is never covered; the hold is a Wait, not an instant Combust III (its GCD ran past it).
    var hold = AR(planRules, "Hold the GCD (the Earthquake Macrocosmos is due)");
    E2E("DMU_Mit_AST: Earthquake Macrocosmos from +2.6 s, the GCD held from +1.1 s", planDef["macroEq"].Contains("sinceCast(Earthquake) >= 2.6 && sinceCast(Earthquake) < 4.4")
        && planDef["macroEqSoon"].Contains("sinceCast(Earthquake) >= 1.1 && sinceCast(Earthquake) < 2.6") && Prop<uint>(hold, "ActionId") == 1000004
        && Prop<string>(hold, "WhenSource") == "macroEqSoon && !casting && remainingGcd < 0.6" && 2.6 + 15 >= 14.1 + 3.5, planDef["macroEq"]);    // Every level: no level gate, spells by their hooked base form.
    // DUN_AST.json (2026-11-03): the dungeon layer on both DPS buttons, only where `dungeon` is 1 (never Dancing Mad, raids,
    // trials or alliance raids); tank heals at the user's own hand-pressed thresholds, each behind its option, never on an
    // invulnerable tank.
    {
        var dunPath = Path.Combine(examples, "DUN_AST.json");
        var dunSet = Prop<object>(Load(File.ReadAllText(dunPath), dunPath), "Set");
        var dunRules = AstRules("DUN_AST.json");
        var dunDef = AstDefines("DUN_AST.json");
        var replaces = ((IEnumerable<string>)dunSet.GetType().GetProperty("ReplacesNames")!.GetValue(dunSet)!).ToHashSet();
        var invuln = new[] { "!82", "!409", "!810", "!811", "!3255", "!1836" };
        var badRules = dunRules.Where(r => !Prop<string>(r, "WhenSource").Contains("tankUp") || !System.Text.RegularExpressions.Regex.IsMatch(Prop<string>(r, "WhenSource"), @"optDun\w+ > 0")
            || r.GetType().GetProperty("TargetSource")!.GetValue(r) is string ts && !invuln.All(ts.Contains)).Select(r => Prop<string>(r, "Name")).ToList();
        E2E("DUN_AST: a layer on both DPS buttons, only in dungeons; every rule behind its option, tank alive and not invulnerable",
            Prop<bool>(dunSet, "Layer") && Prop<string>(dunSet, "WhenSource") == "dungeon && inCombat"
            && new[] { "Malefic", "FallMalefic", "Gravity", "Gravity2" }.All(replaces.Contains) && dunRules.Count == 7 && badRules.Count == 0
            && dunDef["optDunExaltationHp"] == "90" && dunDef["optDunIntersectionHp"] == "85" && dunDef["optDunDignityHp"] == "80"
            && dunDef["optDunAoeHealHp"] == "65" && dunDef["optDunRegenHp"] == "80" && dunDef["tankUp"] == "tankHp > 0 && !tankInvuln",
            string.Join(", ", badRules));
    }
    foreach (var f in new[] { "AST_ST_DPS.json", "AST_AoE_DPS.json", "AST_ST_Heal.json", "AST_AoE_Heal.json" })
    {
        var text = File.ReadAllText(Path.Combine(examples, f));
        E2E($"{f}: no level gate, no level-100-only spell named", !System.Text.RegularExpressions.Regex.IsMatch(text, @"""when"":\s*""level >=") && !text.Contains("\"action\": \"Combust3\"") && !text.Contains("\"action\": \"Gravity2\"") && !text.Contains("\"action\": \"HeliosConjuction\""));
    }
}

foreach (var path in args.Skip(3))
{
    var loaded = Load(File.ReadAllText(path), path);
    var errs = Errors(loaded);
    E2E($"{Path.GetFileName(path)} compiles", errs.Count == 0, string.Join(" | ", errs));
    if (loaded.GetType().GetProperty("Set")!.GetValue(loaded) is { } set2)
    {
        var rules2 = ((IEnumerable)set2.GetType().GetProperty("Rules")!.GetValue(set2)!).Cast<object>().ToList();
        var targets = rules2.Where(r => r.GetType().GetProperty("TargetSource")!.GetValue(r) is not null).ToList();
        var isLayer = (bool)set2.GetType().GetProperty("Layer")!.GetValue(set2)!;
        E2E($"{Path.GetFileName(path)}: {rules2.Count} rules, {targets.Count} targeted, replaces {string.Join(",", (IEnumerable<string>)set2.GetType().GetProperty("ReplacesNames")!.GetValue(set2)!)}{(isLayer ? ", layer" : "")}", rules2.Count > 0);
        if (Path.GetFileName(path).StartsWith("DMU_") || Path.GetFileName(path).StartsWith("Mit_") || Path.GetFileName(path).StartsWith("DUN_"))
            E2E($"{Path.GetFileName(path)} is a layer", isLayer);
    }
}
E2E("SGE examples embedded", new[] { "SGE_ST_DPS", "SGE_AoE_DPS", "SGE_ST_Heal", "SGE_AoE_Heal" }.All(n => wrath.GetManifestResourceNames().Contains($"WrathCombo.CustomRules.Examples.{n}.json")));

// Every embedded example compiles with its own defaults.
foreach (var exRes in wrath.GetManifestResourceNames().Where(n => n.StartsWith("WrathCombo.CustomRules.Examples.") && n.EndsWith(".json")))
{
    using var stream = wrath.GetManifestResourceStream(exRes)!;
    var text = new StreamReader(stream).ReadToEnd();
    var exLoaded = Load(text, exRes);
    var errs = Errors(exLoaded);
    E2E($"embedded {exRes[32..]} compiles", errs.Count == 0, string.Join(" | ", errs));
    // Every option has a label and a tooltip written for it (not the name split into words, not an empty tooltip).
    var exOpts = ((System.Collections.IEnumerable?)exLoaded.GetType().GetProperty("Options")!.GetValue(exLoaded))?.Cast<object>().ToList() ?? [];
    var unexplained = exOpts.Select(o => (Name: (string)o.GetType().GetProperty("Name")!.GetValue(o)!, Help: (string)o.GetType().GetProperty("Help")!.GetValue(o)!))
        .Where(o => o.Help.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(text, @"""options""[\s\S]*""" + o.Name + @""":\s*\{[^}]*""label""")).Select(o => o.Name).ToList();
    E2E($"embedded {exRes[32..]} options labelled and explained", unexplained.Count == 0, string.Join(", ", unexplained));
}
Console.WriteLine($"E2E: {(e2eFail == 0 ? "all passed" : $"{e2eFail} failed")}");
return fail + e2eFail == 0 ? 0 : 1;

#endregion

internal sealed class FakeSymbols(
    Dictionary<string, uint> actions,
    Dictionary<string, uint> statuses,
    Dictionary<string, Func<double>> gauge) : IRuleSymbols
{
    public bool TryResolveAction(string name, out uint id) => actions.TryGetValue(name, out id);
    public bool TryResolveStatus(string name, out uint id) => statuses.TryGetValue(name, out id);
    public bool TryResolveGauge(string field, out Func<double>? getter) => gauge.TryGetValue(field, out getter);
}

internal sealed class FakeRuntime : IRuleRuntime
{
    public HashSet<uint> Ready = [];
    public Dictionary<uint, uint> Hooks { get; } = [];
    public List<(uint Action, uint Trigger, RuleTarget Target)> Retargets = [];
    public bool Weave;
    public int WeaveCalls;
    public bool CanWeave() { WeaveCalls++; return Weave; }
    public bool ActionReady(uint actionId) => Ready.Contains(actionId);
    public uint OriginalHook(uint actionId) => Hooks.TryGetValue(actionId, out var h) ? h : actionId;
    public HashSet<RuleTarget> NoTargetFor = [];
    public uint Retarget(uint action, uint trigger, RuleTargetSpec[] targets) { Retargets.Add((action, trigger, targets[0].Kind)); return NoTargetFor.Contains(targets[0].Kind) ? 0 : action; }
}
