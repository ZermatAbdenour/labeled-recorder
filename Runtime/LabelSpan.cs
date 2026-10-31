using System;

namespace LabeledRecorder
{
    /// <summary>
    /// The handle returned by <see cref="GameLabel.Span"/>. Disposing it closes the span.
    /// <para>
    /// This is a struct, so a <c>using</c> block over it neither allocates nor boxes. In a player build it holds
    /// no name and <see cref="Dispose"/> does nothing.
    /// </para>
    /// </summary>
    public struct LabelSpan : IDisposable
    {
        readonly string m_Name;

        /// <summary>
        /// Creates a handle for an already-opened span.
        /// </summary>
        /// <param name="name">The span name to close on dispose.</param>
        public LabelSpan(string name)
        {
            m_Name = name;
        }

        /// <summary>
        /// Closes the span.
        /// </summary>
        public void Dispose()
        {
            // GameLabel.End is itself conditional, so this reduces to the null test in a player build.
            if (m_Name != null)
                GameLabel.End(m_Name);
        }
    }
}
