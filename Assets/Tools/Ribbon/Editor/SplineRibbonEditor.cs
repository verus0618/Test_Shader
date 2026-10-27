using UnityEditor;
using UnityEngine;

namespace VFX.Tools
{
    /// <summary>
    /// Inspector and Scene View tools for <see cref="SplineRibbon"/>: click a knot to select it, drag it with the
    /// move handle, click its tangent dots to bend the curve, and add or delete knots from the Inspector.
    /// </summary>
    [CustomEditor(typeof(SplineRibbon))]
    [CanEditMultipleObjects]
    public sealed class SplineRibbonEditor : Editor
    {
        private enum HandlePart { Knot, TangentIn, TangentOut }

        private const string ScriptField = "m_Script";
        private const string KnotsField = "knots";
        private const string ProfileField = "profile";
        private const string ProfileSegmentsField = "profileSegments";
        private const int CurvedProfileMinSegments = 8;
        private const int MinKnotCount = 2;
        private const float NewKnotDistance = 1f;
        private const float KnotHandleSize = 0.1f;
        private const float TangentHandleSize = 0.07f;
        private const float CurveLineWidth = 3f;
        private const int CreateMenuPriority = 10;

        private static readonly Color CurveColor = new Color(0.2f, 0.9f, 1f);
        private static readonly Color KnotColor = Color.white;
        private static readonly Color SelectedColor = new Color(1f, 0.9f, 0.2f);
        private static readonly Color TangentColor = new Color(1f, 0.6f, 0.2f);
        private static readonly Color AutoTangentColor = new Color(1f, 1f, 1f, 0.35f);

        private int _selectedKnot = -1;
        private HandlePart _selectedPart;
        private bool _showKnotList;

        private SplineRibbon Ribbon => (SplineRibbon)target;

        private void OnDisable()
        {
            UnityEditor.Tools.hidden = false;
        }

        #region Inspector

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawSettings();
            serializedObject.ApplyModifiedProperties();

            // Knot editing works on one ribbon at a time, the same as the Scene View handles.
            if (targets.Length == 1)
                DrawKnotEditor();
        }

        private void DrawSettings()
        {
            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.name == ScriptField || property.name == KnotsField)
                    continue;

