/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public struct ShaderConstants
    {
        public readonly static Color BILLBOARD_BACKGROUND_COLOR = new Color(0f, 0f, 0f, 1f);
        public const string BILLBOARD_SHADER_NAME = "ScatterStream/Billboard";
        public const string BILLBOARD_TEXTURE_PROP = "_TEXTURE";
    }
}
