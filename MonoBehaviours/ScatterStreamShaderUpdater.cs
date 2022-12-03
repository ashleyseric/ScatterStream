using UnityEngine;

namespace AshleySeric.ScatterStream
{
    public class ScatterStreamShaderUpdater : MonoBehaviour
    {
        [SerializeField] private WindZone windZone;

        private void Update()
        {
            if (windZone != null)
            {
                Shader.SetGlobalVector(ShaderConstants.WIND_DIRECTION, windZone.transform.forward);
                Shader.SetGlobalFloat(ShaderConstants.WIND_SPEED, windZone.windMain);
                Shader.SetGlobalFloat(ShaderConstants.WIND_TURBULENCE, windZone.windTurbulence);
                Shader.SetGlobalFloat(ShaderConstants.WIND_PULSE_FREQUENCY, windZone.windPulseFrequency);
                Shader.SetGlobalFloat(ShaderConstants.WIND_PULSE_MAGNITUDE, windZone.windPulseMagnitude);
            }
        }
    }
}
