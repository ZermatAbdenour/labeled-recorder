using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace LabeledRecorder
{
    /// <summary>
    /// The lock-free buffer that carries labels from the game thread (or any job thread) to the Editor-side
    /// writer, plus the frame clock that stamps them.
    /// <para>
    /// This is internal plumbing. Game code should call <see cref="GameLabel"/> instead, because those calls are
    /// stripped from player builds. Nothing here allocates or does work while <see cref="Active"/> is false, and
    /// the Editor is the only thing that ever sets it to true.
    /// </para>
    /// </summary>
    public static class LabelSink
    {
        /// <summary>
        /// True while an Editor recording session is collecting labels. Every <see cref="GameLabel"/> call tests
        /// this first, so the cost of a label call outside a recording is a single static field read.
        /// </summary>
        public static volatile bool Active;

        /// <summary>
        /// The game time, in seconds, of the frame currently being simulated. Published once per frame by the
        /// Editor driver before any game script runs, so it is safe to read from job threads (where
        /// <c>UnityEngine.Time</c> is not).
        /// </summary>
        public static double Time;

        /// <summary>
        /// The engine frame count of the frame currently being simulated. Published alongside <see cref="Time"/>.
        /// </summary>
        public static int Frame;

        static LabelRecord[] s_Buffer;
        static long[] s_Sequence;
        static int s_Mask;
        static long s_Write;
        static long s_Read;
        static int s_Dropped;
        static int s_Pushed;

        static readonly ConcurrentDictionary<string, LabelValue> s_LastSignal =
            new ConcurrentDictionary<string, LabelValue>();

        /// <summary>The number of labels discarded because the buffer was full since the last <see cref="Begin"/>.</summary>
        public static int DroppedCount => Volatile.Read(ref s_Dropped);

        /// <summary>The number of labels accepted since the last <see cref="Begin"/>.</summary>
        public static int PushedCount => Volatile.Read(ref s_Pushed);

        /// <summary>
        /// Allocates the buffer and opens the sink. Called by the Editor when a recording starts.
        /// </summary>
        /// <param name="capacity">The buffer size in records. Rounded up to a power of two, minimum 256.</param>
        public static void Begin(int capacity)
        {
            var size = 256;
            while (size < capacity && size < 1 << 22)
                size <<= 1;

            if (s_Buffer == null || s_Buffer.Length != size)
            {
                s_Buffer = new LabelRecord[size];
                s_Sequence = new long[size];
            }
            else
            {
                Array.Clear(s_Buffer, 0, size);
                Array.Clear(s_Sequence, 0, size);
            }

            s_Mask = size - 1;
            Volatile.Write(ref s_Write, 0L);
            Volatile.Write(ref s_Read, 0L);
            Volatile.Write(ref s_Dropped, 0);
            Volatile.Write(ref s_Pushed, 0);
            Active = true;
        }

        /// <summary>
        /// Closes the sink. Pending records stay readable so the Editor can drain them one last time.
        /// </summary>
        public static void End()
        {
            Active = false;
        }

        /// <summary>
        /// Releases the buffer. Call after the final <see cref="Drain"/>.
        /// </summary>
        public static void Release()
        {
            Active = false;
            s_Buffer = null;
            s_Sequence = null;
            s_Mask = 0;
        }

        /// <summary>
        /// Forgets the remembered value of every signal, so the next <see cref="GameLabel.Set"/> for each channel
        /// is treated as a change. Called between recordings.
        /// </summary>
        public static void ForgetSignals()
        {
            s_LastSignal.Clear();
        }

        /// <summary>
        /// Copies the last known value of every signal channel. The Editor uses this to write a baseline sample at
        /// the start of a recording, so a label file always states game state at frame 0.
        /// </summary>
        /// <param name="into">The list to append to.</param>
        public static void SnapshotSignals(List<KeyValuePair<string, LabelValue>> into)
        {
            foreach (var pair in s_LastSignal)
                into.Add(pair);
        }

        /// <summary>
        /// Records a signal value and reports whether it differs from the last value seen on that channel.
        /// </summary>
        /// <param name="name">The channel name.</param>
        /// <param name="value">The new value.</param>
        /// <param name="epsilon">The tolerance below which a numeric change is ignored.</param>
        /// <returns>True when the value changed and should be pushed.</returns>
        public static bool SignalChanged(string name, in LabelValue value, double epsilon)
        {
            if (s_LastSignal.TryGetValue(name, out var previous) && previous.Matches(value, epsilon))
                return false;

            s_LastSignal[name] = value;
            return true;
        }

        /// <summary>
        /// Appends a record. Safe to call from any thread. Returns without blocking if the buffer is full,
        /// counting the loss in <see cref="DroppedCount"/> rather than stalling the game.
        /// </summary>
        /// <param name="record">The record to append.</param>
        public static void Push(in LabelRecord record)
        {
            var buffer = s_Buffer;
            var sequence = s_Sequence;
            if (buffer == null || sequence == null)
                return;

            var capacity = buffer.Length;

            while (true)
            {
                var write = Volatile.Read(ref s_Write);

                if (write - Volatile.Read(ref s_Read) >= capacity)
                {
                    Interlocked.Increment(ref s_Dropped);
                    return;
                }

                if (Interlocked.CompareExchange(ref s_Write, write + 1, write) != write)
                    continue;

                var slot = (int)(write & s_Mask);
                buffer[slot] = record;
                Volatile.Write(ref sequence[slot], write + 1);
                Interlocked.Increment(ref s_Pushed);
                return;
            }
        }

        /// <summary>
        /// Moves every fully published record into <paramref name="into"/>, in push order. Single consumer only;
        /// the Editor driver is the only caller.
        /// </summary>
        /// <param name="into">The list to append to.</param>
        /// <returns>The number of records moved.</returns>
        public static int Drain(List<LabelRecord> into)
        {
            var buffer = s_Buffer;
            var sequence = s_Sequence;
            if (buffer == null || sequence == null)
                return 0;

            var read = Volatile.Read(ref s_Read);
            var write = Volatile.Read(ref s_Write);
            var moved = 0;

            while (read < write)
            {
                var slot = (int)(read & s_Mask);

                // A producer reserved this slot but has not finished writing it yet. Stop here and pick it up on
                // the next drain, so records are never handed over out of order.
                if (Volatile.Read(ref sequence[slot]) != read + 1)
                    break;

                into.Add(buffer[slot]);
                buffer[slot] = default; // drop the string references so they can be collected
                read++;
                moved++;
            }

            Volatile.Write(ref s_Read, read);
            return moved;
        }
    }
}
