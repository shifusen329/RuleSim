namespace RuleSim.Actions;

// What the server keeps for one actor that its statuses don't carry: the job gauge, the linear
// combo, and the job's own timers and counters.
public sealed class JobState
{
    public const int GaugeLength = 16;

    public Job Job { get; private set; }

    // The ActorGauge payload: byte 0 the class job, bytes 2..15 the gauge values, which land on the
    // client's gauge struct from its offset 0x08.
    public byte[] Gauge { get; } = new byte[GaugeLength];

    public uint ComboAction { get; internal set; }
    public float ComboTimer { get; internal set; }

    internal int BlitzBuildGcdsLeft;
    internal float ImprovisationElapsed;
    internal uint PendingCast;
    internal bool PendingCastGrantsDualcast;
    internal readonly Dictionary<TimedGauge, (float Timer, int LastValue)> Timers = [];

    public JobState(Job job) => Reset(job);

    public void Reset(Job job)
    {
        Job = job;
        Array.Clear(Gauge);
        Gauge[0] = (byte)job;
        ComboAction = 0;
        ComboTimer = 0f;
        BlitzBuildGcdsLeft = 0;
        ImprovisationElapsed = 0f;
        PendingCast = 0;
        PendingCastGrantsDualcast = false;
        Timers.Clear();
    }
}

// A field of the gauge payload, addressed by its offset on the client's gauge struct.
internal class GaugeField(int structOffset, int size = 1, bool signed = false)
{
    private const int PayloadOffset = 6;
    private readonly int offset = structOffset - PayloadOffset;

    // Its byte in the payload.
    public int Index => offset;

    public int Read(byte[] gauge) => size switch
    {
        1 => signed ? (sbyte)gauge[offset] : gauge[offset],
        _ => signed ? BitConverter.ToInt16(gauge, offset) : BitConverter.ToUInt16(gauge, offset),
    };

    public void Write(byte[] gauge, int value)
    {
        if (size == 1)
        {
            gauge[offset] = unchecked((byte)value);
            return;
        }
        var v = unchecked((ushort)value);
        gauge[offset] = (byte)v;
        gauge[offset + 1] = (byte)(v >> 8);
    }
}

// A capped resource on the gauge (cartridges, beast gauge, lilies): additions clamp to [0, Max].
internal sealed class ResourceGauge(int structOffset, int max, int size = 1) : GaugeField(structOffset, size)
{
    public int Max => max;

    public void Add(byte[] gauge, int amount) => Write(gauge, Math.Clamp(Read(gauge) + amount, 0, max));
}

// A gauge the server changes on a timer: regen adds Amount every Seconds up to its cap; decay
// empties it Seconds after the last change. TimerField is the gauge's own visible countdown, in ms.
internal sealed class TimedGauge(Job job, ResourceGauge gauge, float seconds, bool decay, int amount = 1, GaugeField? timerField = null)
{
    public Job Job => job;
    public ResourceGauge Gauge => gauge;
    public float Seconds => seconds;
    public bool Decay => decay;
    public int Amount => amount;
    public GaugeField? TimerField => timerField;
}
