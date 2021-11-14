﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using ZSerializer;
using ZSerializer.Editor;


[CustomEditor(typeof(PersistentGameObject))]
public class PersistentGameObjectEditor : Editor
{
    private PersistentGameObject manager;
    private static ZSerializerStyler styler;

    private void OnEnable()
    {
        manager = target as PersistentGameObject;
    }

    [DidReloadScripts]
    static void OnDatabaseReload()
    {
        styler = new ZSerializerStyler();
    }


    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        GUILayout.Space(-15);
        using (new EditorGUILayout.HorizontalScope(ZSerializerStyler.window))
        {
            GUILayout.Label($"<color=#{ZSerializerStyler.MainHex}>  Persistent GameObject</color>", styler.header, GUILayout.MinHeight(28));
            manager.showSettings = ZSerializerEditor.SettingsButton(manager.showSettings, styler, 28);
            PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
        }

        if (manager.showSettings)
        {
            ZSerializerEditor.ShowGroupIDSettings(typeof(PersistentGameObject), manager, false);
            if (ZSerializerSettings.Instance.advancedSerialization)
            {
                GUILayout.Space(-15);
                using (new GUILayout.VerticalScope(ZSerializerStyler.window))
                {
                    GUILayout.Label("Serialized Components:");
                    if (manager.serializedComponents.Count == 0) GUILayout.Label("None");
                    for (var i = 0; i < manager.serializedComponents.Count; i++)
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            serializedObject.Update();

                            var component = manager.serializedComponents[i];
                            Color fontColor = Color.cyan;
                            switch (component.persistenceType)
                            {
                                case PersistentType.Everything:
                                    fontColor = new Color(0.61f, 1f, 0.94f);
                                    break;
                                case PersistentType.Component:
                                    fontColor = new Color(1f, 0.79f, 0.47f);
                                    break;
                                case PersistentType.None:
                                    fontColor = new Color(1f, 0.56f, 0.54f);
                                    break;
                            }

                            GUILayout.Label(
                                component.Type.Name +
                                (ZSerializerSettings.Instance.debugMode ? $"({component.zuid})" : ""),
                                new GUIStyle("helpbox")
                                {
                                    font = styler.header.font, normal = new GUIStyleState() {textColor = fontColor},
                                    alignment = TextAnchor.MiddleCenter
                                }, GUILayout.MaxWidth(ZSerializerSettings.Instance.debugMode ? 150 : 100));

                            EditorGUILayout.PropertyField(serializedObject
                                .FindProperty(nameof(manager.serializedComponents)).GetArrayElementAtIndex(i)
                                .FindPropertyRelative("persistenceType"), GUIContent.none);

                            serializedObject.ApplyModifiedProperties();
                        }
                    }

                    if (GUILayout.Button("Reset"))
                    {
                        manager.serializedComponents.Clear();
                        manager.Reset();
                    }
                }
            }
        }

        serializedObject.ApplyModifiedProperties();
    }
}