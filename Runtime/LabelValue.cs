using System;
using System.Collections.Generic;
using UnityEngine;

namespace LabeledRecorder
{
    /// <summary>
    /// The storage class of a <see cref="LabelValue"/>.
    /// </summary>
    public enum LabelValueKind : byte
    {
        /// <summary>No value.</summary>
        None = 0,
        /// <summary>A double-precision number.</summary>
        Number = 1,
        /// <summary>A boolean.</summary>
        Bool = 2,
        /// <summary>A string.</summary>
        Text = 3,
        /// <summary>A 2, 3 or 4 component vector (component count is stored in <see cref="LabelValue.Width"/>).</summary>
        Vector = 4
    }

    /// <summary>
    /// A single label payload value. This is a struct with implicit conversions from the common Unity and
    /// C# scalar types, so payloads can be passed inline without boxing or allocating:
    /// <code>GameLabel.Event("player.hit", "damage", 24f, "crit", true);</code>
    /// </summary>
    public struct LabelValue
    {
        /// <summary>How this value is stored.</summary>
        public LabelValueKind Kind;

        /// <summary>The numeric payload, when <see cref="Kind"/> is <see cref="LabelValueKind.Number"/> or <see cref="LabelValueKind.Bool"/>.</summary>
        public double Number;

        /// <summary>The string payload, when <see cref="Kind"/> is <see cref="LabelValueKind.Text"/>.</summary>
        public string Text;

        /// <summary>The vector payload, when <see cref="Kind"/> is <see cref="LabelValueKind.Vector"/>.</summary>
        public float X, Y, Z, W;

        /// <summary>The number of meaningful vector components (2, 3 or 4).</summary>
        public byte Width;

        /// <summary>True when this value carries no payload.</summary>
        public bool IsNone => Kind == LabelValueKind.None;

        /// <summary>Wraps a number.</summary>
        public static implicit operator LabelValue(double v) => new LabelValue { Kind = LabelValueKind.Number, Number = v };
        /// <summary>Wraps a number.</summary>
        public static implicit operator LabelValue(float v) => new LabelValue { Kind = LabelValueKind.Number, Number = v };
        /// <summary>Wraps a number.</summary>
        public static implicit operator LabelValue(int v) => new LabelValue { Kind = LabelValueKind.Number, Number = v };
        /// <summary>Wraps a number.</summary>
        public static implicit operator LabelValue(long v) => new LabelValue { Kind = LabelValueKind.Number, Number = v };
        /// <summary>Wraps a boolean.</summary>
        public static implicit operator LabelValue(bool v) => new LabelValue { Kind = LabelValueKind.Bool, Number = v ? 1d : 0d };
        /// <summary>Wraps a string. A null string produces a <see cref="LabelValueKind.None"/> value.</summary>
        public static implicit operator LabelValue(string v) => v == null ? default : new LabelValue { Kind = LabelValueKind.Text, Text = v };
        /// <summary>Wraps a 2D vector.</summary>
        public static implicit operator LabelValue(Vector2 v) => new LabelValue { Kind = LabelValueKind.Vector, Width = 2, X = v.x, Y = v.y };
        /// <summary>Wraps a 3D vector.</summary>
        public static implicit operator LabelValue(Vector3 v) => new LabelValue { Kind = LabelValueKind.Vector, Width = 3, X = v.x, Y = v.y, Z = v.z };
        /// <summary>Wraps a colour as an RGBA vector.</summary>
        public static implicit operator LabelValue(Color v) => new LabelValue { Kind = LabelValueKind.Vector, Width = 4, X = v.r, Y = v.g, Z = v.b, W = v.a };
        /// <summary>Wraps a quaternion's euler angles as a 3D vector.</summary>
        public static implicit operator LabelValue(Quaternion v) => (Vector3)v.eulerAngles;

        /// <summary>
        /// Wraps an enum value as text, without the allocation that <c>Enum.ToString()</c> performs.
        /// Names are resolved once per enum type and cached.
        /// </summary>
        /// <typeparam name="T">The enum type.</typeparam>
        /// <param name="value">The enum value to wrap.</param>
        /// <returns>A text value holding the enum member name, or its numeric value if the name is unknown.</returns>
        public static LabelValue Of<T>(T value) where T : struct, Enum
        {
            return new LabelValue { Kind = LabelValueKind.Text, Text = EnumNames<T>.Get(value) };
        }

        /// <summary>
        /// Wraps the name of a Unity object, or no value when the object is null or destroyed.
        /// </summary>
        /// <param name="value">The object whose name to record.</param>
        /// <returns>A text value holding the object's name.</returns>
        public static LabelValue Of(UnityEngine.Object value)
        {
            return value == null ? default : new LabelValue { Kind = LabelValueKind.Text, Text = value.name };
        }

        /// <summary>
        /// Compares two values for equality, with an optional tolerance applied to numeric and vector payloads.
        /// </summary>
        /// <param name="other">The value to compare against.</param>
        /// <param name="epsilon">The tolerance for numeric comparison. Pass 0 for exact comparison.</param>
        /// <returns>True when the two values are considered equal.</returns>
        public bool Matches(in LabelValue other, double epsilon = 0d)
        {
            if (Kind != other.Kind)
                return false;

            switch (Kind)
            {
                case LabelValueKind.None:
                    return true;
                case LabelValueKind.Bool:
                    return Number == other.Number;
                case LabelValueKind.Text:
                    return string.Equals(Text, other.Text, StringComparison.Ordinal);
                case LabelValueKind.Number:
                    return epsilon <= 0d ? Number == other.Number : Math.Abs(Number - other.Number) < epsilon;
                case LabelValueKind.Vector:
                    if (Width != other.Width)
                        return false;
                    if (epsilon <= 0d)
                        return X == other.X && Y == other.Y && Z == other.Z && W == other.W;
                    return Math.Abs(X - other.X) < epsilon
                        && Math.Abs(Y - other.Y) < epsilon
                        && Math.Abs(Z - other.Z) < epsilon
                        && Math.Abs(W - other.W) < epsilon;
                default:
                    return false;
            }
        }

        static class EnumNames<T> where T : struct, Enum
        {
            static readonly Dictionary<T, string> s_Names = Build();

            static Dictionary<T, string> Build()
            {
                var names = Enum.GetNames(typeof(T));
                var values = (T[])Enum.GetValues(typeof(T));
                var map = new Dictionary<T, string>(values.Length);
                for (var i = 0; i < values.Length; i++)
                    map[values[i]] = names[i];
                return map;
            }

            internal static string Get(T value)
            {
                return s_Names.TryGetValue(value, out var name) ? name : Convert.ToInt64(value).ToString();
            }
        }
    }
}
