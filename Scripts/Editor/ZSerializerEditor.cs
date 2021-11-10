using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;
using ZSerializer;
using ZSerializer.Editor;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace ZSerializer.Editor
{
    public static class ZSerializerEditor
    {
        [DidReloadScripts]
        static void InitializePackage()
        {
            if (!ZSerializerSettings.Instance ||
                ZSerializerSettings.Instance && !ZSerializerSettings.Instance.packageInitialized)
            {
                ZSerializerMenu.ShowWindow();
            }
        }

        [DidReloadScripts]
        static void TryRebuildZSerializers()
        {
            if (ZSerializerSettings.Instance && ZSerializerSettings.Instance.packageInitialized)
            {
                ZSerializerStyler styler = new ZSerializerStyler();
                if (styler.settings.autoRebuildZSerializers)
                {
                    var types = ZSerialize.GetPersistentTypes().ToArray();

                    Class[] classes = new Class[types.Length];

                    for (int i = 0; i < types.Length; i++)
                    {
                        classes[i] = new Class(types[i], GetClassState(types[i]));
                    }

                    string path;

                    foreach (var c in classes)
                    {
                        ClassState state = c.state;

                        if (state == ClassState.NeedsRebuilding)
                        {
                            var pathList = Directory.GetFiles("Assets", $"*{c.classType.Name}*.cs",
                                SearchOption.AllDirectories)[0].Split('.').ToList();
                            pathList.RemoveAt(pathList.Count - 1);
                            path = String.Join(".", pathList) + "ZSerializer.cs";

                            path = Application.dataPath.Substring(0, Application.dataPath.Length - 6) +
                                   path.Replace('\\', '/');

                            CreateZSerializer(c.classType, path);
                            AssetDatabase.Refresh();
                        }
                    }
                }
            }
        }

        public static void CreateZSerializer(Type type, string path)
        {
            string newPath = new string((new string(path.Reverse().ToArray())).Substring(path.Split('/').Last().Length)
                .Reverse().ToArray());
            Debug.Log("Editor script being created at " + newPath + "ZSerializers");
            string relativePath = "Assets" + newPath.Substring(Application.dataPath.Length);

            var ns = type.Namespace;


            if (!AssetDatabase.IsValidFolder(relativePath + "ZSerializers"))
            {
                Directory.CreateDirectory(newPath + "ZSerializers");
            }


            string newNewPath = newPath + "ZSerializers/" + type.Name + "ZSerializer.cs";

            FileStream fileStream = new FileStream(newNewPath, FileMode.Create, FileAccess.Write);
            StreamWriter sw = new StreamWriter(fileStream);


            string script =
                $"{(string.IsNullOrEmpty(ns) ? "" : $"namespace {ns} " + "{\n")}[System.Serializable]\n" +
                $"public sealed class {type.Name}ZSerializer : ZSerializer.Internal.ZSerializer\n" +
                "{\n";

            var fieldInfos = GetFieldsThatShouldBeSerialized(type);

            var currentType = type;

            // while (type.BaseType != typeof(MonoBehaviour))
            // {
            //     fieldInfos.AddRange(type.BaseType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            //         .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList());
            //     type = type.BaseType;
            // }

            type = currentType;

            foreach (var fieldInfo in fieldInfos)
            {
                var fieldType = fieldInfo.FieldType;

                if (fieldInfo.FieldType.IsArray)
                {
                    fieldType = fieldInfo.FieldType.GetElementType();
                }


                int genericParameterAmount = fieldType.GenericTypeArguments.Length;

                script +=
                    $"    public {fieldInfo.FieldType} {fieldInfo.Name};\n".Replace('+', '.');

                if (genericParameterAmount > 0)
                {
                    string oldString = $"`{genericParameterAmount}[";
                    string newString = "<";

                    var genericArguments = fieldType.GenericTypeArguments;

                    for (var i = 0; i < genericArguments.Length; i++)
                    {
                        oldString += genericArguments[i] + (i == genericArguments.Length - 1 ? "]" : ",");
                        newString += genericArguments[i] + (i == genericArguments.Length - 1 ? ">" : ",");
                    }

                    script = script.Replace(oldString, newString);
                }
            }

//             script += @"    public int groupID;
//     public bool autoSync;
// ";

            script +=
                $"\n    public {type.Name}ZSerializer(string ZUID, string GOZUID) : base(ZUID, GOZUID)\n" +
                "    {" +
                "       var instance = ZSerializer.ZSerialize.idMap[ZUID];\n";

            foreach (var fieldInfo in fieldInfos)
            {
                var fieldType = fieldInfo.FieldType;

                if (fieldInfo.FieldType.IsArray)
                {
                    fieldType = fieldInfo.FieldType.GetElementType();
                }


                int genericParameterAmount = fieldType.GenericTypeArguments.Length;

                script +=
                    $"         {fieldInfo.Name} = ({fieldInfo.FieldType})typeof({type.Name}).GetField(\"{fieldInfo.Name}\"{(!fieldInfo.IsPublic ? ", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic" : "")}).GetValue(instance);\n"
                        .Replace('+', '.');

                if (genericParameterAmount > 0)
                {
                    string oldString = $"`{genericParameterAmount}[";
                    string newString = "<";

                    var genericArguments = fieldType.GenericTypeArguments;

                    for (var i = 0; i < genericArguments.Length; i++)
                    {
                        oldString += genericArguments[i] + (i == genericArguments.Length - 1 ? "]" : ",");
                        newString += genericArguments[i] + (i == genericArguments.Length - 1 ? ">" : ",");
                    }

                    script = script.Replace(oldString, newString);
                }
            }


            // script += $"         groupID = (int)typeof({type.FullName}).GetProperty(\"GroupID\").GetValue(instance);\n" +
            //           $"         autoSync = (bool)typeof({type.FullName}).GetProperty(\"AutoSync\").GetValue(instance);\n" +
            //           "    }";

            script += "    }";

            script += "\n\n    public override void RestoreValues(UnityEngine.Component component)\n    {\n";

            foreach (var fieldInfo in fieldInfos)
            {
                script +=
                    $"         typeof({type.Name}).GetField(\"{fieldInfo.Name}\"{(!fieldInfo.IsPublic ? ", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic" : "")}).SetValue(component, {fieldInfo.Name});\n";
            }

            // script += $"         typeof({type.FullName}).GetProperty(\"GroupID\").SetValue(component, groupID);\n" +
            //           $"         typeof({type.FullName}).GetProperty(\"AutoSync\").SetValue(component, autoSync);\n" +
            //           "    }";
            script += "    }";

            script += "\n}";

            if (!string.IsNullOrEmpty(ns)) script += "\n}";

            ZSerialize.Log("ZSerializer script being created at " + newNewPath);

            sw.Write(script);

            sw.Close();

            foreach (var persistentGameObject in Object.FindObjectsOfType<PersistentMonoBehaviour>())
            {
                persistentGameObject.GenerateEditorZUIDs(false);
            }
        }

        static Type[] typesImplementingCustomEditor = AppDomain.CurrentDomain.GetAssemblies().SelectMany(ass =>
            ass.GetTypes()
                .Where(t => t.GetCustomAttributes(typeof(CustomEditor)).Any() && !t.FullName.Contains("UnityEngine.") &&
                            !t.FullName.Contains("UnityEditor.")).Select(t =>
                    t.GetCustomAttribute<CustomEditor>().GetType()
                        .GetField("m_InspectedType", BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.GetValue(t.GetCustomAttribute<CustomEditor>()) as Type)
                .Where(t => t.IsSubclassOf(typeof(MonoBehaviour)))).ToArray();

        public static void CreateEditorScript(Type type, string path)
        {
            if (typesImplementingCustomEditor.Contains(type))
            {
                Debug.Log($"{type} already implements a Custom Editor, and another one won't be created");
                return;
            }

            string editorScript =
                @"using UnityEditor;
using ZSerializer;
using ZSerializer.Editor;

[CustomEditor(typeof(" + type.FullName + @"))]
public sealed class " + type.Name + @"Editor : PersistentMonoBehaviourEditor<" + type.FullName + @"> 
{
    public override void OnInspectorGUI()
    {
        DrawPersistentMonoBehaviourInspector();
    }
}";


            string newPath = new string((new string(path.Reverse().ToArray())).Substring(path.Split('/').Last().Length)
                .Reverse().ToArray());
            Debug.Log("Editor script being created at " + newPath + "Editor");
            string relativePath = "Assets" + newPath.Substring(Application.dataPath.Length);


            if (!AssetDatabase.IsValidFolder(relativePath + "Editor"))
            {
                Directory.CreateDirectory(newPath + "Editor");
            }


            string newNewPath = newPath + "Editor/Z" + type.Name + "Editor.cs";
            FileStream fileStream = new FileStream(newNewPath, FileMode.Create, FileAccess.Write);
            StreamWriter sw = new StreamWriter(fileStream);
            sw.Write(editorScript);
            sw.Close();

            AssetDatabase.Refresh();
        }

        static List<FieldInfo> GetFieldsThatShouldBeSerialized(Type type)
        {
            //keep an eye on this, seems fishy

            var fieldInfos = type.GetFields()
                .Where(f =>
                {
                    return (f.GetCustomAttribute(typeof(NonZSerialized)) == null ||
                            f.GetCustomAttribute<ForceZSerialized>() != null) &&
                           (f.FieldType.IsSerializable || typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType) ||
                            (f.FieldType.FullName ?? f.FieldType.Name).StartsWith("UnityEngine.")) &&
                           (f.FieldType.IsGenericType
                               ? f.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)
                               : true);
                }).ToList();

            fieldInfos.AddRange(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(f =>
                {
                    return f.GetCustomAttribute<SerializeField>() != null &&
                           f.GetCustomAttribute<NonZSerialized>() == null &&
                           (f.FieldType.IsSerializable || typeof(UnityEngine.Object).IsAssignableFrom(f.FieldType) ||
                            (f.FieldType.FullName ?? f.FieldType.Name).StartsWith("UnityEngine.")) &&
                           (f.FieldType.IsGenericType
                               ? f.FieldType.GetGenericTypeDefinition() != typeof(Dictionary<,>)
                               : true);
                }));

            return fieldInfos;
        }


        public static ClassState GetClassState(Type type)
        {
            Type ZSerializerType = type.Assembly.GetType(type.FullName + "ZSerializer");
            if (ZSerializerType == null) return ClassState.NotMade;

            var fieldsZSerializer = ZSerializerType.GetFields()
                .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList();

            var fieldTypes = GetFieldsThatShouldBeSerialized(type);

            new Color(0, 0, 0, 1);

            if (fieldsZSerializer.Count == fieldTypes.Count)
            {
                for (int j = 0; j < fieldsZSerializer.Count; j++)
                {
                    if (fieldsZSerializer[j].Name != fieldTypes[j].Name ||
                        fieldsZSerializer[j].FieldType != fieldTypes[j].FieldType)
                    {
                        return ClassState.NeedsRebuilding;
                    }
                }

                return ClassState.Valid;
            }

            return ClassState.NeedsRebuilding;
        }

        public static bool SettingsButton(bool showSettings, ZSerializerStyler styler, int width)
        {
            return GUILayout.Toggle(showSettings, styler.cogWheel, new GUIStyle("button"),
                GUILayout.MaxHeight(width), GUILayout.MaxWidth(width));
        }

        public static void BuildWindowValidityButton(Type componentType, ZSerializerStyler styler)
        {
            int width = 32;

            ClassState state = GetClassState(componentType);

            var textureToUse = GetTextureToUse(state, styler);

            if (state == ClassState.Valid)
            {
                bool defaultOnValue = ZSerializerSettings.Instance.componentDataDictionary[componentType].isOn;
                textureToUse = defaultOnValue ? textureToUse : styler.offImage;
            }

            if (!Application.isPlaying)
            {
                if (GUILayout.Button(textureToUse,
                    GUILayout.MaxWidth(width), GUILayout.Height(width)))
                {
                    if (state == ClassState.Valid)
                    {
                        bool newOnValue = !ZSerializerSettings.Instance.componentDataDictionary[componentType].isOn;
                        ZSerializerSettings.Instance.componentDataDictionary[componentType].isOn = newOnValue;
                        foreach (var component in Object.FindObjectsOfType(componentType).Where(c =>
                            c.GetType() == componentType && ((PersistentMonoBehaviour)c).AutoSync))
                        {
                            ((PersistentMonoBehaviour)component).IsOn = newOnValue;
                            EditorUtility.SetDirty(component);
                        }

                        EditorUtility.SetDirty(ZSerializerSettings.Instance);
                        AssetDatabase.SaveAssets();
                    }
                    else
                    {
                        GenerateZSerializer(componentType, state);
                    }
                }
            }
        }

        private static void BuildInspectorValidityButton<T>(T component, ZSerializerStyler styler)
            where T : PersistentMonoBehaviour
        {
            int width = 28;
            ClassState state = GetClassState(typeof(T));

            var textureToUse = GetTextureToUse(state, styler);

            if (state == ClassState.Valid && component)
            {
                textureToUse = component.IsOn ? styler.validImage : styler.offImage;
            }

            if (!Application.isPlaying)
            {
                if (GUILayout.Button(textureToUse,
                    GUILayout.MaxWidth(width), GUILayout.Height(width)))
                {
                    if (state != ClassState.Valid)
                        GenerateZSerializer(typeof(T), state);
                    else
                    {
                        bool componentIsOn = component.IsOn;

                        if (component.AutoSync)
                        {
                            foreach (var persistentMonoBehaviour in Object.FindObjectsOfType<T>()
                                .Where(t => t.GetType() == component.GetType() && t.AutoSync))
                            {
                                persistentMonoBehaviour.IsOn = !componentIsOn;
                                ZSerializerSettings.Instance.componentDataDictionary[persistentMonoBehaviour.GetType()]
                                    .isOn = persistentMonoBehaviour.IsOn;
                            }
                        }
                        else
                        {
                            component.IsOn = !componentIsOn;
                        }

                        EditorUtility.SetDirty(component);
                        EditorUtility.SetDirty(ZSerializerSettings.Instance);
                    }
                }
            }
        }

        static Texture2D GetTextureToUse(ClassState state, ZSerializerStyler styler)
        {
            if (styler.validImage == null) styler.GetEveryResource();

            Texture2D textureToUse = styler.validImage;

            if (state != ClassState.Valid)
            {
                textureToUse = state == ClassState.NeedsRebuilding
                    ? styler.needsRebuildingImage
                    : styler.notMadeImage;
            }

            return textureToUse;
        }

        static void GenerateZSerializer(Type componentType, ClassState state)
        {
            var pathList = Directory.GetFiles("Assets", $"*{componentType.Name}*.cs",
                SearchOption.AllDirectories)[0].Split('.').ToList();
            pathList.RemoveAt(pathList.Count - 1);
            var path = String.Join(".", pathList) + "ZSerializer.cs";

            path = Application.dataPath.Substring(0, Application.dataPath.Length - 6) +
                   path.Replace('\\', '/');


            CreateZSerializer(componentType, path);
            if (state == ClassState.NotMade)
                CreateEditorScript(componentType, path);
            AssetDatabase.Refresh();
        }

        public static void BuildButtonAll(Class[] classes, int width, ZSerializerStyler styler)
        {
            Texture2D textureToUse = styler.refreshImage;

            if (GUILayout.Button(textureToUse,
                GUILayout.MaxWidth(width), GUILayout.Height(width)))
            {
                string path;

                foreach (var c in classes)
                {
                    var pathList = Directory.GetFiles("Assets", $"*{c.classType.Name}*.cs",
                        SearchOption.AllDirectories)[0].Split('.').ToList();
                    pathList.RemoveAt(pathList.Count - 1);
                    path = String.Join(".", pathList) + "ZSerializer.cs";
                    path = Application.dataPath.Substring(0, Application.dataPath.Length - 6) +
                           path.Replace('\\', '/');

                    CreateZSerializer(c.classType, path);
                    CreateEditorScript(c.classType, path);
                    AssetDatabase.Refresh();
                }
            }
        }


        internal static void BuildPersistentComponentEditor<T>(T manager, ZSerializerStyler styler,
            ref bool showSettings,
            Action<Type, IZSerialize, bool> toggleOn) where T : PersistentMonoBehaviour
        {
            // Texture2D cogwheel = styler.cogWheel;

            GUILayout.Space(-15);
            using (new GUILayout.HorizontalScope(ZSerializerStyler.window))
            {
                var state = GetClassState(manager.GetType());
                string color = state == ClassState.Valid ? manager.IsOn ? "29cf42" : "999999" :
                    state == ClassState.NeedsRebuilding ? "FFC107" : "FF625A";

                GUILayout.Label($"<color=#{color}>  Persistent Component</color>",
                    styler.header, GUILayout.Height(28));
                // using (new EditorGUI.DisabledScope(GetClassState(manager.GetType()) != ClassState.Valid))
                //     editMode = GUILayout.Toggle(editMode, cogwheel, new GUIStyle("button"), GUILayout.MaxWidth(28),
                //         GUILayout.Height(28));

                BuildInspectorValidityButton(manager, styler);
                showSettings = SettingsButton(showSettings, styler, 28);
                PrefabUtility.RecordPrefabInstancePropertyModifications(manager);
            }

            if (showSettings)
            {
                toggleOn?.Invoke(typeof(PersistentMonoBehaviour), manager, true);

                SerializedObject serializedObject = new SerializedObject(manager);
                serializedObject.Update();

                foreach (var field in typeof(T).GetFields()
                    .Where(f => f.DeclaringType != typeof(PersistentMonoBehaviour)))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        string color = field.GetCustomAttribute<NonZSerialized>() == null && manager.IsOn
                            ? "29cf42"
                            : "999999";
                        GUILayout.Label($"<color=#{color}>{field.Name.FieldNameToInspectorName()}</color>",
                            new GUIStyle("label") { richText = true },
                            GUILayout.Width(EditorGUIUtility.currentViewWidth / 3f));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(field.Name), GUIContent.none);
                    }
                }

                serializedObject.ApplyModifiedProperties();
            }
        }

        public static string FieldNameToInspectorName(this string value)
        {
            var charArray = value.ToCharArray();

            List<char> chars = new List<char>();

            for (int i = 0; i < charArray.Length; i++)
            {
                if (i == 0)
                {
                    chars.Add(Char.ToUpper(charArray[i]));
                    continue;
                }

                if (Char.IsUpper(charArray[i]))
                {
                    chars.Add(' ');
                    chars.Add(Char.ToUpper(charArray[i]));
                }
                else
                {
                    chars.Add(charArray[i]);
                }
            }

            return new string(chars.ToArray());
        }

        internal static void ShowGroupIDSettings(Type type, IZSerialize data, bool canAutoSync)
        {
            GUILayout.Space(-15);
            using (new EditorGUILayout.HorizontalScope(ZSerializerStyler.window))
            {
                GUILayout.Label("Save Group", GUILayout.MaxWidth(80));
                int newValue = EditorGUILayout.Popup(data.GroupID,
                    ZSerializerSettings.Instance.saveGroups.Where(s => !string.IsNullOrEmpty(s)).ToArray());
                if (newValue != data.GroupID)
                {
                    data.GroupID = newValue;
                }

                if (canAutoSync)
                {
                    SerializedObject o = new SerializedObject(data as PersistentMonoBehaviour);

                    o.Update();

                    EditorGUILayout.PropertyField(
                        o.FindProperty("autoSync"), GUIContent.none, GUILayout.Width(12));
                    GUILayout.Label("Sync", GUILayout.Width(35));

                    o.ApplyModifiedProperties();
                }
            }
        }

        public static void BuildEditModeEditor<T>(SerializedObject serializedObject, T manager, bool editMode,
            ref bool[] persistentFields, Action OnInspectorGUI)
        {
            if (editMode)
            {
                GUILayout.Label("Select fields to serialize",
                    new GUIStyle("helpbox") { alignment = TextAnchor.MiddleCenter }, GUILayout.Height(18));
                var fields = manager.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);

                for (var i = 0; i < fields.Length; i++)
                {
                    using (new GUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            SerializedProperty prop = serializedObject.FindProperty(fields[i].Name);
                            EditorGUILayout.PropertyField(prop);
                        }

                        persistentFields[i] = GUILayout.Toggle(persistentFields[i], "", GUILayout.MaxWidth(15));
                    }
                }
            }
            else
            {
                OnInspectorGUI.Invoke();
            }
        }

        private static Vector2 scrollPos;

        public static void BuildSettingsEditor(ZSerializerStyler styler, ref int selectedMenu, ref int selectedType,
            ref int selectedGroup, ref int selectedGroupIndex,
            float width)
        {
            IEnumerable<FieldInfo> fieldInfos = typeof(ZSerializerSettings)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(f => f.GetCustomAttribute<HideInInspector>() == null);
            SerializedObject serializedObject = new SerializedObject(styler.settings);

            string[] toolbarNames = { "Settings", "Groups", "Component Blacklist" };

            using (new GUILayout.VerticalScope("box"))
            {
                selectedMenu = GUILayout.Toolbar(selectedMenu, toolbarNames);

                switch (selectedMenu)
                {
                    case 0:

                        GUILayout.Space(-15);
                        using (new GUILayout.VerticalScope(ZSerializerStyler.window, GUILayout.Height(1)))
                        {
                            serializedObject.Update();

                            foreach (var fieldInfo in fieldInfos)
                            {
                                EditorGUI.BeginChangeCheck();

                                EditorGUILayout.PropertyField(serializedObject.FindProperty(fieldInfo.Name));

                                if (EditorGUI.EndChangeCheck())
                                {
                                    if (fieldInfo.Name == "advancedSerialization")
                                    {
                                        if (!ZSerializerSettings.Instance.advancedSerialization)
                                        {
                                            foreach (var persistentGameObject in Object
                                                .FindObjectsOfType<PersistentGameObject>())
                                            {
                                                persistentGameObject.serializedComponents.Clear();
                                            }
                                        }
                                    }
                                }
                            }

                            serializedObject.ApplyModifiedProperties();
#if UNITY_EDITOR_WIN
                            if (GUILayout.Button("Open Save file Directory"))
                            {
                                Process process = new Process();
                                ProcessStartInfo startInfo = new ProcessStartInfo();
                                startInfo.WindowStyle = ProcessWindowStyle.Normal;
                                startInfo.FileName = "cmd.exe";
                                string _path = Application.persistentDataPath;
                                startInfo.Arguments = $"/C start {_path}";
                                process.StartInfo = startInfo;
                                process.Start();
                            }
#endif
                        }

                        break;
                    case 1:


                        GUILayout.Space(-15);
                        using (new GUILayout.VerticalScope(ZSerializerStyler.window, GUILayout.Height(1)))
                        {
                            var groupsNames = new[] { "Serialization Groups", "Scene Groups" };
                            selectedGroup = GUILayout.Toolbar(selectedGroup, groupsNames);

                            serializedObject.Update();
                            switch (selectedGroup)
                            {
                                case 0:

                                    for (int i = 0; i < 16; i++)
                                    {
                                        using (new EditorGUI.DisabledScope(i < 2))
                                        {
                                            var prop = serializedObject.FindProperty("saveGroups")
                                                .GetArrayElementAtIndex(i);
                                            prop.stringValue = EditorGUILayout.TextArea(prop.stringValue,
                                                new GUIStyle("textField") { alignment = TextAnchor.MiddleCenter });
                                        }
                                    }

                                    if (GUILayout.Button("Reset all Group IDs from Scene"))
                                    {
                                        ZSerialize.Log("<color=cyan>Resetting All Group IDs</color>");

                                        foreach (var monoBehaviour in Object.FindObjectsOfType<MonoBehaviour>()
                                            .Where(o => o is IZSerialize))
                                        {
                                            var serialize = monoBehaviour as IZSerialize;
                                            serialize!.GroupID = 0;
                                            EditorUtility.SetDirty(monoBehaviour);

                                            // monoBehaviour.GetType().GetField("groupID",
                                            //         BindingFlags.NonPublic | BindingFlags.Instance)
                                            //     ?.SetValue(monoBehaviour, 0);
                                            // monoBehaviour.GetType().BaseType
                                            //     ?.GetField("groupID", BindingFlags.Instance | BindingFlags.NonPublic)
                                            //     ?.SetValue(monoBehaviour, 0);
                                        }
                                    }

                                    break;
                                case 1:

                                    // ZSerializerSettings.Instance.sceneGroups.Count;
                                    var groupsProp = serializedObject.FindProperty("sceneGroups");
                                    for (int i = 0; i < ZSerializerSettings.Instance.sceneGroups.Count; i++)
                                    {
                                        if (selectedGroupIndex == -1)
                                        {
                                            using (new EditorGUILayout.HorizontalScope())
                                            {
                                                var togglePrev = selectedGroupIndex == i;
                                                var toggle = GUILayout.Toggle(selectedGroupIndex == i, groupsProp
                                                    .GetArrayElementAtIndex(i)
                                                    .FindPropertyRelative("name").stringValue, new GUIStyle("button"));
                                                if (togglePrev != toggle)
                                                    selectedGroupIndex = toggle ? i : -1;


                                                if (GUILayout.Button("-", GUILayout.MaxWidth(20)))
                                                {
                                                    ZSerializerSettings.Instance.sceneGroups.RemoveAt(i);
                                                    EditorUtility.SetDirty(ZSerializerSettings.Instance);
                                                    AssetDatabase.SaveAssets();
                                                    return;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (selectedGroupIndex == i)
                                            {
                                                var togglePrev = selectedGroupIndex == i;
                                                var toggle = GUILayout.Toggle(selectedGroupIndex == i, groupsProp
                                                    .GetArrayElementAtIndex(i)
                                                    .FindPropertyRelative("name").stringValue, new GUIStyle("button"));
                                                if (togglePrev != toggle)
                                                    selectedGroupIndex = toggle ? i : -1;

                                                if (toggle)
                                                {
                                                    EditorGUILayout.PropertyField(groupsProp.GetArrayElementAtIndex(i)
                                                        .FindPropertyRelative("name"));

                                                    var path = groupsProp.GetArrayElementAtIndex(i)
                                                        .FindPropertyRelative("loadingManagementScenePath").stringValue;


                                                    var oldScene = string.IsNullOrEmpty(path)
                                                        ? null
                                                        : AssetDatabase.LoadAssetAtPath<SceneAsset>(
                                                            Path.Combine("Assets", path + ".unity"));
                                                    EditorGUI.BeginChangeCheck();
                                                    var newScene = EditorGUILayout.ObjectField("Loading Scene",
                                                        oldScene, typeof(SceneAsset), false) as SceneAsset;

                                                    if (EditorGUI.EndChangeCheck())
                                                    {
                                                        var newPath = AssetDatabase.GetAssetPath(newScene);
                                                        var scenePathProperty = groupsProp.GetArrayElementAtIndex(i)
                                                            .FindPropertyRelative("loadingManagementScenePath");
                                                        scenePathProperty.stringValue =
                                                            newPath.Substring(7, newPath.Length - 13);

                                                        EditorUtility.SetDirty(ZSerializerSettings.Instance);
                                                        AssetDatabase.SaveAssets();
                                                    }

                                                    var sceneAssetList = ZSerializerSettings.Instance.sceneGroups[i]
                                                        .scenePaths.Select(s =>
                                                            string.IsNullOrEmpty(s)
                                                                ? default(SceneAsset)
                                                                : AssetDatabase.LoadAssetAtPath<SceneAsset>(
                                                                    Path.Combine("Assets", s + ".unity"))).ToList();


                                                    using (new EditorGUILayout.VerticalScope("box"))
                                                    {
                                                        for (var j = 0; j < sceneAssetList.Count; j++)
                                                        {
                                                            var sceneAsset = sceneAssetList[j];
                                                            using (new GUILayout.HorizontalScope())
                                                            {
                                                                EditorGUI.BeginChangeCheck();
                                                                var newSceneAsset = EditorGUILayout.ObjectField(
                                                                    sceneAsset,
                                                                    typeof(SceneAsset), false) as SceneAsset;
                                                                if (EditorGUI.EndChangeCheck())
                                                                {
                                                                    var newPath =
                                                                        AssetDatabase.GetAssetPath(newSceneAsset);
                                                                    var scenePathProperty = groupsProp
                                                                        .GetArrayElementAtIndex(i)
                                                                        .FindPropertyRelative("scenePaths");
                                                                    scenePathProperty.GetArrayElementAtIndex(
                                                                            sceneAssetList
                                                                                .IndexOf(sceneAsset)).stringValue =
                                                                        newPath.Substring(7, newPath.Length - 13);

                                                                    EditorUtility.SetDirty(ZSerializerSettings
                                                                        .Instance);
                                                                    AssetDatabase.SaveAssets();
                                                                }

                                                                if (GUILayout.Button("-", GUILayout.MaxWidth(20)))
                                                                {
                                                                    ZSerializerSettings.Instance.sceneGroups[i].scenePaths.RemoveAt(j);
                                                                    return;
                                                                }
                                                            }
                                                        }

                                                        if (GUILayout.Button("+"))
                                                        {
                                                            ZSerializerSettings.Instance.sceneGroups[i].scenePaths.Add("");
                                                            EditorUtility.SetDirty(ZSerializerSettings.Instance);
                                                            AssetDatabase.SaveAssets();
                                                        }
                                                    }

                                                    // EditorGUILayout.PropertyField(groupsProp.GetArrayElementAtIndex(i)
                                                    //     .FindPropertyRelative("loadingManagementScene"));
                                                }
                                            }
                                        }
                                    }


                                    if (selectedGroupIndex == -1)
                                        if (GUILayout.Button("+"))
                                        {
                                            ZSerializerSettings.Instance.sceneGroups.Add(new SceneGroup
                                                { name = "New Scene Group" });
                                            EditorUtility.SetDirty(ZSerializerSettings.Instance);
                                            AssetDatabase.SaveAssets();
                                        }

                                    break;
                            }

                            serializedObject.ApplyModifiedProperties();
                        }

                        break;
                    case 2:
                        if (ZSerializerSettings.Instance
                            .componentBlackList.Count > 0)
                        {
                            using (new GUILayout.HorizontalScope(GUILayout.Width(1)))
                            {
                                using (new EditorGUILayout.VerticalScope())
                                {
                                    GUILayout.Space(-15);
                                    using (new EditorGUILayout.VerticalScope(ZSerializerStyler.window,
                                        GUILayout.Height(1),
                                        GUILayout.Height(Mathf.Max(88,
                                            20.6f * ZSerializerSettings.Instance.componentBlackList.Count))))
                                    {
                                        foreach (var serializableComponentBlackList in ZSerializerSettings.Instance
                                            .componentBlackList)
                                        {
                                            if (GUILayout.Button(serializableComponentBlackList.Type.Name,
                                                GUILayout.Width(150)))
                                            {
                                                selectedType =
                                                    ZSerializerSettings.Instance.componentBlackList.IndexOf(
                                                        serializableComponentBlackList);
                                            }
                                        }
                                    }
                                }


                                using (new EditorGUILayout.VerticalScope())
                                {
                                    GUILayout.Space(-15);
                                    using (new EditorGUILayout.VerticalScope(ZSerializerStyler.window,
                                        GUILayout.Height(1)))
                                    {
                                        using (var scrollView =
                                            new GUILayout.ScrollViewScope(scrollPos, new GUIStyle(),
                                                GUILayout.Width(width - 196),
                                                GUILayout.Height(Mathf.Max(61.8f,
                                                    20.6f * ZSerializerSettings.Instance.componentBlackList.Count))))
                                        {
                                            scrollPos = scrollView.scrollPosition;
                                            foreach (var componentName in ZSerializerSettings.Instance
                                                .componentBlackList[selectedType]
                                                .componentNames)
                                            {
                                                GUILayout.Label(componentName);
                                            }
                                        }
                                    }
                                }
                            }

                            if (GUILayout.Button("Delete Blacklist"))
                            {
                                ZSerializerSettings.Instance.componentBlackList.Clear();
                                EditorUtility.SetDirty(ZSerializerSettings.Instance);
                                AssetDatabase.SaveAssets();
                                selectedType = 0;
                                GenerateUnityComponentClasses();
                            }

                            if (GUILayout.Button("Open ZSerializer Configurator"))
                            {
                                ZSerializerFineTuner.ShowWindow();
                            }
                        }
                        else
                        {
                            GUILayout.Label("The Component Blacklist is Empty.",
                                new GUIStyle("label") { alignment = TextAnchor.MiddleCenter });
                            if (GUILayout.Button("Open Fine Tuner"))
                            {
                                ZSerializerFineTuner.ShowWindow();
                            }
                        }

                        break;
                    // case 3:
                    //     for (var i = 0; i < ZSerializerSettings.Instance.defaultOnDictionary.typeNames.Count; i++)
                    //     {
                    //         using (new GUILayout.HorizontalScope())
                    //         {
                    //             GUILayout.Label(ZSerializerSettings.Instance.defaultOnDictionary.typeNames[i], GUILayout.Width(200));//
                    //             GUILayout.Label(ZSerializerSettings.Instance.defaultOnDictionary.valueList[i].ToString());
                    //         }    
                    //     }
                    //     break;
                }
            }
        }

        [MenuItem("Tools/ZSerializer/Generate Unity Component ZSerializers")]
        public static void GenerateUnityComponentClasses()
        {
            string longScript = @"namespace ZSerializer {

";

            List<Type> types = ZSerialize.UnitySerializableTypes;
            foreach (var type in types)
            {
                EditorUtility.DisplayProgressBar("Generating Unity Component ZSerializers", type.Name,
                    types.IndexOf(type) / (float)types.Count);


                if (type != typeof(PersistentGameObject))
                {
                    longScript +=
                        "[System.Serializable]\npublic sealed class " + type.Name +
                        "ZSerializer : ZSerializer.Internal.ZSerializer {\n";

                    foreach (var propertyInfo in type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(ZSerialize.PropertyIsSuitableForZSerializer))
                    {
                        longScript +=
                            $"    public {propertyInfo.PropertyType.ToString().Replace('+', '.')} " +
                            propertyInfo.Name +
                            ";\n";
                    }

                    foreach (var fieldInfo in type
                        .GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.GetCustomAttribute<ObsoleteAttribute>() == null))
                    {
                        var fieldType = fieldInfo.FieldType;

                        if (fieldInfo.FieldType.IsArray)
                        {
                            fieldType = fieldInfo.FieldType.GetElementType();
                        }

                        int genericParameterAmount = fieldType.GenericTypeArguments.Length;

                        longScript +=
                            $"    public {fieldInfo.FieldType.ToString().Replace('+', '.')} " + fieldInfo.Name +
                            ";\n";

                        if (genericParameterAmount > 0)
                        {
                            string oldString = $"`{genericParameterAmount}[";
                            string newString = "<";

                            var genericArguments = fieldType.GenericTypeArguments;

                            for (var i = 0; i < genericArguments.Length; i++)
                            {
                                oldString += genericArguments[i].ToString().Replace('+', '.') +
                                             (i == genericArguments.Length - 1 ? "]" : ",");
                                newString += genericArguments[i].ToString().Replace('+', '.') +
                                             (i == genericArguments.Length - 1 ? ">" : ",");
                            }

                            longScript = longScript.Replace(oldString, newString);
                        }
                    }

                    longScript += "    public " + type.Name +
                                  "ZSerializer (string ZUID, string GOZUID) : base(ZUID, GOZUID) {\n" +
                                  "        var instance = ZSerializer.ZSerialize.idMap[ZUID] as " + type.FullName +
                                  ";\n";

                    foreach (var propertyInfo in type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(ZSerialize.PropertyIsSuitableForZSerializer))
                    {
                        longScript +=
                            $"      " + propertyInfo.Name + " = " + "instance." + propertyInfo.Name +
                            ";\n";
                    }

                    foreach (var fieldInfo in type
                        .GetFields(BindingFlags.Public | BindingFlags.Instance)
                        .Where(f => f.GetCustomAttribute<ObsoleteAttribute>() == null))
                    {
                        longScript +=
                            $"        " + fieldInfo.Name + " = " + "instance." + fieldInfo.Name + ";\n";
                    }

                    longScript += "    }\n";


                    longScript +=
                        @"    public override void RestoreValues(UnityEngine.Component component)
    {
        var _realComponent = component as " + type.FullName + @";
";
                    foreach (var propertyInfo in type
                        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(ZSerialize.PropertyIsSuitableForZSerializer))
                    {
                        longScript +=
                            $"        _realComponent." + propertyInfo.Name + " = " + propertyInfo.Name + ";\n";
                    }

                    longScript += "    }\n";
                    longScript += "}\n";
                }
            }

            EditorUtility.ClearProgressBar();

            longScript += "}";

            if (!Directory.Exists(Application.dataPath + "/ZResources/ZSerializer"))
                Directory.CreateDirectory(Application.dataPath + "/ZResources/ZSerializer");

            FileStream fs = new FileStream(
                Application.dataPath + "/ZResources/ZSerializer/UnityComponentZSerializers.cs",
                FileMode.Create);
            StreamWriter sw = new StreamWriter(fs);

            sw.Write(longScript);
            sw.Close();

            AssetDatabase.Refresh();
            ZSerialize.Log("Unity Component ZSerializers built");
        }

        [DidReloadScripts]
        static void OnReload()
        {
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            Dictionary<string, Object> map = new Dictionary<string, Object>();

            foreach (var monoBehaviour in Object.FindObjectsOfType<MonoBehaviour>().Where(o => o is IZSerialize)
                .Reverse())
            {
                var serializable = monoBehaviour as IZSerialize;
                if (!string.IsNullOrEmpty(serializable.ZUID))
                {
                    if (map.TryGetValue(serializable.ZUID, out _))
                    {
                        serializable.GenerateEditorZUIDs(map.TryGetValue(serializable.GOZUID, out var go) &&
                                                         go != monoBehaviour.gameObject);

                        ZSerialize.idMap.TryAdd(serializable.ZUID, monoBehaviour);
                        ZSerialize.idMap.TryAdd(serializable.GOZUID, monoBehaviour.gameObject);

                        if (serializable is PersistentGameObject pg)
                        {
                            foreach (var pgSerializedComponent in pg.serializedComponents)
                            {
                                ZSerialize.idMap.TryAdd(pgSerializedComponent.zuid, pgSerializedComponent.component);
                            }
                        }
                    }

                    if (serializable as Object)
                        map[serializable.ZUID] = serializable as Object;
                    if (monoBehaviour && monoBehaviour.gameObject)
                        map[serializable.GOZUID] = monoBehaviour.gameObject;
                }
            }
        }
    }
}