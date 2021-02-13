using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEditor;
using Deform;
using Unity.Mathematics;
using UnityEngine.Rendering;

namespace DeformEditor
{
    [CustomEditor(typeof(LatticeDeformer))]
    public class LatticeDeformerEditor : DeformerEditor
    {
        private Vector3Int newResolution;

        private quaternion handleRotation = quaternion.identity;
        private float3 handleScale = Vector3.one;
        
        private List<float3> originalPositions = new List<float3>();

        private Tool activeTool = Tool.None;

        private bool mouseDragEligible = false;
        private Vector2 mouseDownPosition;
        private int previousSelectionCount = 0;
        
        // Serialized so it can be picked up by Undo system
        [SerializeField] private List<int> selectedIndices = new List<int>();

        private static class Content
        {
            public static readonly GUIContent Target = new GUIContent(text: "Target", tooltip: DeformEditorGUIUtility.Strings.AxisTooltip);
            public static readonly GUIContent Resolution = new GUIContent(text: "Resolution", tooltip: "Per axis control point counts, the higher the resolution the more splits");
        }

        private class Properties
        {
            public SerializedProperty Target;
            public SerializedProperty Resolution;

            public Properties(SerializedObject obj)
            {
                Target = obj.FindProperty("target");
                Resolution = obj.FindProperty("resolution");
            }
        }

        private Properties properties;

        protected override void OnEnable()
        {
            base.OnEnable();

            properties = new Properties(serializedObject);
            newResolution = properties.Resolution.vector3IntValue;
        }

        public override void OnInspectorGUI()
        {
            base.OnInspectorGUI();

            LatticeDeformer latticeDeformer = ((LatticeDeformer) target);

            serializedObject.UpdateIfRequiredOrScript();

            EditorGUILayout.PropertyField(properties.Target, Content.Target);

            newResolution = EditorGUILayout.Vector3IntField(Content.Resolution, newResolution);
            // Make sure we have at least two control points per axis
            newResolution = Vector3Int.Max(newResolution, new Vector3Int(2, 2, 2));
            // Don't let the lattice resolution get ridiculously high
            newResolution = Vector3Int.Min(newResolution, new Vector3Int(32, 32, 32));

            if (GUILayout.Button("Update Lattice"))
            {
                Undo.RecordObject(target, "Update Lattice");
                latticeDeformer.GenerateControlPoints(newResolution, true);
                selectedIndices.Clear();
            }

            if (GUILayout.Button("Reset Lattice Points"))
            {
                Undo.RecordObject(target, "Reset Lattice Points");
                latticeDeformer.GenerateControlPoints(newResolution);
                selectedIndices.Clear();
            }

            if (latticeDeformer.CanAutoFitBounds)
            {
                if (GUILayout.Button("Auto-Fit Bounds"))
                {
                    Undo.RecordObject(target, "Auto-Fit Bounds");
                    latticeDeformer.FitBoundsToParentDeformable();
                }
            }

            if (selectedIndices.Count != 0)
            {
                if (GUILayout.Button("Stop Editing Control Points"))
                {
                    DeselectAll();
                }
            }

            //activeTool = (Tool) EditorGUILayout.EnumPopup("Active Tool", activeTool);

            serializedObject.ApplyModifiedProperties();

            EditorApplication.QueuePlayerLoopUpdate();
        }

