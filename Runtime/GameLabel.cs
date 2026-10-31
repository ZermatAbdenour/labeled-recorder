using System.Diagnostics;

namespace LabeledRecorder
{
    /// <summary>
    /// The label API for game code. Drop any of these calls anywhere in your gameplay scripts; while a Unity
    /// Recorder capture is running in the Editor they are written to a sidecar label file next to the video, and
    /// the rest of the time they do nothing.
    /// <example>
    /// <code>
    /// GameLabel.Event("combat.kill", "weapon", "shotgun", "headshot", true);
    /// GameLabel.Begin("boss.fight");
    /// GameLabel.Set("player.health", health);
    /// GameLabel.Highlight("triple kill", 0.9f);
    /// </code>
    /// </example>
    /// <para>
    /// <b>Player builds.</b> Every method here carries
    /// <see cref="ConditionalAttribute"/>, so when your code is compiled without <c>UNITY_EDITOR</c> the C#
    /// compiler deletes the call <i>and the expressions you passed as arguments</i> from the call site. There is
    /// no branch left to predict and no string left to build, so labelling costs a shipped build exactly nothing
    /// and needs no <c>#if</c> blocks of your own.
    /// </para>
    /// <para>
    /// <b>In the Editor.</b> Outside a recording each call is a single static <c>bool</c> read and a return. During
    /// a recording it writes one struct into a pre-allocated lock-free buffer: no allocation, no file I/O on the
    /// game thread, and safe to call from jobs and background threads. If the buffer ever fills up labels are
    /// dropped rather than stalling the game, and the count of dropped labels is written into the label file.
    /// </para>
    /// <para>
    /// Define <c>LABELED_RECORDER_FORCE</c> to keep these calls alive in a player build. That only fills the
    /// buffer; you then have to drain <see cref="LabelSink"/> yourself, since the writer is Editor-only.
    /// </para>
    /// </summary>
    public static class GameLabel
    {
        const string k_Editor = "UNITY_EDITOR";
        const string k_Forced = "LABELED_RECORDER_FORCE";

        /// <summary>
        /// True while a recording is actively collecting labels. Always false in a player build.
        /// <para>
        /// You never need this to make labelling cheap; use it only to skip genuinely expensive work that you
        /// would otherwise do just to build a payload:
        /// <code>
        /// if (GameLabel.IsRecording)
        ///     GameLabel.Event("wave.cleared", "enemies", CountEnemiesTheSlowWay());
        /// </code>
        /// </para>
        /// </summary>
        public static bool IsRecording
        {
#if UNITY_EDITOR || LABELED_RECORDER_FORCE
            get { return LabelSink.Active; }
#else
            get { return false; }
#endif
        }

        // ---------------------------------------------------------------------------------------------------
        // Point events
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Marks a moment in time.
        /// <para>
        /// Use a dotted name: everything before the first dot becomes the event's category, which is what lets an
        /// editing agent reason about groups of events ("all <c>combat.*</c> events") without knowing your game.
        /// </para>
        /// </summary>
        /// <param name="name">The dotted event name, for example <c>combat.kill</c> or <c>ui.menu.opened</c>.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Event(string name)
        {
            Push(LabelOp.Event, name, 0f, 0, null, default, null, default, null, default, null, default);
        }

        /// <summary>Marks a moment in time, with one payload field.</summary>
        /// <param name="name">The dotted event name.</param>
        /// <param name="key">The payload field name.</param>
        /// <param name="value">The payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Event(string name, string key, LabelValue value)
        {
            Push(LabelOp.Event, name, 0f, 1, key, value, null, default, null, default, null, default);
        }

        /// <summary>Marks a moment in time, with two payload fields.</summary>
        /// <param name="name">The dotted event name.</param>
        /// <param name="key0">The first payload field name.</param>
        /// <param name="value0">The first payload value.</param>
        /// <param name="key1">The second payload field name.</param>
        /// <param name="value1">The second payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Event(string name, string key0, LabelValue value0, string key1, LabelValue value1)
        {
            Push(LabelOp.Event, name, 0f, 2, key0, value0, key1, value1, null, default, null, default);
        }

        /// <summary>Marks a moment in time, with three payload fields.</summary>
        /// <param name="name">The dotted event name.</param>
        /// <param name="key0">The first payload field name.</param>
        /// <param name="value0">The first payload value.</param>
        /// <param name="key1">The second payload field name.</param>
        /// <param name="value1">The second payload value.</param>
        /// <param name="key2">The third payload field name.</param>
        /// <param name="value2">The third payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Event(string name, string key0, LabelValue value0, string key1, LabelValue value1,
            string key2, LabelValue value2)
        {
            Push(LabelOp.Event, name, 0f, 3, key0, value0, key1, value1, key2, value2, null, default);
        }