                EditorGUILayout.PropertyField(property, true);
                if (property.name == ProfileField)
                    DrawProfilePresets(property);
            }
        }

        private void DrawProfilePresets(SerializedProperty profileProperty)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel(new GUIContent("Profile Preset", "Fills the Profile curve with a ready shape. Arc and V also raise Profile Segments so the bend is visible."));
                if (GUILayout.Button("Flat", EditorStyles.miniButtonLeft))
                    ApplyProfile(profileProperty, CreateFlatProfile(), false);
                if (GUILayout.Button("Arc", EditorStyles.miniButtonMid))
                    ApplyProfile(profileProperty, CreateArcProfile(), true);
                if (GUILayout.Button("V", EditorStyles.miniButtonRight))
                    ApplyProfile(profileProperty, CreateVProfile(), true);
            }
        }

        private void ApplyProfile(SerializedProperty profileProperty, AnimationCurve curve, bool curved)
        {
            profileProperty.animationCurveValue = curve;
            if (!curved)
                return;

            SerializedProperty segments = serializedObject.FindProperty(ProfileSegmentsField);
            if (segments.intValue < CurvedProfileMinSegments)
                segments.intValue = CurvedProfileMinSegments;
        }

        private void DrawKnotEditor()
        {
            SplineRibbon ribbon = Ribbon;
            if (ribbon.knots == null)
                return;
            ClampSelection(ribbon);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Knots", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Click a knot in the Scene View to select it. Click the orange dots to edit its tangents. Esc clears the selection.", MessageType.None);

            using (new EditorGUILayout.HorizontalScope())
            {
                string addLabel = _selectedKnot >= 0 ? "Insert After Selected" : "Add Knot At End";
                if (GUILayout.Button(addLabel))
                    AddKnot(ribbon);

                using (new EditorGUI.DisabledScope(_selectedKnot < 0 || ribbon.knots.Count <= MinKnotCount))
                {
                    if (GUILayout.Button("Delete Selected"))
                        DeleteSelectedKnot(ribbon);
                }
            }

            if (_selectedKnot >= 0)
                DrawSelectedKnot(ribbon);

            EditorGUILayout.Space();
            _showKnotList = EditorGUILayout.Foldout(_showKnotList, $"All Knots ({ribbon.knots.Count})", true);
            if (_showKnotList)
            {
                serializedObject.Update();
                EditorGUILayout.PropertyField(serializedObject.FindProperty(KnotsField), true);
                serializedObject.ApplyModifiedProperties();
            }
        }

        private void DrawSelectedKnot(SplineRibbon ribbon)
        {
            SplineRibbon.Knot knot = ribbon.knots[_selectedKnot];
            ribbon.GetResolvedTangents(_selectedKnot, out Vector3 resolvedIn, out Vector3 resolvedOut);

            EditorGUILayout.LabelField($"Selected Knot {_selectedKnot}", EditorStyles.miniBoldLabel);
            EditorGUI.BeginChangeCheck();
            Vector3 position = EditorGUILayout.Vector3Field("Position", knot.position);
            var mode = (SplineRibbon.TangentMode)EditorGUILayout.EnumPopup(
                new GUIContent("Tangent Mode", "Auto: smooth, calculated from neighbours. Mirrored: same length, opposite. Aligned: opposite, own length. Free: sharp corners allowed."),
                knot.tangentMode);
            Vector3 tangentIn;
            Vector3 tangentOut;
            using (new EditorGUI.DisabledScope(knot.tangentMode == SplineRibbon.TangentMode.Auto))
            {
                tangentIn = EditorGUILayout.Vector3Field("Tangent In", resolvedIn);
                tangentOut = EditorGUILayout.Vector3Field("Tangent Out", resolvedOut);
            }
            float knotWidth = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Width", "Width multiplier at this knot."), knot.width));
            float roll = EditorGUILayout.FloatField(new GUIContent("Roll", "Rotation of the ribbon around the curve at this knot, in degrees."), knot.roll);
            if (!EditorGUI.EndChangeCheck())
                return;

            BeginEdit(ribbon, "Edit Spline Knot");
            if (mode != knot.tangentMode)
            {
                // Leaving Auto keeps the current shape: the calculated tangents become the editable ones.
                if (knot.tangentMode == SplineRibbon.TangentMode.Auto)
                {
                    knot.tangentIn = resolvedIn;
                    knot.tangentOut = resolvedOut;
                }
                knot.tangentMode = mode;
                knot.SetTangentOut(knot.tangentOut);
            }
            else if (tangentIn != resolvedIn)
            {
                knot.SetTangentIn(tangentIn);
            }
            else if (tangentOut != resolvedOut)
            {
                knot.SetTangentOut(tangentOut);
            }
            knot.position = position;
            knot.width = knotWidth;
            knot.roll = roll;
            EndEdit(ribbon);
        }

        private void AddKnot(SplineRibbon ribbon)
        {
            BeginEdit(ribbon, "Add Spline Knot");

            int count = ribbon.knots.Count;
            int index = _selectedKnot >= 0 ? _selectedKnot : count - 1;
            var newKnot = new SplineRibbon.Knot();

            if (index >= 0 && index < ribbon.SegmentCount)
            {
                // Split the curve after the selected knot in the middle.
                SplineRibbon.Knot start = ribbon.knots[index];
                SplineRibbon.Knot end = ribbon.knots[(index + 1) % count];
                ribbon.GetSegment(index, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                newKnot.position = SplineRibbon.EvaluateBezier(p0, p1, p2, p3, 0.5f);
                newKnot.width = Mathf.Lerp(start.width, end.width, 0.5f);
                newKnot.roll = Mathf.Lerp(start.roll, end.roll, 0.5f);
            }
            else if (index >= 0)
            {
                // Extend the open end of the path in the direction it is heading.
                SplineRibbon.Knot last = ribbon.knots[index];
                newKnot.position = last.position + GetEndDirection(ribbon, index) * NewKnotDistance;
                newKnot.width = last.width;
                newKnot.roll = last.roll;
            }

            ribbon.knots.Insert(index + 1, newKnot);
            Select(index + 1, HandlePart.Knot);
            EndEdit(ribbon);
        }

        private static Vector3 GetEndDirection(SplineRibbon ribbon, int index)
        {
            ribbon.GetResolvedTangents(index, out _, out Vector3 tangentOut);
            if (tangentOut.sqrMagnitude > Mathf.Epsilon)
                return tangentOut.normalized;

            if (index > 0)
            {
                Vector3 fromPrevious = ribbon.knots[index].position - ribbon.knots[index - 1].position;
                if (fromPrevious.sqrMagnitude > Mathf.Epsilon)
                    return fromPrevious.normalized;
            }
            return Vector3.right;
        }

        private void DeleteSelectedKnot(SplineRibbon ribbon)
        {
            BeginEdit(ribbon, "Delete Spline Knot");
            ribbon.knots.RemoveAt(_selectedKnot);
            Select(Mathf.Min(_selectedKnot, ribbon.knots.Count - 1), HandlePart.Knot);
            EndEdit(ribbon);
        }

        #endregion

        #region Scene View

        private void OnSceneGUI()
        {
            SplineRibbon ribbon = Ribbon;
            if (ribbon.knots == null || targets.Length != 1)
                return;
            ClampSelection(ribbon);

            Event current = Event.current;
            if (_selectedKnot >= 0 && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                Select(-1, HandlePart.Knot);
                current.Use();
            }

            Transform transform = ribbon.transform;
            DrawCurve(ribbon, transform);
            DrawKnotButtons(ribbon, transform);
            if (_selectedKnot >= 0)
            {
                DrawTangents(ribbon, transform);
                DrawMoveHandle(ribbon, transform);
            }

            // The object's own move gizmo would sit on top of the knot handles.
            UnityEditor.Tools.hidden = _selectedKnot >= 0;
        }

        private static void DrawCurve(SplineRibbon ribbon, Transform transform)
        {
            for (int segment = 0; segment < ribbon.SegmentCount; segment++)
            {
                ribbon.GetSegment(segment, out Vector3 p0, out Vector3 p1, out Vector3 p2, out Vector3 p3);
                Handles.DrawBezier(transform.TransformPoint(p0), transform.TransformPoint(p3),
                    transform.TransformPoint(p1), transform.TransformPoint(p2), CurveColor, null, CurveLineWidth);
            }
        }

        private void DrawKnotButtons(SplineRibbon ribbon, Transform transform)
        {
            for (int i = 0; i < ribbon.knots.Count; i++)
            {
                Vector3 world = transform.TransformPoint(ribbon.knots[i].position);
                float size = HandleUtility.GetHandleSize(world) * KnotHandleSize;

                bool selected = i == _selectedKnot;
                Handles.color = selected ? SelectedColor : KnotColor;
                DrawSelectableDot(world, size, selected && _selectedPart == HandlePart.Knot, i, HandlePart.Knot);
                Handles.Label(world + Vector3.up * size * 1.5f, i.ToString());
            }
        }

        private void DrawTangents(SplineRibbon ribbon, Transform transform)
        {
            SplineRibbon.Knot knot = ribbon.knots[_selectedKnot];
            ribbon.GetResolvedTangents(_selectedKnot, out Vector3 tangentIn, out Vector3 tangentOut);

            // The ends of an open path only have one curve, so the unused tangent is hidden.
            bool hasIn = ribbon.closed || _selectedKnot > 0;
            bool hasOut = ribbon.closed || _selectedKnot < ribbon.knots.Count - 1;
            bool editable = knot.tangentMode != SplineRibbon.TangentMode.Auto;

            Vector3 knotWorld = transform.TransformPoint(knot.position);
            if (hasIn)
                DrawTangent(knotWorld, transform.TransformPoint(knot.position + tangentIn), editable, HandlePart.TangentIn);
            if (hasOut)
                DrawTangent(knotWorld, transform.TransformPoint(knot.position + tangentOut), editable, HandlePart.TangentOut);
        }

        private void DrawTangent(Vector3 knotWorld, Vector3 tipWorld, bool editable, HandlePart part)
        {
            Handles.color = editable ? TangentColor : AutoTangentColor;
            Handles.DrawLine(knotWorld, tipWorld);
            if (!editable)
                return;

            float size = HandleUtility.GetHandleSize(tipWorld) * TangentHandleSize;
            bool selected = _selectedPart == part;
            Handles.color = selected ? SelectedColor : TangentColor;
            DrawSelectableDot(tipWorld, size, selected, _selectedKnot, part);
        }

        // The active part only gets a plain dot, so clicks go to the move handle drawn on top of it.
        private void DrawSelectableDot(Vector3 world, float size, bool active, int knot, HandlePart part)
        {
            if (active)
            {
                if (Event.current.type == EventType.Repaint)
                    Handles.SphereHandleCap(0, world, Quaternion.identity, size, EventType.Repaint);
                return;
            }

            if (Handles.Button(world, Quaternion.identity, size, size, Handles.SphereHandleCap))
                Select(knot, part);
        }

        private void DrawMoveHandle(SplineRibbon ribbon, Transform transform)
        {
            SplineRibbon.Knot knot = ribbon.knots[_selectedKnot];
            ribbon.GetResolvedTangents(_selectedKnot, out Vector3 tangentIn, out Vector3 tangentOut);

            Vector3 local = knot.position;
            if (_selectedPart == HandlePart.TangentIn)
                local += tangentIn;
            else if (_selectedPart == HandlePart.TangentOut)
                local += tangentOut;

            Quaternion rotation = UnityEditor.Tools.pivotRotation == PivotRotation.Local ? transform.rotation : Quaternion.identity;
            EditorGUI.BeginChangeCheck();
            Vector3 world = Handles.PositionHandle(transform.TransformPoint(local), rotation);
            if (!EditorGUI.EndChangeCheck())
                return;

            BeginEdit(ribbon, "Move Spline Knot");
            Vector3 moved = transform.InverseTransformPoint(world);
            switch (_selectedPart)
            {
                case HandlePart.TangentIn:
                    knot.SetTangentIn(moved - knot.position);
                    break;
                case HandlePart.TangentOut:
                    knot.SetTangentOut(moved - knot.position);
                    break;
                default:
                    knot.position = moved;
                    break;
            }
            EndEdit(ribbon);
        }

        #endregion

        #region Helpers

        private void Select(int knot, HandlePart part)
        {
            _selectedKnot = knot;
            _selectedPart = part;
            Repaint();
            SceneView.RepaintAll();
        }

        // Undo, the knot list or a mode change can leave the selection pointing at something that is gone.
        private void ClampSelection(SplineRibbon ribbon)
        {
            if (_selectedKnot >= ribbon.knots.Count)
                _selectedKnot = -1;
            if (_selectedKnot >= 0 && ribbon.knots[_selectedKnot].tangentMode == SplineRibbon.TangentMode.Auto)
                _selectedPart = HandlePart.Knot;
        }

        private static void BeginEdit(SplineRibbon ribbon, string undoName)
        {
            Undo.RecordObject(ribbon, undoName);
        }

        // Knots are edited directly, so OnValidate does not run; the prefab override and rebuild are explicit.
        private static void EndEdit(SplineRibbon ribbon)
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(ribbon);
            ribbon.RequestRebuild();
            SceneView.RepaintAll();
        }

        private static AnimationCurve CreateFlatProfile()
        {
            return new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0f));
        }

        // Parabola 4x(1 - x): 0 at the edges, 1 in the middle, slope 4 at the edges.
        private static AnimationCurve CreateArcProfile()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0f, 0f, 4f),
                new Keyframe(0.5f, 1f, 0f, 0f),
                new Keyframe(1f, 0f, -4f, 0f));
        }

        private static AnimationCurve CreateVProfile()
        {
            var curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyBroken(curve, i, true);
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }
            return curve;
        }

        [MenuItem("GameObject/Effects/Spline Ribbon", false, CreateMenuPriority)]
        private static void CreateSplineRibbon(MenuCommand command)
        {
            var gameObject = new GameObject("Spline Ribbon");
            GameObjectUtility.SetParentAndAlign(gameObject, command.context as GameObject);
            if (command.context == null && SceneView.lastActiveSceneView != null)
                gameObject.transform.position = SceneView.lastActiveSceneView.pivot;

            // Adding the component runs its Reset, which assigns the default material.
            gameObject.AddComponent<SplineRibbon>();
            Undo.RegisterCreatedObjectUndo(gameObject, "Create Spline Ribbon");
            Selection.activeGameObject = gameObject;
        }

        #endregion
    }
}
