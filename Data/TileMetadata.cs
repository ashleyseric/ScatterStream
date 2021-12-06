/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

namespace AshleySeric.ScatterStream
{
    public struct TileMetadata : System.IEquatable<TileMetadata>
    {
        public readonly int streamGuid;
        public readonly TileCoords coords;

        public TileMetadata(int streamGuid, TileCoords coords)
        {
            this.streamGuid = streamGuid;
            this.coords = coords;
        }

        public override bool Equals(object obj)
        {
            return obj is TileMetadata info &&
                   streamGuid == info.streamGuid &&
                   coords.Equals(info.coords);
        }

        public bool Equals(TileMetadata other)
        {
            return streamGuid == other.streamGuid &&
                   coords.Equals(other.coords);
        }

        public override int GetHashCode()
        {
            int hashCode = -1278929389;
            hashCode = hashCode * -1521134295 + streamGuid.GetHashCode();
            hashCode = hashCode * -1521134295 + coords.GetHashCode();
            return hashCode;
        }
    }
}