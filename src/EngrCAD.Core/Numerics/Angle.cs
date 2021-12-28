using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core.Numerics
{
    public class Angle
    {
        public static Angle Zero => new Angle(0);
        public static readonly Angle PI = FromRadians(MathF.PI);
        public static readonly Angle TwoPI = FromRadians(MathF.PI * 2);

        public float Radians { get; }
        public float Degrees => Radians * 180.0f / MathF.PI;
        public float Gradians => Radians * 50f / MathF.PI;

        private Angle(float radians)
        {
            Radians = radians;
        }
        public override bool Equals(object obj)
        {
            if (!(obj is Angle))
                return false;
            return Radians.NearlyEquals(((Angle)obj).Radians);
        }
        public bool Equals(Angle other)
        {
            return Radians.Equals(other.Radians);
        }
        public override int GetHashCode()
        {
            return Radians.GetHashCode();
        }

        public static Angle FromRadians(float radians) => new Angle(radians);
        public static Angle FromDegrees(float degrees) => new Angle(degrees * MathF.PI / 180.0f);
        public static Angle FromGradians(float gradians) => new Angle(gradians * MathF.PI / 50f);
        public static Angle operator +(Angle a1, Angle a2) => new Angle(a1.Radians + a2.Radians);
        public static Angle operator -(Angle a1, Angle a2) => new Angle(a1.Radians - a2.Radians);
        public static Angle operator *(Angle a, float d) => new Angle(a.Radians * d);
        public static Angle operator /(Angle a, float d) => new Angle(a.Radians / d);
        public static implicit operator float(Angle angle) => angle.Radians;
        public static implicit operator Angle(float radians) => FromRadians(radians);
        public override string ToString() => "Angle<" + Degrees + "\x00B0>";
    }
}
