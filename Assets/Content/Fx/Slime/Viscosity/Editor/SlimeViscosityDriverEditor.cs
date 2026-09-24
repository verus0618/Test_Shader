using UnityEditor;

namespace TestMisha.Slime
{
    /// <summary>
    /// Default inspector plus a live status line: which objects the slime sees and how many deformations
    /// are in use, so "the slime does not react" can be traced to a missing collider or layer at a glance.
    /// </summary>
    [CustomEditor(typeof(SlimeViscosityDriver))]
    sealed class SlimeViscosityDriverEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var driver = (SlimeViscosityDriver)target;
            string objects = string.Join(", ", driver.TrackedObjects);
            if (objects.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "No objects near the slime. An object is found only if it has an enabled, non-trigger collider on one of the Layers above.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Nearby objects: {objects}\nActive contacts: {driver.ActiveContacts} / {driver.maxContacts}",
                    MessageType.Info);
            }
        }

        public override bool RequiresConstantRepaint() => true;
    }
}
