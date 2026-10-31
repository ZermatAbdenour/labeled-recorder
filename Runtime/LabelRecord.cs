namespace LabeledRecorder
{
    /// <summary>
    /// The kind of entry a <see cref="LabelRecord"/> describes.
    /// </summary>
    public enum LabelOp : byte
    {
        /// <summary>A point in time. Written to the <c>events</c> array with <c>kind: "point"</c>.</summary>
        Event = 0,
        /// <summary>The opening edge of a span. Paired with <see cref="SpanEnd"/> by the writer.</summary>
        SpanBegin = 1,
        /// <summary>The closing edge of a span.</summary>
        SpanEnd = 2,
        /// <summary>A new value for a named game-data channel. Written to the <c>signals</c> array.</summary>
        Signal = 3,
        /// <summary>An editorially interesting moment, carrying a score. Written to the <c>markers</c> array.</summary>
        Marker = 4,
        /// <summary>A chapter boundary. Written to the <c>chapters</c> array.</summary>
        Chapter = 5,
        /// <summary>A key/value fact about the session as a whole. Written to <c>session.meta</c>.</summary>
        Meta = 6
    }

    /// <summary>
    /// One label as captured on the game thread, before it is resolved to a media timecode.
    /// <para>
    /// This is internal plumbing between the runtime assembly and the Editor assembly. Game code should never
    /// construct these directly: use <see cref="GameLabel"/>, whose calls are removed from player builds.
    /// </para>
    /// </summary>
    public struct LabelRecord
    {
        /// <summary>What this record describes.</summary>
        public LabelOp Op;

        /// <summary>The dotted label name, for example <c>combat.kill</c>.</summary>
        public string Name;

        /// <summary>The value of <see cref="LabelSink.Time"/> when the record was pushed.</summary>
        public double Time;

        /// <summary>The value of <see cref="LabelSink.Frame"/> when the record was pushed.</summary>
        public int Frame;

        /// <summary>For <see cref="LabelOp.Marker"/>, the caller-supplied importance in the 0..1 range.</summary>
        public float Score;

        /// <summary>How many of the <c>K0..K3</c>/<c>V0..V3</c> pairs carry a payload.</summary>
        public byte FieldCount;

        /// <summary>Payload keys.</summary>
        public string K0, K1, K2, K3;

        /// <summary>Payload values.</summary>
        public LabelValue V0, V1, V2, V3;

        /// <summary>
        /// Reads a payload pair by index.
        /// </summary>
        /// <param name="index">The pair index, 0 to 3.</param>
        /// <param name="key">Receives the payload key.</param>
        /// <param name="value">Receives the payload value.</param>
        public void GetField(int index, out string key, out LabelValue value)
        {
            switch (index)
            {
                case 0: key = K0; value = V0; return;
                case 1: key = K1; value = V1; return;
                case 2: key = K2; value = V2; return;
                case 3: key = K3; value = V3; return;
                default: key = null; value = default; return;
            }
        }
    }
}
