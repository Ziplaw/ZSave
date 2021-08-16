using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using ZSerializer;
using ZSerializer.Editor;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

public static class ZSaverEditor
{
    [DidReloadScripts]
    static void InitializePackage()
    {
        if (!ZSaverSettings.Instance || ZSaverSettings.Instance && !ZSaverSettings.Instance.packageInitialized)
        {
            ZSaveManagerEditorWindow.ShowWindow();

            // ZSaverSettings.Instance.packageInitialized = true;
            // GenerateUnityComponentClasses();
        }
    }

    [DidReloadScripts]
    static void TryRebuildZSavers()
    {
        if (ZSaverSettings.Instance && ZSaverSettings.Instance.packageInitialized)
        {
            ZSaverStyler styler = new ZSaverStyler();
            if (styler.settings.autoRebuildZSerializers)
            {
                var types = ZSave.GetPersistentTypes().ToArray();

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

                        CreateZSaver(c.classType, path);
                        AssetDatabase.Refresh();
                    }
                }
            }
        }
    }

    public static void CreateZSaver(Type type, string path)
    {
        FileStream fileStream = new FileStream(path, FileMode.Create, FileAccess.Write);
        StreamWriter sw = new StreamWriter(fileStream);

        string script =
            "[System.Serializable]\n" +
            $"public class {type.Name}ZSerializer : ZSerializer.ZSerializer<{type.Name}>\n" +
            "{\n";

        var fieldInfos = type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList();

        fieldInfos.AddRange(type.BaseType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList());

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

        string className = type.Name + "Instance";

        script +=
            $"\n    public {type.Name}ZSerializer({type.Name} {className}) : base({className}.gameObject, {className})\n" +
            "    {\n";

        foreach (var fieldInfo in type.GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList())
        {
            var fieldType = fieldInfo.FieldType;

            if (fieldInfo.FieldType.IsArray)
            {
                fieldType = fieldInfo.FieldType.GetElementType();
            }


            int genericParameterAmount = fieldType.GenericTypeArguments.Length;

            script +=
                $"         {fieldInfo.Name} = ({fieldInfo.FieldType})typeof({type.Name}).GetField(\"{fieldInfo.Name}\").GetValue({className});\n"
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

        script += $"         groupID = {className}.GroupID;\n" +
                  $"         autoSync = {className}.AutoSync;\n" +
                  "    }\n}";

        ZSave.Log("ZSerializer script being created at " + path);

        sw.Write(script);

        sw.Close();
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
            Debug.Log($"{type} already implements a Custom Editor, and another one won´t be created");
            return;
        }

        string editorScript =
            @"using UnityEditor;
using UnityEditor.Callbacks;
using ZSerializer;

[CustomEditor(typeof(" + type.Name + @"))]
public class " + type.Name + @"Editor : Editor
{
    private " + type.Name + @" manager;
    private static ZSaverStyler styler;

    private void OnEnable()
    {
        manager = target as " + type.Name + @";
        styler = new ZSaverStyler();
    }

    [DidReloadScripts]
    static void OnDatabaseReload()
    {
        if(ZSaverSettings.Instance && ZSaverSettings.Instance.packageInitialized)
        styler = new ZSaverStyler();
    }

    public override void OnInspectorGUI()
    {
        if(manager is PersistentMonoBehaviour)
            ZSaverEditor.BuildPersistentComponentEditor(manager, styler, ref manager.showSettings, ZSaverEditor.ShowGroupIDSettings);
        base.OnInspectorGUI();
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


    public static ClassState GetClassState(Type type)
    {
        Type ZSaverType = type.Assembly.GetType(type.Name + "ZSerializer");
        // Debug.Log(type.Assembly);
        if (ZSaverType == null) return ClassState.NotMade;

        var fieldsZSaver = ZSaverType.GetFields()
            .Where(f => f.GetCustomAttribute(typeof(NonZSerialized)) == null).ToList();
        var fieldsType = type.GetFields().Where(f =>
                f.GetCustomAttribute<NonZSerialized>() == null || f.GetCustomAttribute<ForceZSerialized>() != null)
            .ToList();
        fieldsType.AddRange(type.BaseType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Where(f =>
            f.GetCustomAttribute<NonZSerialized>() == null || f.GetCustomAttribute<ForceZSerialized>() != null));

        if (fieldsZSaver.Count == fieldsType.Count - 0)
        {
            for (int j = 0; j < fieldsZSaver.Count; j++)
            {
                if (fieldsZSaver[j].Name != fieldsType[j].Name ||
                    fieldsZSaver[j].FieldType != fieldsType[j].FieldType)
                {
                    return ClassState.NeedsRebuilding;
                }
            }

            return ClassState.Valid;
        }

        return ClassState.NeedsRebuilding;
    }

    public static bool SettingsButton(bool showSettings, ZSaverStyler styler, int width)
    {
        return GUILayout.Toggle(showSettings, styler.cogWheel, new GUIStyle("button"),
            GUILayout.MaxHeight(width), GUILayout.MaxWidth(width));
    }

    public static void BuildButton(Type type, int width, ZSaverStyler styler)
    {
        ClassState state = GetClassState(type);
        if (styler.validImage == null) styler.GetEveryResource();

        using (new EditorGUI.DisabledGroupScope(state == ClassState.Valid))
        {
            Texture2D textureToUse = styler.validImage;

            if (state != ClassState.Valid)
            {
                textureToUse = state == ClassState.NeedsRebuilding
                    ? styler.needsRebuildingImage
                    : styler.notMadeImage;
            }

            if (GUILayout.Button(textureToUse,
                GUILayout.MaxWidth(width), GUILayout.Height(width)))
            {
                string path;

                if (UnityEngine.Random.Range(0, 100) == 0)
                {
                    PlayClip(Resources.Load<AudioClip>("surprise"));
                }

                var pathList = Directory.GetFiles("Assets", $"*{type.Name}*.cs",
                    SearchOption.AllDirectories)[0].Split('.').ToList();
                pathList.RemoveAt(pathList.Count - 1);
                path = String.Join(".", pathList) + "ZSerializer.cs";

                path = Application.dataPath.Substring(0, Application.dataPath.Length - 6) +
                       path.Replace('\\', '/');


                CreateZSaver(type, path);
                if (state == ClassState.NotMade)
                    CreateEditorScript(type, path);
                AssetDatabase.Refresh();
            }
        }
    }

    public static void PlayClip(AudioClip clip, int startSample = 0, bool loop = false)
    {
        Assembly unityEditorAssembly = typeof(AudioImporter).Assembly;

        Type audioUtilClass = unityEditorAssembly.GetType("UnityEditor.AudioUtil");
        MethodInfo method = audioUtilClass.GetMethod(
            "PlayPreviewClip",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new Type[] {typeof(AudioClip), typeof(int), typeof(bool)},
            null
        );

        method?.Invoke(
            null,
            new object[] {clip, startSample, loop}
        );
    }

    public static void BuildButtonAll(Class[] classes, int width, ZSaverStyler styler)
    {
        Texture2D textureToUse = styler.refreshImage;

        if (GUILayout.Button(textureToUse,
            GUILayout.MaxWidth(width), GUILayout.Height(width)))
        {
            string path;

            // string folderPath = EditorUtility.SaveFolderPanel("ZSerializer.cs Save Locations", "Assets", "");

            foreach (var c in classes)
            {
                var pathList = Directory.GetFiles("Assets", $"*{c.classType.Name}*.cs",
                    SearchOption.AllDirectories)[0].Split('.').ToList();
                pathList.RemoveAt(pathList.Count - 1);
                path = String.Join(".", pathList) + "ZSerializer.cs";
                path = Application.dataPath.Substring(0, Application.dataPath.Length - 6) +
                       path.Replace('\\', '/');

                CreateZSaver(c.classType, path);
                CreateEditorScript(c.classType, path);
                AssetDatabase.Refresh();
            }
        }
    }


    public static void BuildPersistentComponentEditor<T>(T manager, ZSaverStyler styler, ref bool showSettings,
        Action<Type, ISaveGroupID, bool> toggleOn) where T : ISaveGroupID
    {
        // Texture2D cogwheel = styler.cogWheel;

        using (new GUILayout.HorizontalScope("helpbox"))
        {
            GUILayout.Label("Persistent Component",
                styler.header, GUILayout.Height(28));
            // using (new EditorGUI.DisabledScope(GetClassState(manager.GetType()) != ClassState.Valid))
            //     editMode = GUILayout.Toggle(editMode, cogwheel, new GUIStyle("button"), GUILayout.MaxWidth(28),
            //         GUILayout.Height(28));

            BuildButton(manager.GetType(), 28, styler);
            showSettings = SettingsButton(showSettings, styler, 28);
            PrefabUtility.RecordPrefabInstancePropertyModifications(manager as MonoBehaviour);
        }

        if (showSettings)
            toggleOn?.Invoke(typeof(PersistentMonoBehaviour), manager, true);
    }

    public static void ShowGroupIDSettings(Type type, ISaveGroupID data, bool canAutoSync)
    {
        using (new EditorGUILayout.HorizontalScope("helpbox"))
        {
            GUILayout.Label("Save Group", GUILayout.MaxWidth(80));
            int newValue = EditorGUILayout.Popup(data.GroupID,
                ZSaverSettings.Instance.saveGroups.Where(s => !string.IsNullOrEmpty(s)).ToArray());
            if (newValue != data.GroupID)
            {
                if (!data.AutoSync)
                {
                    type.GetField("groupID", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                        .SetValue(data, newValue);
                }
                else
                {
                    foreach (var o in GameObject.FindObjectsOfType(data.GetType())
                        .Where(t => ((ISaveGroupID) t).AutoSync))
                    {
                        o.GetType().BaseType.GetField("groupID", BindingFlags.NonPublic | BindingFlags.Instance)
                            .SetValue(o, newValue);
                    }
                }
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
                new GUIStyle("helpbox") {alignment = TextAnchor.MiddleCenter}, GUILayout.Height(18));
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

    public static void BuildSettingsEditor(ZSaverStyler styler, ref int selectedMenu, ref int selectedType, float width)
    {
        IEnumerable<FieldInfo> fieldInfos = typeof(ZSaverSettings)
            .GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(f => f.GetCustomAttribute<HideInInspector>() == null);
        SerializedObject serializedObject = new SerializedObject(styler.settings);

        string[] toolbarNames;

        if (ZSaverSettings.Instance.debugMode)
        {
            toolbarNames = new[] {"Settings", "Saving Groups", "Component Blacklist"};
        }
        else
        {
            toolbarNames = new[] {"Settings", "Saving Groups"};
        }

        selectedMenu = GUILayout.Toolbar(selectedMenu, toolbarNames);

        switch (selectedMenu)
        {
            case 0:
                using (new GUILayout.VerticalScope("helpbox"))
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
                                if (!ZSaverSettings.Instance.advancedSerialization)
                                {
                                    foreach (var persistentGameObject in Object.FindObjectsOfType<PersistentGameObject>())
                                    {
                                        persistentGameObject.serializedComponents.Clear();
                                    }
                                }
                            }
                        }
                    }

                    serializedObject.ApplyModifiedProperties();
                }

                break;
            case 1:
                using (new GUILayout.VerticalScope("helpbox"))
                {
                    serializedObject.Update();

                    if (GUILayout.Button("Reset all Group IDs from Scene"))
                    {
                        ZSave.Log("<color=cyan>Resetting All Group IDs</color>");

                        foreach (var monoBehaviour in GameObject.FindObjectsOfType<MonoBehaviour>()
                            .Where(o => o is ISaveGroupID))
                        {
                            monoBehaviour.GetType().GetField("groupID", BindingFlags.NonPublic | BindingFlags.Instance)
                                ?.SetValue(monoBehaviour, 0);
                            monoBehaviour.GetType().BaseType
                                ?.GetField("groupID", BindingFlags.Instance | BindingFlags.NonPublic)
                                ?.SetValue(monoBehaviour, 0);
                        }
                    }

                    for (int i = 0; i < 16; i++)
                    {
                        using (new EditorGUI.DisabledScope(i < 2))
                        {
                            var prop = serializedObject.FindProperty("saveGroups").GetArrayElementAtIndex(i);
                            prop.stringValue = EditorGUILayout.TextArea(prop.stringValue,
                                new GUIStyle("textField") {alignment = TextAnchor.MiddleCenter});
                        }
                    }

                    serializedObject.ApplyModifiedProperties();
                }

                break;
            case 2:

                using (new GUILayout.HorizontalScope(GUILayout.Width(20)))
                {
                    using (new EditorGUILayout.VerticalScope("helpbox"))
                    {
                        foreach (var serializableComponentBlackList in ZSaverSettings.Instance.componentBlackList)
                        {
                            if (GUILayout.Button(serializableComponentBlackList.Type.Name))
                            {
                                selectedType =
                                    ZSaverSettings.Instance.componentBlackList.IndexOf(serializableComponentBlackList);
                            }
                        }
                    }

                    using (var scrollView =
                        new GUILayout.ScrollViewScope(scrollPos, new GUIStyle("helpbox"), GUILayout.Width(width - 124),
                            GUILayout.Height(21.8f * ZSaverSettings.Instance.componentBlackList.Count)))
                    {
                        scrollPos = scrollView.scrollPosition;
                        foreach (var componentName in ZSaverSettings.Instance.componentBlackList[selectedType]
                            .componentNames)
                        {
                            GUILayout.Label(componentName);
                        }
                    }
                }

                break;
        }

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
    }

    [MenuItem("Tools/ZSave/Generate Unity Component ZSerializers")]
    public static void GenerateUnityComponentClasses()
    {
        string longScript = "";

        IEnumerable<Type> types = ZSave.ComponentSerializableTypes;
        foreach (var type in types)
        {
            if (type != typeof(PersistentGameObject))
            {
                longScript +=
                    "[System.Serializable]\npublic class " + type.Name + "ZSerializer : ZSerializer.ZSerializer<" +
                    type.FullName +
                    "> {\n";

                foreach (var propertyInfo in type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(ZSave.PropertyIsSuitableForAssignment))
                {
                    // if (propertyInfo.PropertyType == typeof(RayTracingMode))
                    // {
                    //     Debug.Log(propertyInfo.Name + " " + type + " " +  ZSave.PropertyIsSuitableForAssignment(propertyInfo) + " " + ZSaverSettings.Instance.componentBlackList.IsInBlackList(type, propertyInfo.Name));
                    // }

                    longScript +=
                        $"    public {propertyInfo.PropertyType.ToString().Replace('+', '.')} " + propertyInfo.Name +
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

                if (type == typeof(PersistentGameObject))
                    longScript +=
                        $"    public ZSaver.GameObjectData gameObjectData;\n";

                longScript += "    public " + type.Name + "ZSerializer (" + type.FullName + " " + type.Name +
                              "Instance) : base(" +
                              type.Name + "Instance.gameObject, " + type.Name + "Instance ) {\n";

                foreach (var propertyInfo in type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(ZSave.PropertyIsSuitableForAssignment))
                {
                    longScript +=
                        $"        " + propertyInfo.Name + " = " + type.Name + "Instance." + propertyInfo.Name + ";\n";
                }

                foreach (var fieldInfo in type
                    .GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => f.GetCustomAttribute<ObsoleteAttribute>() == null))
                {
                    longScript +=
                        $"        " + fieldInfo.Name + " = " + type.Name + "Instance." + fieldInfo.Name + ";\n";
                }

                if (type == typeof(PersistentGameObject))
                    longScript +=
                        @"        gameObjectData =new ZSaver.GameObjectData()
        {
            loadingOrder = PersistentGameObject.CountParents(PersistentGameObjectInstance.transform),
            active = _componentParent.activeSelf,
            hideFlags = _componentParent.hideFlags,
            isStatic = _componentParent.isStatic,
            layer = PersistentGameObjectInstance.gameObject.layer,
            name = _componentParent.name,
            position = _componentParent.transform.position,
            rotation = _componentParent.transform.rotation,
            size = _componentParent.transform.localScale,
            tag = PersistentGameObjectInstance.gameObject.tag,
            parent = PersistentGameObjectInstance.transform.parent ? PersistentGameObjectInstance.transform.parent.gameObject : null
        };";

                longScript += "\n    }\n";
                longScript += "}\n";
            }
        }

        if (!Directory.Exists(Application.dataPath + "/ZResources"))
            Directory.CreateDirectory(Application.dataPath + "/ZResources");

        FileStream fs = new FileStream(
            Application.dataPath + "/ZResources/UnityComponentZSerializers.cs",
            FileMode.Create);
        StreamWriter sw = new StreamWriter(fs);

        sw.Write(longScript);
        sw.Close();

        AssetDatabase.Refresh();
        ZSave.Log("Unity Component ZSerializers built");
    }
}