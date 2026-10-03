using UnityEditor;
using UnityEngine;

namespace TestMisha.Fx.Pulse.Editor
{
    /// <summary>Adds a Hierarchy menu entry that creates a ready-to-use Pulse Block Spawner.</summary>
    internal static class PulseBlockSpawnerMenu
    {
        private const string MenuPath = "GameObject/TestMisha/Pulse Block Spawner";
        private const int MenuPriority = 10;

        [MenuItem(MenuPath, false, MenuPriority)]
        private static void CreateSpawner(MenuCommand command)
        {
            var spawnerObject = new GameObject("Pulse Block Spawner");

            // Parent under the right-clicked object, like the built-in Create items do.
            GameObjectUtility.SetParentAndAlign(spawnerObject, command.context as GameObject);
            Undo.RegisterCreatedObjectUndo(spawnerObject, "Create Pulse Block Spawner");
            Undo.AddComponent<PulseBlockSpawner>(spawnerObject);
            Selection.activeGameObject = spawnerObject;
        }
    }
}
