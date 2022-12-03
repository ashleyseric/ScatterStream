/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct ShaderConstants
    {
        public readonly static Color BILLBOARD_BACKGROUND_COLOR = new Color(0f, 0f, 0f, 1f);
        public const string BILLBOARD_SHADER_NAME = "ScatterStream/Billboard";
        public const string BILLBOARD_TEXTURE = "_TEXTURE";
        public const string INSTANCE_COLOUR = "_INSTANCE_COLOUR";
        public const string WIND_SPEED = "_SCATTER_STREAM_WIND_SPEED";
        public const string WIND_DIRECTION = "_SCATTER_STREAM_WIND_DIRECTION";
        public const string WIND_TURBULENCE = "_SCATTER_STREAM_WIND_TURBULENCE";
        public const string WIND_PULSE_FREQUENCY = "_SCATTER_STREAM_WIND_PULSE_FREQUENCY";
        public const string WIND_PULSE_MAGNITUDE = "_SCATTER_STREAM_WIND_PULSE_MAGNITUDE";
    }
}
