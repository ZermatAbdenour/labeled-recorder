using System;
using System.Globalization;
using System.Text;

namespace LabeledRecorder.Editor
{
    /// <summary>
    /// A small, dependency-free JSON writer. It exists because the label file needs stable number formatting and
    /// optional indentation, and <c>JsonUtility</c> cannot express the nested, heterogeneous shape of a label
    /// track.
    /// </summary>
    public sealed class LabelJsonWriter
    {
        readonly StringBuilder m_Builder;
        readonly bool m_Pretty;
        int m_Depth;
        bool m_NeedsComma;

        /// <summary>
        /// Creates a writer.
        /// </summary>
        /// <param name="pretty">True to indent with two spaces and break lines.</param>
        /// <param name="capacity">The initial character capacity of the backing buffer.</param>
        public LabelJsonWriter(bool pretty, int capacity = 1 << 16)
        {
            m_Builder = new StringBuilder(capacity);
            m_Pretty = pretty;
        }

        /// <summary>The JSON produced so far.</summary>
        public override string ToString()
        {
            return m_Builder.ToString();
        }

        /// <summary>The number of characters written so far.</summary>
        public int Length => m_Builder.Length;

        /// <summary>Opens an object. Pass a name when inside another object.</summary>
        /// <param name="name">The property name, or null when inside an array.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter BeginObject(string name = null)
        {
            Prefix(name);
            m_Builder.Append('{');
            m_Depth++;
            m_NeedsComma = false;
            return this;
        }

        /// <summary>Closes an object.</summary>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter EndObject()
        {
            m_Depth--;
            if (m_NeedsComma)
                NewLine();
            m_Builder.Append('}');
            m_NeedsComma = true;
            return this;
        }

        /// <summary>Opens an array. Pass a name when inside an object.</summary>
        /// <param name="name">The property name, or null when inside an array.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter BeginArray(string name = null)
        {
            Prefix(name);
            m_Builder.Append('[');
            m_Depth++;
            m_NeedsComma = false;
            return this;
        }

        /// <summary>Closes an array.</summary>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter EndArray()
        {
            m_Depth--;
            if (m_NeedsComma)
                NewLine();
            m_Builder.Append(']');
            m_NeedsComma = true;
            return this;
        }

        /// <summary>Writes a string property, skipping it entirely when the value is null.</summary>
        /// <param name="name">The property name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Str(string name, string value)
        {
            if (value == null)
                return this;
            Prefix(name);
            Escape(value);
            return this;
        }

        /// <summary>Writes a number property, rounded to six decimal places.</summary>
        /// <param name="name">The property name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Num(string name, double value)
        {
            Prefix(name);
            AppendNumber(value);
            return this;
        }

        /// <summary>Writes an integer property.</summary>
        /// <param name="name">The property name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Int(string name, long value)
        {
            Prefix(name);
            m_Builder.Append(value.ToString(CultureInfo.InvariantCulture));
            m_NeedsComma = true;
            return this;
        }

        /// <summary>Writes a boolean property.</summary>
        /// <param name="name">The property name.</param>
        /// <param name="value">The value.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Bool(string name, bool value)
        {
            Prefix(name);
            m_Builder.Append(value ? "true" : "false");
            m_NeedsComma = true;
            return this;
        }

        /// <summary>Writes a null property.</summary>
        /// <param name="name">The property name.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Null(string name)
        {
            Prefix(name);
            m_Builder.Append("null");
            m_NeedsComma = true;
            return this;
        }

        /// <summary>
        /// Writes a <see cref="LabelValue"/> as the JSON type that matches its payload: a number, a boolean, a
        /// string, or an array of numbers for a vector.
        /// </summary>
        /// <param name="name">The property name, or null when inside an array.</param>
        /// <param name="value">The value to write.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Value(string name, in LabelValue value)
        {
            switch (value.Kind)
            {
                case LabelValueKind.Number:
                    return Num(name, value.Number);
                case LabelValueKind.Bool:
                    return Bool(name, value.Number != 0d);
                case LabelValueKind.Text:
                    return Str(name, value.Text);
                case LabelValueKind.Vector:
                    Prefix(name);
                    m_Builder.Append('[');
                    AppendNumber(value.X);
                    m_Builder.Append(',');
                    AppendNumber(value.Y);
                    if (value.Width >= 3)
                    {
                        m_Builder.Append(',');
                        AppendNumber(value.Z);
                    }
                    if (value.Width >= 4)
                    {
                        m_Builder.Append(',');
                        AppendNumber(value.W);
                    }
                    m_Builder.Append(']');
                    m_NeedsComma = true;
                    return this;
                default:
                    return Null(name);
            }
        }

        /// <summary>
        /// Writes a raw, already-encoded JSON fragment as a property value. Used for pre-serialized blocks.
        /// </summary>
        /// <param name="name">The property name, or null when inside an array.</param>
        /// <param name="json">A valid JSON fragment.</param>
        /// <returns>This writer, for chaining.</returns>
        public LabelJsonWriter Raw(string name, string json)
        {
            Prefix(name);
            m_Builder.Append(json);
            m_NeedsComma = true;
            return this;
        }

        void AppendNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                m_Builder.Append("null");
                m_NeedsComma = true;
                return;
            }

            var rounded = Math.Round(value, 6);
            m_Builder.Append(rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15
                ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
                : rounded.ToString("0.######", CultureInfo.InvariantCulture));
            m_NeedsComma = true;
        }

        void Prefix(string name)
        {
            if (m_NeedsComma)
                m_Builder.Append(',');

            NewLine();

            if (name != null)
            {
                Escape(name);
                m_Builder.Append(m_Pretty ? ": " : ":");
            }

            m_NeedsComma = false;
        }

        void NewLine()
        {
            if (!m_Pretty)
                return;

            if (m_Builder.Length == 0)
                return;

            m_Builder.Append('\n');
            for (var i = 0; i < m_Depth; i++)
                m_Builder.Append("  ");
        }

        void Escape(string value)
        {
            m_Builder.Append('"');

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                switch (c)
                {
                    case '"': m_Builder.Append("\\\""); break;
                    case '\\': m_Builder.Append("\\\\"); break;
                    case '\n': m_Builder.Append("\\n"); break;
                    case '\r': m_Builder.Append("\\r"); break;
                    case '\t': m_Builder.Append("\\t"); break;
                    case '\b': m_Builder.Append("\\b"); break;
                    case '\f': m_Builder.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
                            m_Builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            m_Builder.Append(c);
                        break;
                }
            }

            m_Builder.Append('"');
            m_NeedsComma = true;
        }
    }
}
