/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using System;
using Unity.Mathematics;

namespace AshleySeric.ScatterStream
{
    public struct TileCoords : IEquatable<TileCoords>
    {
        public int x;
        public int y;

        public TileCoords(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public TileCoords(int2 xy)
        {
            this.x = xy.x;
            this.y = xy.y;
        }

        public static bool operator ==(TileCoords lhs, TileCoords rhs) => lhs.Equals(rhs);
        public static bool operator !=(TileCoords lhs, TileCoords rhs) => !lhs.Equals(rhs);

        public bool Equals(TileCoords other)
        {
            return x == other.x && y == other.y;
        }

        public override int GetHashCode()
        {
            int hashCode = 1502939027;
            hashCode = hashCode * -1521134295 + x.GetHashCode();
            hashCode = hashCode * -1521134295 + y.GetHashCode();
            return hashCode;
        }

        public override bool Equals(object obj)
        {
            return base.Equals(obj);
        }

        public override string ToString()
        {
            return $"({x}, {y})";
        }
    }
}