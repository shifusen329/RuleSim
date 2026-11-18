using System.Text;
using Lumina.Excel.Sheets;
using Lumina.Text.Expressions;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;
using LuminaAction = Lumina.Excel.Sheets.Action;

// GameDump (2026-11-18): Action rows and their tooltips from the local game data, the tooltip's level branches evaluated at
// --level (default 100), so level-dependent potencies print as the game shows them at that level.
//   dotnet tools/GameDump/bin/Release/net10.0/GameDump.dll actions <id|id-id> [...] [--level=100] [--job=35] [--game=<game dir>] [--why]
//   dotnet tools/GameDump/bin/Release/net10.0/GameDump.dll statuses <id|id-id> [...]
// Prints id | name | category | level | cast s | recast s | charges | tooltip. --why prints each branch condition it met.
var game = args.FirstOrDefault(a => a.StartsWith("--game="))?[7..] ?? Environment.GetEnvironmentVariable("FFXIV_GAME")
    ?? @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game";
var level = uint.Parse(args.FirstOrDefault(a => a.StartsWith("--level="))?[8..] ?? "100");
var why = args.Contains("--why");
var lumina = new Lumina.GameData(Path.Combine(game, "sqpack"), new Lumina.LuminaOptions { PanicOnSheetChecksumMismatch = false });
var ids = new List<uint>();
foreach (var a in args.Skip(1).Where(a => !a.StartsWith("--")))
{
    var p = a.Split('-');
    if (p.Length == 2) for (var i = uint.Parse(p[0]); i <= uint.Parse(p[1]); i++) ids.Add(i);
    else ids.Add(uint.Parse(a));
}
var notes = new List<string>();

// A player parameter in a tooltip condition: 68 is the job (a tooltip shows a job's own numbers only when it matches; the
// action's own job, or --job), 72 the level the tooltip is drawn for.
uint job = 0;
uint Param(uint p) => p switch { 68 => job, 72 => level, _ => 0 };

long Num(ReadOnlySeExpression e)
{
    if (e.TryGetUInt(out var u)) return u;
    if (e.TryGetParameterExpression(out _, out var operand) && operand.TryGetUInt(out var p)) return Param(p);
    if (e.TryGetBinaryExpression(out var t, out var l, out var r)) return Cond(t, l, r) ? 1 : 0;
    return 0;
}

bool Cond(byte type, ReadOnlySeExpression l, ReadOnlySeExpression r)
{
    long a = Num(l), b = Num(r);
    var ok = (ExpressionType)type switch
    {
        ExpressionType.GreaterThanOrEqualTo => a >= b, ExpressionType.GreaterThan => a > b,
        ExpressionType.LessThanOrEqualTo => a <= b, ExpressionType.LessThan => a < b,
        ExpressionType.Equal => a == b, ExpressionType.NotEqual => a != b, _ => false,
    };
    if (why) notes.Add($"[{(ExpressionType)type} {a} {b} -> {ok}]");
    return ok;
}

bool Truth(ReadOnlySeExpression e) =>
    e.TryGetBinaryExpression(out var t, out var l, out var r) ? Cond(t, l, r) : Num(e) != 0;

string Branch(ReadOnlySeExpression e) =>
    e.TryGetString(out var s) ? Eval(s) : e.TryGetUInt(out var u) ? u.ToString() : "";

string Eval(ReadOnlySeString s)
{
    var sb = new StringBuilder();
    foreach (var payload in s)
    {
        if (payload.Type == ReadOnlySePayloadType.Text) { sb.Append(Encoding.UTF8.GetString(payload.Body.Span)); continue; }
        switch (payload.MacroCode)
        {
            case MacroCode.NewLine: sb.Append(' '); break;
            case MacroCode.If:
                if (payload.TryGetExpression(out var c, out var yes, out var no)) sb.Append(Truth(c) ? Branch(yes) : Branch(no));
                break;
            default:
                // Num, Kilo and the like carry their value as the only expression; colour and style macros carry none worth printing.
                if (payload.MacroCode is MacroCode.Num or MacroCode.Kilo or MacroCode.Sec && payload.TryGetExpression(out var v))
                    sb.Append(Num(v));
                break;
        }
    }
    return sb.ToString();
}

static string Flat(string s) => s.Replace("\r", " ").Replace("\n", " ");
if (args.Length > 0 && args[0] == "statuses")
{
    var st = lumina.GetExcelSheet<Status>()!;
    foreach (var id in ids)
        if (st.TryGetRow(id, out var r))
            Console.WriteLine($"{id} | {r.Name.ExtractText()} | maxStacks {r.MaxStacks} | {Flat(Eval(r.Description))}");
    return;
}
var actions = lumina.GetExcelSheet<LuminaAction>()!;
var transient = lumina.GetExcelSheet<ActionTransient>()!;
foreach (var id in ids)
{
    if (!actions.TryGetRow(id, out var r)) continue;
    notes.Clear();
    job = args.FirstOrDefault(a => a.StartsWith("--job="))?[6..] is { } j ? uint.Parse(j) : r.ClassJob.RowId;
    var desc = transient.TryGetRow(id, out var t) ? Flat(Eval(t.Description)) : "";
    Console.WriteLine($"{id} | {r.Name.ExtractText()} | cat {r.ActionCategory.RowId} | lv {r.ClassJobLevel} | cast {r.Cast100ms / 10.0:0.0} | " +
                      $"recast {r.Recast100ms / 10.0:0.0} | charges {r.MaxCharges} | {desc}" + (why ? "  " + string.Join(" ", notes) : ""));
}
