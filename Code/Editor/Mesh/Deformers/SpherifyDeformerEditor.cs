﻿using UnityEngine;
using UnityEditor;
using Deform;

namespace DeformEditor
{
	[CustomEditor (typeof (SpherifyDeformer)), CanEditMultipleObjects]
	public class SpherifyDeformerEditor : DeformerEditor
	{
		private class Content
		{
			public static readonly GUIContent Factor = DeformEditorGUIUtility.DefaultContent.Factor;
			public static readonly GUIContent Radius = new GUIContent (text: "Radius", tooltip: "The radius of the sphere that the points are pushed towards.");
			public static readonly GUIContent Smooth = new GUIContent (text: "Smooth", tooltip: "Should the interpolation towards the sphere be smoothed.");
			public static readonly GUIContent Axis = DeformEditorGUIUtility.DefaultContent.Axis;
		}

		private class Properties
		{
			public SerializedProperty Factor;
			public SerializedProperty Radius;
			public SerializedProperty Smooth;
			public SerializedProperty Axis;

			public Properties (SerializedObject obj)
			{
				Factor	= obj.FindProperty ("factor");
				Radius	= obj.FindProperty ("radius");
				Smooth	= obj.FindProperty ("smooth");
				Axis	= obj.FindProperty ("axis");
			}
		}

		private Properties properties;

		protected override void OnEnable ()
		{
			base.OnEnable ();
			properties = new Properties (serializedObject);
		}

		public override void OnInspectorGUI ()
		{
			base.OnInspectorGUI ();

			serializedObject.UpdateIfRequiredOrScript ();

			EditorGUILayout.Slider (properties.Factor, 0f, 1f, Content.Factor);
			EditorGUILayout.PropertyField (properties.Radius, Content.Radius);
			EditorGUILayout.PropertyField (properties.Smooth, Content.Smooth);
			EditorGUILayout.PropertyField (properties.Axis, Content.Axis);

			serializedObject.ApplyModifiedProperties ();

			EditorApplication.QueuePlayerLoopUpdate ();
		}

		public override void OnSceneGUI ()
		{
			base.OnEnable ();

			var spherify = target as SpherifyDeformer;

			DrawFactorHandle (spherify);
			DrawRadiusHandle (spherify);

			EditorApplication.QueuePlayerLoopUpdate ();
		}

		private void DrawRadiusHandle (SpherifyDeformer spherify)
		{
			using (var check = new EditorGUI.ChangeCheckScope ())
			{
				var newRadius = DeformHandles.Radius (spherify.Axis.rotation, spherify.Axis.position, spherify.Radius);
				if (check.changed)
				{
					Undo.RecordObject (spherify, "Changed Radius");
					spherify.Radius = newRadius;
				}
			}
		}

		private void DrawFactorHandle (SpherifyDeformer spherify)
		{
			if (spherify.Radius == 0f)
				return;

			var direction = spherify.Axis.forward;
			var worldPosition = spherify.Axis.position + direction * (spherify.Factor * spherify.Radius);

			DeformHandles.Line (spherify.Axis.position, worldPosition, DeformHandles.LineMode.Light);
			DeformHandles.Line (worldPosition, spherify.Axis.position + direction * spherify.Radius, DeformHandles.LineMode.LightDotted);

			using (var check = new EditorGUI.ChangeCheckScope ())
			{
				var newWorldPosition = DeformHandles.Slider (worldPosition, direction);
				if (check.changed)
				{
					Undo.RecordObject (spherify, "Changed Factor");
					var newFactor = DeformHandlesUtility.DistanceAlongAxis (spherify.Axis, spherify.Axis.position, newWorldPosition, Axis.Z) / spherify.Radius;
					spherify.Factor = newFactor;
				}
			}
		}
	}
}