        /// <summary>Marks a moment in time, with four payload fields.</summary>
        /// <param name="name">The dotted event name.</param>
        /// <param name="key0">The first payload field name.</param>
        /// <param name="value0">The first payload value.</param>
        /// <param name="key1">The second payload field name.</param>
        /// <param name="value1">The second payload value.</param>
        /// <param name="key2">The third payload field name.</param>
        /// <param name="value2">The third payload value.</param>
        /// <param name="key3">The fourth payload field name.</param>
        /// <param name="value3">The fourth payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Event(string name, string key0, LabelValue value0, string key1, LabelValue value1,
            string key2, LabelValue value2, string key3, LabelValue value3)
        {
            Push(LabelOp.Event, name, 0f, 4, key0, value0, key1, value1, key2, value2, key3, value3);
        }

        // ---------------------------------------------------------------------------------------------------
        // Spans
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Opens a span: something that lasts, such as a boss fight, a cutscene, a chase or a power-up. Close it
        /// with <see cref="End(string)"/>. The writer pairs them by name, supports nesting, and records the
        /// duration so an editing agent can treat the span as a candidate clip range.
        /// <para>A span still open when the recording stops is written out with <c>"open": true</c>.</para>
        /// </summary>
        /// <param name="name">The dotted span name, for example <c>boss.fight</c>.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Begin(string name)
        {
            Push(LabelOp.SpanBegin, name, 0f, 0, null, default, null, default, null, default, null, default);
        }

        /// <summary>Opens a span, with one payload field.</summary>
        /// <param name="name">The dotted span name.</param>
        /// <param name="key">The payload field name.</param>
        /// <param name="value">The payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Begin(string name, string key, LabelValue value)
        {
            Push(LabelOp.SpanBegin, name, 0f, 1, key, value, null, default, null, default, null, default);
        }

        /// <summary>Opens a span, with two payload fields.</summary>
        /// <param name="name">The dotted span name.</param>
        /// <param name="key0">The first payload field name.</param>
        /// <param name="value0">The first payload value.</param>
        /// <param name="key1">The second payload field name.</param>
        /// <param name="value1">The second payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Begin(string name, string key0, LabelValue value0, string key1, LabelValue value1)
        {
            Push(LabelOp.SpanBegin, name, 0f, 2, key0, value0, key1, value1, null, default, null, default);
        }

        /// <summary>
        /// Closes the innermost open span with this name.
        /// </summary>
        /// <param name="name">The span name passed to <see cref="Begin(string)"/>.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void End(string name)
        {
            Push(LabelOp.SpanEnd, name, 0f, 0, null, default, null, default, null, default, null, default);
        }

        /// <summary>
        /// Closes the innermost open span with this name, attaching an outcome. Fields set here are merged into
        /// the span, so this is where results belong: <c>GameLabel.End("boss.fight", "won", true)</c>.
        /// </summary>
        /// <param name="name">The span name passed to <see cref="Begin(string)"/>.</param>
        /// <param name="key">The payload field name.</param>
        /// <param name="value">The payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void End(string name, string key, LabelValue value)
        {
            Push(LabelOp.SpanEnd, name, 0f, 1, key, value, null, default, null, default, null, default);
        }

        /// <summary>Closes the innermost open span with this name, attaching two outcome fields.</summary>
        /// <param name="name">The span name passed to <see cref="Begin(string)"/>.</param>
        /// <param name="key0">The first payload field name.</param>
        /// <param name="value0">The first payload value.</param>
        /// <param name="key1">The second payload field name.</param>
        /// <param name="value1">The second payload value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void End(string name, string key0, LabelValue value0, string key1, LabelValue value1)
        {
            Push(LabelOp.SpanEnd, name, 0f, 2, key0, value0, key1, value1, null, default, null, default);
        }

        /// <summary>
        /// Opens a span that closes itself at the end of a <c>using</c> block.
        /// <example>
        /// <code>
        /// using (GameLabel.Span("cutscene.intro"))
        /// {
        ///     yield return PlayIntro();
        /// }
        /// </code>
        /// </example>
        /// <para>
        /// This one cannot be compiled away like the others, because it returns a value. In a player build it
        /// reduces to constructing an empty struct and calling an empty <c>Dispose</c>. Prefer
        /// <see cref="Begin(string)"/>/<see cref="End(string)"/> on paths where that matters.
        /// </para>
        /// </summary>
        /// <param name="name">The dotted span name.</param>
        /// <returns>A disposable handle that closes the span.</returns>
        public static LabelSpan Span(string name)
        {
#if UNITY_EDITOR || LABELED_RECORDER_FORCE
            Begin(name);
            return new LabelSpan(name);
#else
            return default;
#endif
        }

        // ---------------------------------------------------------------------------------------------------
        // Signals: named game data whose changes are worth a timestamp
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reports the current value of a named piece of game data, and writes a timestamped sample only when the
        /// value actually changed since the last call. Safe and intended to be called every frame.
        /// <example>
        /// <code>
        /// void Update()
        /// {
        ///     GameLabel.Set("player.health", health);     // one sample per real change
        ///     GameLabel.Set("player.weapon", weaponName);
        ///     GameLabel.Set("game.paused", isPaused);
        /// }
        /// </code>
        /// </example>
        /// <para>
        /// The label file stores these as sparse step functions under <c>signals</c>, so an agent can ask what
        /// the value was at any timecode, or find the moments where it crossed a threshold.
        /// </para>
        /// </summary>
        /// <param name="name">The dotted channel name, for example <c>player.health</c>.</param>
        /// <param name="value">The current value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Set(string name, LabelValue value)
        {
#if UNITY_EDITOR || LABELED_RECORDER_FORCE
            if (!LabelSink.Active || string.IsNullOrEmpty(name))
                return;
            if (!LabelSink.SignalChanged(name, value, 0d))
                return;
            Push(LabelOp.Signal, name, 0f, 1, null, value, null, default, null, default, null, default);
#endif
        }

        /// <summary>
        /// Reports a continuously varying number, writing a sample only once it has moved by at least
        /// <paramref name="epsilon"/>. Use this for things that change every frame but only matter in steps, so
        /// the label file stays small and readable.
        /// <example>
        /// <code>GameLabel.Set("player.speed", rb.velocity.magnitude, 0.5f);</code>
        /// </example>
        /// </summary>
        /// <param name="name">The dotted channel name.</param>
        /// <param name="value">The current value.</param>
        /// <param name="epsilon">The smallest change worth recording.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Set(string name, float value, float epsilon)
        {
#if UNITY_EDITOR || LABELED_RECORDER_FORCE
            if (!LabelSink.Active || string.IsNullOrEmpty(name))
                return;
            LabelValue wrapped = value;
            if (!LabelSink.SignalChanged(name, wrapped, epsilon))
                return;
            Push(LabelOp.Signal, name, 0f, 1, null, wrapped, null, default, null, default, null, default);
#endif
        }

        /// <summary>
        /// Writes a signal sample unconditionally, even when the value has not changed. Use this when the
        /// sampling rate itself is the information, for example a per-second score snapshot.
        /// </summary>
        /// <param name="name">The dotted channel name.</param>
        /// <param name="value">The value to record.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Sample(string name, LabelValue value)
        {
            Push(LabelOp.Signal, name, 0f, 1, null, value, null, default, null, default, null, default);
        }

        // ---------------------------------------------------------------------------------------------------
        // Markers, chapters and session facts
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// Flags a moment as editorially interesting, with an importance score. Markers are what an editing agent
        /// ranks first when it looks for shots to cut, so use them for the moments you would point at yourself.
        /// </summary>
        /// <param name="name">The dotted marker name, for example <c>highlight.combo</c>.</param>
        /// <param name="score">Importance in the 0..1 range. Higher sorts earlier.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Mark(string name, float score = 1f)
        {
            Push(LabelOp.Marker, name, score, 0, null, default, null, default, null, default, null, default);
        }

        /// <summary>
        /// Flags a moment as editorially interesting, with a human-readable note that an agent can use as a
        /// caption or title card.
        /// </summary>
        /// <param name="note">A short description of what happened, for example "triple kill".</param>
        /// <param name="score">Importance in the 0..1 range.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Highlight(string note, float score = 1f)
        {
            Push(LabelOp.Marker, "highlight", score, 1, "note", note, null, default, null, default, null, default);
        }

        /// <summary>
        /// Starts a named chapter. Chapters partition the whole recording end to end, so they give an agent the
        /// coarse structure of the take: menu, tutorial, level 1, boss, credits.
        /// </summary>
        /// <param name="name">The chapter name.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Chapter(string name)
        {
            Push(LabelOp.Chapter, name, 0f, 0, null, default, null, default, null, default, null, default);
        }

        /// <summary>
        /// Records a fact about the whole take rather than a moment in it: build number, difficulty, character,
        /// seed, test condition. These land in <c>session.meta</c> in the label file.
        /// </summary>
        /// <param name="key">The fact name.</param>
        /// <param name="value">The fact value.</param>
        [Conditional(k_Editor), Conditional(k_Forced)]
        public static void Meta(string key, LabelValue value)
        {
            Push(LabelOp.Meta, key, 0f, 1, null, value, null, default, null, default, null, default);
        }

        // ---------------------------------------------------------------------------------------------------

#if UNITY_EDITOR || LABELED_RECORDER_FORCE
        static void Push(LabelOp op, string name, float score, byte fieldCount,
            string key0, in LabelValue value0, string key1, in LabelValue value1,
            string key2, in LabelValue value2, string key3, in LabelValue value3)
        {
            if (!LabelSink.Active || string.IsNullOrEmpty(name))
                return;

            var record = new LabelRecord
            {
                Op = op,
                Name = name,
                Time = LabelSink.Time,
                Frame = LabelSink.Frame,
                Score = score,
                FieldCount = fieldCount,
                K0 = key0, V0 = value0,
                K1 = key1, V1 = value1,
                K2 = key2, V2 = value2,
                K3 = key3, V3 = value3
            };

            LabelSink.Push(record);
        }
#else
        static void Push(LabelOp op, string name, float score, byte fieldCount,
            string key0, in LabelValue value0, string key1, in LabelValue value1,
            string key2, in LabelValue value2, string key3, in LabelValue value3)
        {
        }
#endif
    }
}