        public override void OnSceneGUI()
        {
            base.OnSceneGUI();

            LatticeDeformer lattice = target as LatticeDeformer;
            float3[] controlPoints = lattice.ControlPoints;
            Event e = Event.current;

            using (new Handles.DrawingScope(lattice.transform.localToWorldMatrix))
            {
                var cachedZTest = Handles.zTest;

                // Change the depth testing to only show handles in front of solid objects (i.e. typical depth testing) 
                Handles.zTest = CompareFunction.LessEqual;
                DrawLattice(lattice, DeformHandles.LineMode.Solid);
                // Change the depth testing to only show handles *behind* solid objects 
                Handles.zTest = CompareFunction.Greater;
                DrawLattice(lattice, DeformHandles.LineMode.Light);

                // Restore the original z test value now we're done with our drawing
                Handles.zTest = cachedZTest;

                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointHandleID = GUIUtility.GetControlID("LatticeDeformerControlPoint".GetHashCode(), FocusType.Passive);
                            var activeColor = DeformEditorSettings.SolidHandleColor;
                            var controlPointIndex = lattice.GetIndex(x, y, z);

                            if (GUIUtility.hotControl == controlPointHandleID || selectedIndices.Contains(controlPointIndex))
                            {
                                activeColor = Handles.selectedColor;
                            }
                            else if (HandleUtility.nearestControl == controlPointHandleID)
                            {
                                activeColor = Handles.preselectionColor;
                            }

                            if (e.type == EventType.MouseDown && HandleUtility.nearestControl == controlPointHandleID && e.button == 0 && Foo.MouseActionAllowed())
                            {
                                BeginSelectionChangeRegion();
                                GUIUtility.hotControl = controlPointHandleID;
                                GUIUtility.keyboardControl = controlPointHandleID;
                                e.Use();

                                bool modifierKeyPressed = e.control || e.shift || e.command;

                                if (modifierKeyPressed && selectedIndices.Contains(controlPointIndex))
                                {
                                    // Pressed a modifier key so toggle the selection
                                    selectedIndices.Remove(controlPointIndex);
                                }
                                else
                                {
                                    if (!modifierKeyPressed)
                                    {
                                        selectedIndices.Clear();
                                    }

                                    if (!selectedIndices.Contains(controlPointIndex))
                                    {
                                        selectedIndices.Add(controlPointIndex);
                                    }
                                }

                                EndSelectionChangeRegion();
                            }

                            if (Tools.current != Tool.None && selectedIndices.Count != 0)
                            {
                                // If the user changes tool, change our internal mode to match but disable the corresponding Unity tool
                                // (e.g. they hit W key or press on the Rotate Tool button on the top left toolbar) 
                                activeTool = Tools.current;
                                Tools.current = Tool.None;
                            }

                            using (new Handles.DrawingScope(activeColor))
                            {
                                var position = controlPoints[controlPointIndex];
                                var size = HandleUtility.GetHandleSize(position) * DeformEditorSettings.ScreenspaceLatticeHandleCapSize;

                                Handles.DotHandleCap(
                                    controlPointHandleID,
                                    position,
                                    Quaternion.identity,
                                    size,
                                    e.type);
                            }
                        }
                    }
                }
            }

            var defaultControl = Foo.DisableObjectSelection();

            if (selectedIndices.Count != 0)
            {
                var currentPivotPosition = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var index in selectedIndices)
                    {
                        currentPivotPosition += controlPoints[index];
                    }

                    currentPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    currentPivotPosition = controlPoints[selectedIndices.Last()];
                }

                var originalPivotPosition = float3.zero;

                if (Tools.pivotMode == PivotMode.Center)
                {
                    // Get the average position
                    foreach (var originalPosition in originalPositions)
                    {
                        originalPivotPosition += originalPosition;
                    }

                    originalPivotPosition /= selectedIndices.Count;
                }
                else
                {
                    // Match the scene view behaviour that Pivot mode uses the last selected object as pivot
                    originalPivotPosition = originalPositions.Last();
                }


                float3 handlePosition = lattice.Target.TransformPoint(currentPivotPosition);

                if (e.type == EventType.MouseDown)
                {
                    // Potentially started interacting with a handle so reset everything
                    handleScale = Vector3.one;
                    handleRotation = Quaternion.identity;

                    // Cache the selected control point positions before the interaction, so that all handle
                    // transformations are done using the original values rather than compounding error each frame
                    originalPositions.Clear();
                    foreach (int selectedIndex in selectedIndices)
                    {
                        originalPositions.Add(controlPoints[selectedIndex]);
                    }
                }

                if (activeTool == Tool.Move)
                {
                    var positionHandleRotation = lattice.Target.rotation;
                    if (Tools.pivotRotation == PivotRotation.Global)
                    {
                        positionHandleRotation = Quaternion.identity;
                    }

                    EditorGUI.BeginChangeCheck();
                    float3 newPosition = Handles.PositionHandle(handlePosition, positionHandleRotation);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        var delta = newPosition - handlePosition;
                        delta = lattice.Target.InverseTransformVector(delta);
                        foreach (var selectedIndex in selectedIndices)
                        {
                            controlPoints[selectedIndex] += delta;
                        }
                    }
                }
                else if (activeTool == Tool.Rotate)
                {
                    EditorGUI.BeginChangeCheck();
                    quaternion newRotation = Handles.RotationHandle(handleRotation, handlePosition);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        // TODO: Add support for PivotRotation - i.e. local mode 
                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                            controlPoints[selectedIndices[index]] = originalPivotPosition + math.mul(newRotation, (originalPositions[index] - originalPivotPosition));
                        }
                    }
                }
                else if (activeTool == Tool.Scale)
                {
                    var size = HandleUtility.GetHandleSize(handlePosition);
                    EditorGUI.BeginChangeCheck();
                    handleScale = Handles.ScaleHandle(handleScale, handlePosition, handleRotation, size);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(target, "Update Lattice");

                        // TODO: Add support for PivotRotation - i.e. local mode
                        for (var index = 0; index < selectedIndices.Count; index++)
                        {
                            controlPoints[selectedIndices[index]] = originalPivotPosition + handleScale * (originalPositions[index] - originalPivotPosition);
                        }
                    }
                }
            }

            if (e.button == 0) // Left Mouse Button
            {
                if (e.type == EventType.MouseDown && HandleUtility.nearestControl == defaultControl && Foo.MouseActionAllowed())
                {
                    mouseDownPosition = e.mousePosition;
                    mouseDragEligible = true;
                    GUIUtility.hotControl = defaultControl;
                }
                else if (e.type == EventType.MouseDrag && mouseDragEligible)
                {
                    SceneView.currentDrawingSceneView.Repaint();
                }
                else if (e.type == EventType.MouseUp
                         || (mouseDragEligible && e.rawType == EventType.MouseUp)) // Have they released the mouse outside the scene view while doing marquee select?
                {
                    if (mouseDragEligible)
                    {
                        var mouseUpPosition = e.mousePosition;

                        Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                            Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                            Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));

                        BeginSelectionChangeRegion();

                        if (!e.shift && !e.control && !e.command)
                        {
                            selectedIndices.Clear();
                        }

                        for (var index = 0; index < controlPoints.Length; index++)
                        {
                            Camera camera = SceneView.currentDrawingSceneView.camera;
                            var screenPoint = Foo.WorldToGUIPoint(camera, lattice.transform.TransformPoint(controlPoints[index]));

                            if (screenPoint.z < 0)
                            {
                                // Don't consider points that are behind the camera
                            }

                            if (marqueeRect.Contains(screenPoint))
                            {
                                if (e.control || e.command) // Remove selection
                                {
                                    selectedIndices.Remove(index);
                                }
                                else
                                {
                                    selectedIndices.Add(index);
                                }
                            }
                        }

                        EndSelectionChangeRegion();
                    }

                    mouseDragEligible = false;
                }
            }

            if (e.type == EventType.Repaint && mouseDragEligible)
            {
                var mouseUpPosition = e.mousePosition;

                Rect marqueeRect = Rect.MinMaxRect(Mathf.Min(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Min(mouseDownPosition.y, mouseUpPosition.y),
                    Mathf.Max(mouseDownPosition.x, mouseUpPosition.x),
                    Mathf.Max(mouseDownPosition.y, mouseUpPosition.y));
                Foo.DrawMarquee(marqueeRect);
                SceneView.RepaintAll();
            }

            // If the lattice is visible, override Unity's built-in Select All so that it selects all control points 
            if (Foo.SelectAllPressed)
            {
                BeginSelectionChangeRegion();
                selectedIndices.Clear();
                var resolution = lattice.Resolution;
                for (int z = 0; z < resolution.z; z++)
                {
                    for (int y = 0; y < resolution.y; y++)
                    {
                        for (int x = 0; x < resolution.x; x++)
                        {
                            var controlPointIndex = lattice.GetIndex(x, y, z);
                            selectedIndices.Add(controlPointIndex);
                        }
                    }
                }

                EndSelectionChangeRegion();

                e.Use();
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                DeselectAll();
            }

            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void DeselectAll()
        {
            BeginSelectionChangeRegion();
            selectedIndices.Clear();
            EndSelectionChangeRegion();
        }

        private void BeginSelectionChangeRegion()
        {
            Undo.RecordObject(this, "Selection Change");
            previousSelectionCount = selectedIndices.Count;
        }

        private void EndSelectionChangeRegion()
        {
            if (selectedIndices.Count != 0 && previousSelectionCount == 0 && Tools.current == Tool.None) // Is this our first selection?
            {
                // Make sure when we start selecting control points we actually have a useful tool equipped
                activeTool = Tool.Move;
                Repaint(); // Make sure the inspector shows our new active tool
            }
        }

        private void DrawLattice(LatticeDeformer lattice, DeformHandles.LineMode lineMode)
        {
            var resolution = lattice.Resolution;
            var controlPoints = lattice.ControlPoints;
            for (int z = 0; z < resolution.z - 1; z++)
            {
                for (int y = 0; y < resolution.y - 1; y++)
                {
                    for (int x = 0; x < resolution.x - 1; x++)
                    {
                        int index000 = lattice.GetIndex(x, y, z);
                        int index100 = lattice.GetIndex(x + 1, y, z);
                        int index010 = lattice.GetIndex(x, y + 1, z);
                        int index110 = lattice.GetIndex(x + 1, y + 1, z);
                        int index001 = lattice.GetIndex(x, y, z + 1);
                        int index101 = lattice.GetIndex(x + 1, y, z + 1);
                        int index011 = lattice.GetIndex(x, y + 1, z + 1);
                        int index111 = lattice.GetIndex(x + 1, y + 1, z + 1);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index100], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index011], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index010], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index110], lineMode);
                        DeformHandles.Line(controlPoints[index001], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index101], controlPoints[index111], lineMode);

                        DeformHandles.Line(controlPoints[index000], controlPoints[index001], lineMode);
                        DeformHandles.Line(controlPoints[index100], controlPoints[index101], lineMode);
                        DeformHandles.Line(controlPoints[index010], controlPoints[index011], lineMode);
                        DeformHandles.Line(controlPoints[index110], controlPoints[index111], lineMode);
                    }
                }
            }
        }
    }
}