/*  Created by Ashley Seric  |  ashleyseric.com  |  https://github.com/ashleyseric  */

using UnityEngine;

namespace AshleySeric.ScatterStream
{
    [System.Serializable, CreateAssetMenu(fileName = "Scatter Filter", menuName = "Scatter Stream/Filter", order = 0)]
    public class ScatterFilter : ScriptableObject
    {
        public enum NameFilterScope
        {
            ObjectName,
            // TODO: Implement MeshName and MaterialName.
        }

        public enum NameFilterMethod
        {
            Contains,
            DoesNotContain,
            ExactMatch
        }

        public NameFilterMethod nameFilterMethod = NameFilterMethod.Contains;
        public NameFilterScope filterScope = NameFilterScope.ObjectName;
        public string filterKey = "";
        public bool isCaseSensitive = false;
    }
}