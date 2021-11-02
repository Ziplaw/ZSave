using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using JetBrains.Annotations;
using UnityEngine;
using UnityEngine.SceneManagement;
using ZSerializer.Internal;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

[assembly: InternalsVisibleTo("com.Ziplaw.ZSaver.Editor")]
// [assembly: InternalsVisibleTo("Assembly-CSharp")]

namespace ZSerializer
{
    public sealed class ZSerialize
    {
        #region Variables

        internal static int currentGroupID = -1;
        private static string currentScene;
        private static string persistentDataPath;
        private static string _currentLevelName;
        private static Transform _currentParent;
        private static string jsonToSave;
        public static Dictionary<string, Object> idMap = new Dictionary<string, Object>();

        internal static (Type, string)[][] tempTuples;

        private static List<string> saveFiles;

        //Assemblies in which Unity Components are located
        private static List<string> unityComponentAssemblies = new List<string>();

        //All fields allowed to be added to the Serializable Unity Components list
        private static List<Type> unitySerializableTypes;
        internal static List<Type> UnitySerializableTypes => unitySerializableTypes ??= GetUnitySerializableTypes();


        //Cached methods to be invoked dynamically during serialization
        private static MethodInfo castMethod = typeof(Enumerable).GetMethod("Cast");
        private static MethodInfo toArrayMethod = typeof(Enumerable).GetMethod("ToArray");

        private static MethodInfo saveMethod =
            typeof(ZSerialize).GetMethod(nameof(CompileJson), BindingFlags.NonPublic | BindingFlags.Static);

        private static MethodInfo fromJsonMethod = typeof(JsonHelper).GetMethod(nameof(JsonHelper.FromJson));

        private const string mainAssembly = "Assembly-CSharp";


        //IDs to be stored for InstanceID manipulation when loading destroyed GameObjects
        // internal static Dictionary<int, int> idStorage = new Dictionary<int, int>();

        //Every type inheriting from PersistentMonoBehaviour
        internal static IEnumerable<Type> GetPersistentTypes()
        {
            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies();

            foreach (var assembly in assemblies)
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (type.IsSubclassOf(typeof(PersistentMonoBehaviour)))
                    {
                        yield return type;
                    }
                }
            }
        }

        #endregion

        #region Helper Functions

        internal static string GetRuntimeSafeZUID()
        {
            return (Random.value * 100000000).ToString();
        }

        static int[] GetIDList()
        {
            return saveFiles.Select(f =>
            {
                var split = f.Replace('\\', '/').Split('/');
                return ZSerializerSettings.Instance.saveGroups.IndexOf(split[split.Length - 2]);
            }).ToArray();
        }

        static List<Type> GetUnitySerializableTypes()
        {
            return AppDomain.CurrentDomain.GetAssemblies(
            ).SelectMany(a =>
                a.GetTypes().Where(t => t == typeof(PersistentGameObject) ||
                                        t.IsSubclassOf(typeof(Component)) &&
                                        !t.IsSubclassOf(typeof(MonoBehaviour)) &&
                                        t != typeof(Transform) &&
                                        t != typeof(MonoBehaviour) &&
                                        t.GetCustomAttribute<ObsoleteAttribute>() == null &&
                                        t.IsVisible)
            ).ToList();
        }

        internal static bool PropertyIsSuitableForZSerializer(PropertyInfo propertyInfo)
        {
            return propertyInfo.GetCustomAttribute<ObsoleteAttribute>() == null &&
                   propertyInfo.GetCustomAttribute<NonZSerialized>() == null &&
                   propertyInfo.GetSetMethod() != null &&
                   propertyInfo.GetSetMethod().IsPublic &&
                   propertyInfo.CanRead &&
                   propertyInfo.CanWrite &&
                   !ZSerializerSettings.Instance.componentBlackList.IsInBlackList(propertyInfo.ReflectedType,
                       propertyInfo.Name) &&
                   propertyInfo.Name != "material" &&
                   propertyInfo.Name != "materials" &&
                   propertyInfo.Name != "sharedMaterial" &&
                   propertyInfo.Name != "mesh" &&
                   propertyInfo.Name != "tag" &&
                   propertyInfo.Name != "name";
        }

        [RuntimeInitializeOnLoadMethod]
        internal static void Init()
        {
            persistentDataPath = Application.persistentDataPath;
            unitySerializableTypes = GetUnitySerializableTypes();
            OnSceneLoad();

            SceneManager.sceneUnloaded += scene => { OnSceneUnload(); };

            SceneManager.sceneLoaded += (scene, mode) => { OnSceneLoad(); };

            Application.wantsToQuit += () =>
            {
                OnSceneUnload();
                return true;
            };
        }

        #region Scene Loading

        private static void OnSceneLoad()
        {
            currentScene = GetCurrentScene();
        }

        private static void OnSceneUnload()
        {
            //Scene unloading stuff
        }

        #endregion

        #region Save Group Utilities

        /// <summary>
        /// Get a Save Group's ID from its name
        /// </summary>
        /// <param name="name">The name of the SaveGroup</param>
        /// <returns>Save Group's ID</returns>
        public static int NameToSaveGroupID(string name)
        {
            return ZSerializerSettings.Instance.saveGroups.IndexOf(name);
        }

        /// <summary>
        /// Get a SaveGroup's name from its ID
        /// </summary>
        /// <param name="id">The ID of the Save Group</param>
        /// <returns>Save Group's Name</returns>
        public static string SaveGroupIDToName(int id)
        {
            return ZSerializerSettings.Instance.saveGroups[id];
        }

        #endregion

        #region Logging

        //internal functions to Log stuff for Debug Mode
        internal static void Log(object obj)
        {
            if (ZSerializerSettings.Instance.debugMode) Debug.Log(obj);
        }

        internal static void LogWarning(object obj)
        {
            if (ZSerializerSettings.Instance.debugMode) Debug.LogWarning(obj);
        }

        internal static void LogError(object obj)
        {
            if (ZSerializerSettings.Instance.debugMode) Debug.LogError(obj);
        }

        #endregion

        static string GetCurrentScene()
        {
            return SceneManager.GetActiveScene().name;
        }

        static Object FindObjectFromInstanceID(int instanceID)
        {
            return (Object)typeof(Object)
                .GetMethod("FindObjectFromInstanceID",
                    BindingFlags.NonPublic | BindingFlags.Static)
                ?.Invoke(null, new object[] { instanceID });
        }

        //Gets all the types from a persistentGameObject that are not monobehaviours
        static List<Type> GetAllPersistentComponents(IEnumerable<PersistentGameObject> objects)
        {
            return objects.SelectMany(o => o.serializedComponents)
                .Where(sc => sc.persistenceType == PersistentType.Everything).Select(sc => sc.Type).Distinct().ToList();
        }

        //Dynamically create array of zsavers based on component
        static async Task<object[]> CreateArrayOfZSerializers(List<Component> components, Type componentType)
        {
            var ZSerializerType = componentType;
            if (ZSerializerType == null)
            {
                Debug.LogError($"Couldn't find ZSerializer for {componentType.Name}.");
                return null;
            }

            var ZSerializerArrayType = ZSerializerType.MakeArrayType();


            var ZSerializersArray =
                Activator.CreateInstance(ZSerializerArrayType, components.Count);

            object[] zSavers = (object[])ZSerializersArray;

            int currentComponentCount = 0;

            for (var i = 0; i < zSavers.Length; i++)
            {
                zSavers[i] = Activator.CreateInstance(ZSerializerType, components[i].GetZUID(),
                    components[i].gameObject.GetZUID());
                currentComponentCount++;
                if (ZSerializerSettings.Instance.serializationType == SerializationType.Async &&
                    currentComponentCount >= ZSerializerSettings.Instance.maxBatchCount)
                {
                    currentComponentCount = 0;
                    await Task.Yield();
                }
            }

            return (object[])ZSerializersArray;
        }

        static async Task<object[]> OrderPersistentGameObjectsByLoadingOrder(object[] zSavers)
        {
            return await RunTask(() =>
            {
                zSavers = zSavers.OrderBy(x =>
                    ((GameObjectData)x.GetType().GetField("gameObjectData").GetValue(x)).loadingOrder.x).ThenBy(x =>
                    ((GameObjectData)x.GetType().GetField("gameObjectData").GetValue(x)).loadingOrder.y).ToArray();

                MethodInfo cast = castMethod.MakeGenericMethod(new Type[] { typeof(PersistentGameObjectZSerializer) });

                MethodInfo toArray =
                    toArrayMethod.MakeGenericMethod(new Type[] { typeof(PersistentGameObjectZSerializer) });

                var result = cast.Invoke(zSavers, new object[] { zSavers });

                return (object[])toArray.Invoke(result, new object[] { result });
            });
        }

        //Save using Reflection
        static async Task ReflectedSave(object[] zsavers)
        {
            Type ZSaverType = zsavers.GetType().GetElementType();
            var genericSaveMethodInfo = saveMethod.MakeGenericMethod(ZSaverType);
            genericSaveMethodInfo.Invoke(null, new object[] { zsavers });
            if (ZSerializerSettings.Instance.serializationType == SerializationType.Async) await Task.Yield();
        }

        //Restore the values of a given component from a given ZSerializer
        private static void RestoreValues(Component _component, object ZSerializer)
        {
            ZSerializer.GetType().GetMethod("RestoreValues").Invoke(ZSerializer, new object[] { _component });
        }

        #endregion

        #region Save

        static bool ShouldBeSerialized([CanBeNull]IZSerialize serializable)
        {
            return serializable != null && (serializable.GroupID == currentGroupID || currentGroupID == -1) && serializable.IsOn;
        }

        //Saves all Persistent Components
        static async Task SaveAllPersistentMonoBehaviours(List<PersistentMonoBehaviour> persistentMonoBehaviours)
        {
            Dictionary<Type, List<Component>> componentMap = new Dictionary<Type, List<Component>>();

            foreach (var persistentMonoBehaviour in persistentMonoBehaviours)
            {
                var type = persistentMonoBehaviour.GetType();
                if (!componentMap.ContainsKey(type))
                    componentMap.Add(type, new List<Component> { persistentMonoBehaviour });
                else componentMap[type].Add(persistentMonoBehaviour);
            }

            foreach (var pair in componentMap)
            {
                await SerializeComponents(componentMap[pair.Key],
                    pair.Key.Assembly.GetType(pair.Key.Name + "ZSerializer"));
            }
        }

        //Gets all the components of a given type from an array of persistent gameObjects
        static List<Component> GetComponentsOfGivenType(List<PersistentGameObject> objects,
            Type componentType)
        {
            if (componentType == typeof(PersistentGameObject)) return objects.Select(o => o as Component).ToList();
            return objects.SelectMany(o =>
                    o.serializedComponents.Where(sc =>
                        sc.Type == componentType && sc.persistenceType == PersistentType.Everything))
                .Select(sc => sc.component).ToList();
        }


        //Saves all persistent GameObjects and all of its attached unity components
        static async Task SavePersistentGameObjects(List<PersistentGameObject> persistentGameObjectsToSerialize)
        {
            var componentTypes = GetAllPersistentComponents(persistentGameObjectsToSerialize);

            if (persistentGameObjectsToSerialize.Any()) componentTypes.Insert(0, typeof(PersistentGameObject));

            foreach (var componentType in componentTypes)
            {
                await SerializeComponents(
                    GetComponentsOfGivenType(persistentGameObjectsToSerialize, componentType),
                    Assembly.Load(componentType == typeof(PersistentGameObject)
                        ? "com.Ziplaw.ZSaver.Runtime"
                        : mainAssembly).GetType("ZSerializer." + componentType.Name + "ZSerializer"));
            }
        }

        //Dynamically serialize a given list of components 
        static async Task SerializeComponents(List<Component> components, Type zSaverType)
        {
            if (zSaverType == null)
            {
                LogError("No ZSerializer found for this type");
                return;
            }

            object[] zSavers = await CreateArrayOfZSerializers(components, zSaverType);

            if (zSaverType == typeof(PersistentGameObjectZSerializer))
            {
                zSavers = await OrderPersistentGameObjectsByLoadingOrder(zSavers);
            }

            if (zSavers.Length > 0) unityComponentAssemblies.Add(components[0].GetType().Assembly.FullName);


            await ReflectedSave(zSavers);
        }

        #endregion

        #region Load

        //Loads a new GameObject with the exact same properties as the one which was destroyed
        static void LoadDestroyedGameObject(out GameObject gameObject, Type ZSaverType,
            object zSerializerObject)
        {
            GameObjectData gameObjectData =
                (GameObjectData)ZSaverType.GetField("gameObjectData").GetValue(zSerializerObject);

            gameObject = gameObjectData.MakePerfectlyValidGameObject();
        }

        //Loads a component no matter the type
        static async Task LoadObjectsDynamically(Type ZSaverType, Type componentType, object zSerializerObject)
        {
            string zuid = (string)typeof(ZSerializer<>).MakeGenericType(componentType).GetField("ZUID")
                .GetValue(zSerializerObject);
            string gozuid = (string)typeof(ZSerializer<>).MakeGenericType(componentType).GetField("GOZUID")
                .GetValue(zSerializerObject);

            bool componentPresentInGameObject = idMap.TryGetValue(zuid, out var componentObj) && idMap[zuid] != null;
            bool gameObjectPresent = idMap.TryGetValue(gozuid, out var gameObjectObj) && idMap[gozuid] != null;
            GameObject gameObject = gameObjectObj as GameObject;
            Component component = componentObj as Component;

            if (!gameObjectPresent)
            {
                if (componentType != typeof(PersistentGameObject))
                {
                    Debug.LogWarning(
                        $"GameObject holding {componentType} was destroyed, add the Persistent GameObject component to said GameObject if persistence was intended");
                    return;
                }

                LoadDestroyedGameObject(out gameObject, ZSaverType, zSerializerObject);
                idMap[gozuid] = gameObject;
            }

            if (!componentPresentInGameObject)
            {
                if (typeof(IZSerialize).IsAssignableFrom(componentType))
                {
                    component = gameObject.AddComponent(componentType);
                    IZSerialize serializer = component as IZSerialize;
                    idMap[zuid] = component;
                    serializer.ZUID = zuid;
                    serializer.GOZUID = zuid;
                }
            }

            if (componentType == typeof(PersistentGameObject))
            {
                RestoreValues(component, zSerializerObject);

                PersistentGameObject pg = component as PersistentGameObject;
                foreach (var pgSerializedComponent in new List<SerializedComponent>(pg.serializedComponents))
                {
                    if (pgSerializedComponent.component == null)
                    {
                        var addedComponent = pg.gameObject.AddComponent(pgSerializedComponent.Type);
                        pg.serializedComponents[pg.serializedComponents.IndexOf(pgSerializedComponent)].component =
                            addedComponent;
                        idMap[pgSerializedComponent.zuid] = addedComponent;
                    }
                }
            }

            if (component is PersistentMonoBehaviour persistentMonoBehaviour)
                persistentMonoBehaviour.IsOn = true;
        }


        static Type GetTypeFromZSerializerType(Type ZSerializerType)
        {
            if (ZSerializerType == typeof(PersistentGameObjectZSerializer)) return typeof(PersistentGameObject);
            return ZSerializerType.Assembly.GetType(ZSerializerType.Name.Replace("ZSerializer", "")) ??
                   unityComponentAssemblies
                       .Select(s =>
                           Assembly.Load(s).GetType("UnityEngine." + ZSerializerType.Name.Replace("ZSerializer", "")))
                       .FirstOrDefault(t => t != null);
        }

        static async Task LoadComponents(int tupleID)
        {
            foreach (var tuple in tempTuples[tupleID]
                .Where(t => typeof(IZSerialize).IsAssignableFrom(GetTypeFromZSerializerType(t.Item1))))
            {
                Type realType = GetTypeFromZSerializerType(tuple.Item1);
                Log("Deserializing " + realType + "s");
                if (realType == null)
                    Debug.LogError(
                        "ZSerializer type not found, probably because you added ZSerializer somewhere in the name of the class");

                var fromJson = fromJsonMethod.MakeGenericMethod(tuple.Item1);
                object[] zSerializerObjects = (object[])fromJson.Invoke(null,
                    new object[]
                        { tuple.Item2 });

                int currentComponentCount = 0;

                for (var i = 0; i < zSerializerObjects.Length; i++)
                {
                    await LoadObjectsDynamically(tuple.Item1, realType, zSerializerObjects[i]);
                    currentComponentCount++;
                    if (ZSerializerSettings.Instance.serializationType == SerializationType.Async &&
                        currentComponentCount >= ZSerializerSettings.Instance.maxBatchCount)
                    {
                        currentComponentCount = 0;
                        await Task.Yield();
                    }
                }
                // await WriteToFile("components.zsave", GetStringFromTypesAndJson(tempTuples[tupleID]));
            }
        }

//Loads all references and fields from already loaded objects, this is done like this to avoid data loss
        static async Task LoadReferences(int tupleID)
        {
            foreach (var tuple in tempTuples[tupleID].Where(t => t.Item1 != typeof(PersistentGameObjectZSerializer)))
            {
                Type zSerializerType = tuple.Item1;
                // Type realType = GetTypeFromZSerializerType(zSerializerType);
                string json = tuple.Item2;

                var fromJson = fromJsonMethod.MakeGenericMethod(zSerializerType);

                object[] jsonObjects = (object[])fromJson.Invoke(null,
                    new object[]
                        { json });

                int componentCount = 0;
                for (var i = 0; i < jsonObjects.Length; i++)
                {
                    var componentInGameObject =
                        idMap[
                            (string)typeof(ZSerializer<>).MakeGenericType(GetTypeFromZSerializerType(zSerializerType))
                                .GetField("ZUID").GetValue(jsonObjects[i])] as Component;

                    RestoreValues(componentInGameObject, jsonObjects[i]);

                    componentCount++;
                    if (ZSerializerSettings.Instance.serializationType == SerializationType.Async &&
                        componentCount >= ZSerializerSettings.Instance.maxBatchCount)
                    {
                        componentCount = 0;
                        await Task.Yield();
                    }
                }
            }
        }

        #endregion


        /// <summary>
        /// Serialize all Persistent components and Persistent GameObjects that are children of the given transform, onto a specified save file.
        /// </summary>
        /// <param name="levelName">The name of the file that will be saved</param>
        /// <param name="parent">The parent of the objects you want to save</param>
        public async static Task SaveLevel(string levelName, Transform parent)
        {
            _currentLevelName = levelName;
            _currentParent = parent;

            #region Async

            jsonToSave = "";

            float startingTime = Time.realtimeSinceStartup;
            float frameCount = Time.frameCount;

            string fileSize = "";
            var persistentMonoBehavioursInScene = _currentParent.GetComponentsInChildren<PersistentMonoBehaviour>();

            foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
            {
                persistentMonoBehaviour.OnPreSave();
                persistentMonoBehaviour.isSaving = true;
            }

            LogWarning("Saving \"" + _currentLevelName + "\"");
            unityComponentAssemblies.Clear();

            await SavePersistentGameObjects(_currentParent.GetComponentsInChildren<PersistentGameObject>().ToList());
            await SaveAllPersistentMonoBehaviours(_currentParent.GetComponentsInChildren<PersistentMonoBehaviour>()
                .Where(ShouldBeSerialized).ToList());


            await SaveJsonData($"{_currentLevelName}.zsave");
            CompileJson(unityComponentAssemblies.Distinct().ToArray());
            await SaveJsonData($"assemblies-{_currentLevelName}.zsave");


            foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
            {
                persistentMonoBehaviour.isSaving = false;
                persistentMonoBehaviour.OnPostSave();
            }

            Debug.Log("Serialization ended in: " + (Time.realtimeSinceStartup - startingTime) + " seconds or " +
                      (Time.frameCount - frameCount) + " frames. (" + fileSize + ")");
            currentGroupID = -1;
            _currentLevelName = null;
            _currentParent = null;

            await FillLevelJsonTuples();

            #endregion
        }


        /// <summary>
        /// Serialize all Persistent components and GameObjects on the current scene to the selected save file
        /// </summary>
        /// <param name="groupID">The ID for the objects you want to save</param>
        public async static Task SaveAll(int groupID = -1)
        {
            currentGroupID = groupID;

            if (groupID == -1)
            {
                string[] files;

                files = Directory.GetFiles(GetFilePath("", true), "*", SearchOption.AllDirectories);

                foreach (string directory in files)
                {
                    File.Delete(directory);
                }

                string[] directories = Directory.GetDirectories(GetFilePath("", true));
                foreach (string directory in directories)
                {
                    Directory.Delete(directory);
                }
            }

            #region Async

            bool isSavingAll = currentGroupID == -1;

            int[] idList;
            if (isSavingAll)
                idList = Object.FindObjectsOfType<MonoBehaviour>().Where(o => o is IZSerialize)
                    .Select(o => ((IZSerialize)o).GroupID).Distinct().ToArray();
            else idList = new[] { currentGroupID };

            jsonToSave = "";

            float startingTime = Time.realtimeSinceStartup;
            float frameCount = Time.frameCount;

            for (int i = 0; i < idList.Length; i++)
            {
                currentGroupID = idList[i];

                var persistentMonoBehavioursInScene = Object.FindObjectsOfType<PersistentMonoBehaviour>()
                    .Where(ShouldBeSerialized);

                foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
                {
                    if (string.IsNullOrEmpty(persistentMonoBehaviour.ZUID) ||
                        string.IsNullOrEmpty(persistentMonoBehaviour.GOZUID))
                    {
                        throw new SerializationException(
                            $"{persistentMonoBehaviour} has not been setup with a ZUID, this may be caused by not calling base.Start() on your Start() method");
                    }

                    persistentMonoBehaviour.OnPreSave();
                    persistentMonoBehaviour.isSaving = true;
                }

                LogWarning("Saving data on Group: " + ZSerializerSettings.Instance.saveGroups[currentGroupID]);
                unityComponentAssemblies.Clear();

                string[] files = Directory.GetFiles(GetFilePath(""));
                foreach (string file in files)
                {
                    File.Delete(file);
                }

                await SavePersistentGameObjects(idMap
                    .Where(kvp =>
                        kvp.Value is PersistentGameObject && ShouldBeSerialized(kvp.Value as IZSerialize))
                    .Select(kvp => kvp.Value as PersistentGameObject).ToList());
                await SaveAllPersistentMonoBehaviours(idMap
                    .Where(kvp =>
                        kvp.Value is PersistentMonoBehaviour && ShouldBeSerialized(kvp.Value as IZSerialize))
                    .Select(kvp => kvp.Value as PersistentMonoBehaviour).ToList());


                await SaveJsonData($"components.zsave");
                CompileJson(unityComponentAssemblies.Distinct().ToArray());
                await SaveJsonData("assemblies.zsave");

                Log(
                    $"<color=cyan>{ZSerializerSettings.Instance.saveGroups[currentGroupID]}: {new FileInfo(GetFilePath("components.zsave")).Length * .001f} KB</color>");
                // if (idList.Length > 1 && i != idList.Length - 1) fileSize += ", ";

                foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
                {
                    persistentMonoBehaviour.isSaving = false;
                    persistentMonoBehaviour.OnPostSave();
                }
            }

            Debug.Log("Serialization ended in: " + (Time.realtimeSinceStartup - startingTime) + " seconds or " +
                      (Time.frameCount - frameCount) + " frames.");
            currentGroupID = -1;

            // await FillTemporaryJsonTuples(idList);

            #endregion
        }


        internal static List<int> restorationIDList = new List<int>();

        static async Task FillTemporaryJsonTuples()
        {
            await RunTask(() =>
            {
                if (tempTuples == null || tempTuples.Length == 0)
                    tempTuples = new (Type, string)[ZSerializerSettings.Instance.saveGroups
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Max(sg => ZSerializerSettings.Instance.saveGroups.IndexOf(sg)) + 1][];

                tempTuples[currentGroupID] = ReadFromFile("components.zsave");
            });
        }

        static async Task FillLevelJsonTuples()
        {
            await RunTask(() =>
            {
                if (tempTuples == null || tempTuples.Length == 0)
                    tempTuples = new (Type, string)[1][];

                tempTuples[0] = ReadFromFile($"{_currentLevelName}.zsave");
            });
        }

        /// <summary>
        /// Load all Persistent components and GameObjects from the current scene that have been previously serialized in the given level save file
        /// </summary>
        public async static Task LoadLevel(string levelName, Transform parent)
        {
            _currentLevelName = levelName;
            _currentParent = parent;

            if (saveFiles == null || saveFiles.Count == 0)
                saveFiles = Directory.GetFiles(GetFilePath(""), $"{levelName}.zsave").ToList();


            restorationIDList.Add(0);
            restorationIDList = restorationIDList.Distinct().ToList();
            await FillLevelJsonTuples();

            LogWarning("Loading Level: " + levelName);

            var persistentMonoBehavioursInScene = parent.GetComponentsInChildren<PersistentMonoBehaviour>();

            foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
            {
                persistentMonoBehaviour.OnPreLoad();
                persistentMonoBehaviour.isLoading = true;
            }

            float startingTime = Time.realtimeSinceStartup;
            float frameCount = Time.frameCount;

            unityComponentAssemblies =
                JsonHelper.FromJson<string>(ReadFromFile($"assemblies-{levelName}.zsave")[0].Item2).ToList();

            await LoadComponents(0);
            await LoadReferences(0);

            Debug.Log(
                $"Deserialization of level \"{levelName}\" ended in: " +
                (Time.realtimeSinceStartup - startingTime) + " seconds or " +
                (Time.frameCount - frameCount) + " frames");

            foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
            {
                persistentMonoBehaviour.isLoading = false;
                persistentMonoBehaviour.OnPostLoad();
            }
        }

        /// <summary>
        /// Load all Persistent components and GameObjects from the current scene that have been previously serialized in the current save file
        /// </summary>
        public async static Task LoadAll(int groupID = -1)
        {
            if (saveFiles == null || saveFiles.Count == 0)
                saveFiles = Directory.GetFiles(GetFilePath("", true), "components.zsave",
                    SearchOption.AllDirectories).ToList();

            currentGroupID = groupID;
            bool isLoadingAll = currentGroupID == -1;
            Log(isLoadingAll ? "Loading All Data" : "Loading Group " + currentGroupID);


            int[] idList;
            if (isLoadingAll)
            {
                idList = GetIDList();
            }
            else
            {
                idList = new[] { currentGroupID };
            }

            restorationIDList.AddRange(idList);
            restorationIDList = restorationIDList.Distinct().ToList();

            currentGroupID = groupID;

            for (int i = 0; i < idList.Length; i++)
            {
                currentGroupID = idList[i];
                LogWarning("Loading Group in disk: " + ZSerializerSettings.Instance.saveGroups[currentGroupID]);

                var persistentMonoBehavioursInScene = Object.FindObjectsOfType<PersistentMonoBehaviour>()
                    .Where(ShouldBeSerialized);

                foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
                {
                    if (string.IsNullOrEmpty(persistentMonoBehaviour.ZUID) ||
                        string.IsNullOrEmpty(persistentMonoBehaviour.GOZUID))
                    {
                        throw new SerializationException(
                            $"{persistentMonoBehaviour} has not been setup with a ZUID, this may be caused by not calling base.Start() on your Start() method");
                    }

                    persistentMonoBehaviour.OnPreLoad();
                    persistentMonoBehaviour.isLoading = true;
                }

                float startingTime = Time.realtimeSinceStartup;
                float frameCount = Time.frameCount;

                unityComponentAssemblies =
                    JsonHelper.FromJson<string>(ReadFromFile("assemblies.zsave")[0].Item2).ToList();


                await FillTemporaryJsonTuples();
                await LoadComponents(currentGroupID);
                await FillTemporaryJsonTuples();
                await LoadReferences(currentGroupID);

                Debug.Log(
                    $"Deserialization of group \"{ZSerializerSettings.Instance.saveGroups[currentGroupID]}\" ended in: " +
                    (Time.realtimeSinceStartup - startingTime) + " seconds or " +
                    (Time.frameCount - frameCount) + " frames");

                foreach (var persistentMonoBehaviour in persistentMonoBehavioursInScene)
                {
                    persistentMonoBehaviour.isLoading = false;
                    persistentMonoBehaviour.OnPostLoad();
                }
            }


            currentGroupID = -1;
        }


        #region JSON Formatting

//Saves an array of objects to a file
        static void CompileJson<T>(T[] objectsToPersist)
        {
            string json = "{" + typeof(T).AssemblyQualifiedName + "}" + JsonHelper.ToJson(objectsToPersist);
            Log("Serializing: " + typeof(T) + " " + json);
            jsonToSave += json + "\n";
            // WriteToFile(fileName, json, useGlobalID);
        }

        static string ReplaceZUIDs(string json)
        {
            return Regex.Replace(json, "\"zuid\":\\w+",
                match => { return "\"instanceID\":" + idMap[match.Value.Split(':')[1]].GetHashCode(); });
        }

        // "other":{"m_FileID":1736,"m_PathID":0} NOT WORKING!!!!!!!!!
        static string ReplaceInstanceIDs(string json)
        {
            return Regex.Replace(json, "\"instanceID\":\\D?[0-9]+", match =>
            {
                string id = match.Value.Split(':')[1];
                var mappedItem = idMap.FirstOrDefault(kvp => kvp.Value.GetHashCode().ToString() == id);
                if (!mappedItem.Equals(default(KeyValuePair<string, Object>))) return "\"zuid\":" + mappedItem.Key;
                return match.Value;
            });
        }

        static async Task SaveJsonData(string fileName, bool useGlobalID = false)
        {
            await WriteToFile(fileName, jsonToSave, useGlobalID);
            jsonToSave = "";
        }

//Writes json into file
        static async Task WriteToFile(string fileName, string json, bool useGlobalID = false)
        {
            json = ReplaceInstanceIDs(json);
            await RunTask(() =>
            {
                if (ZSerializerSettings.Instance.encryptData)
                {
                    byte[] key =
                    {
                        0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F
                    };

                    File.WriteAllBytes(GetFilePath(fileName, useGlobalID),
                        EncryptStringToBytes(json, key, key)); // this is reverted because of naming shenanigans
                }
                else
                {
                    File.WriteAllText(GetFilePath(fileName, useGlobalID), json);
                }
            });
        }

        static async Task RunTask(Action action)
        {
            if (ZSerializerSettings.Instance.serializationType == SerializationType.Sync) action();
            else await Task.Run(action);
        }

        static async Task<T> RunTask<T>(Func<T> action)
        {
            if (ZSerializerSettings.Instance.serializationType == SerializationType.Sync) return action();
            return await Task.Run(action);
        }

//Reads json from file
        static (Type, string)[] ReadFromFile(string fileName, bool useGlobalID = false)
        {
            if (!File.Exists(GetFilePath(fileName, useGlobalID)))
            {
                Debug.LogWarning(
                    $"You attempted to load a file that didn't exist ({GetFilePath(fileName, useGlobalID)}), this may be caused by trying to load a save file without having it saved first");

                return null;
            }

            if (ZSerializerSettings.Instance.encryptData)
            {
                byte[] key =
                    { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
                return GetTypesAndJsonFromString(
                    ReplaceZUIDs(
                        DecryptStringFromBytes(File.ReadAllBytes(GetFilePath(fileName, useGlobalID)), key, key)));
            }

            return GetTypesAndJsonFromString(ReplaceZUIDs(File.ReadAllText(GetFilePath(fileName, useGlobalID))));
        }

        static (Type, string)[] GetTypesAndJsonFromString(string modifiedJson)
        {
            var strings = modifiedJson.Split('\n');
            (Type, string)[] tuples = new (Type, string)[strings.Length - 1];

            for (int i = 0; i < strings.Length - 1; i++)
            {
                string[] parts = strings[i].Split('}');
                var typeName = parts[0].Replace("{", "");
                parts[0] = "";
                var json = String.Join("}", parts).Remove(0, 1);
                tuples[i] = (Type.GetType(typeName), json);
            }

            return tuples;
        }

        static string GetStringFromTypesAndJson(IEnumerable<(Type, string)> tuples)
        {
            return String.Join("",
                tuples.Select(t => "{" + t.Item1.AssemblyQualifiedName + "}" + t.Item2 + "\n"));
        }

//Gets complete filepath for a specific filename
        static string GetFilePath(string fileName, bool useGlobalID = false)
        {
            string path = useGlobalID
                ? Path.Combine(
                    persistentDataPath,
                    "SaveFile-" + ZSerializerSettings.Instance.selectedSaveFile,
                    currentScene)
                : Path.Combine(
                    persistentDataPath,
                    "SaveFile-" + ZSerializerSettings.Instance.selectedSaveFile,
                    currentScene,
                    _currentLevelName != null ? "levels" : ZSerializerSettings.Instance.saveGroups[currentGroupID]);


            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            return Path.Combine(path, fileName);
        }

        #endregion

        #region Encrypting

//Encripts json to bytes
        static byte[] EncryptStringToBytes(string plainText, byte[] Key, byte[] IV)
        {
            // Check arguments.
            if (plainText == null || plainText.Length <= 0)
                throw new ArgumentNullException("plainText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");
            byte[] encrypted;
            // Create an RijndaelManaged object
            // with the specified key and IV.
            using (RijndaelManaged rijAlg = new RijndaelManaged())
            {
                rijAlg.Key = Key;
                rijAlg.IV = IV;

                // Create an encryptor to perform the stream transform.
                ICryptoTransform encryptor = rijAlg.CreateEncryptor(rijAlg.Key, rijAlg.IV);

                // Create the streams used for encryption.
                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
                        {
                            //Write all data to the stream.
                            swEncrypt.Write(plainText);
                        }

                        encrypted = msEncrypt.ToArray();
                    }
                }
            }

            // Return the encrypted bytes from the memory stream.
            return encrypted;
        }
//Decrypts json from bytes

        static string DecryptStringFromBytes(byte[] cipherText, byte[] Key, byte[] IV)
        {
            // Check arguments.
            if (cipherText == null || cipherText.Length <= 0)
                throw new ArgumentNullException("cipherText");
            if (Key == null || Key.Length <= 0)
                throw new ArgumentNullException("Key");
            if (IV == null || IV.Length <= 0)
                throw new ArgumentNullException("IV");

            // Declare the string used to hold
            // the decrypted text.
            string plaintext = null;

            // Create an RijndaelManaged object
            // with the specified key and IV.
            using (RijndaelManaged rijAlg = new RijndaelManaged())
            {
                rijAlg.Key = Key;
                rijAlg.IV = IV;

                // Create a decryptor to perform the stream transform.
                ICryptoTransform decryptor = rijAlg.CreateDecryptor(rijAlg.Key, rijAlg.IV);

                // Create the streams used for decryption.
                using (MemoryStream msDecrypt = new MemoryStream(cipherText))
                {
                    using (CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read))
                    {
                        using (StreamReader srDecrypt = new StreamReader(csDecrypt))
                        {
                            // Read the decrypted bytes from the decrypting stream
                            // and place them in a string.
                            plaintext = srDecrypt.ReadToEnd();
                        }
                    }
                }
            }

            return plaintext;
        }

        #endregion
    }

//Class to help with saving arrays with jsonutility
    static class JsonHelper
    {
        public static T[] FromJson<T>(string json)
        {
            Wrapper<T> wrapper = JsonUtility.FromJson<Wrapper<T>>(json);
            return wrapper.Items;
        }

        public static string ToJson<T>(T[] array, bool prettyPrint = false)
        {
            Wrapper<T> wrapper = new Wrapper<T>();
            wrapper.Items = array;
            return JsonUtility.ToJson(wrapper, prettyPrint);
        }

        [Serializable]
        private class Wrapper<T>
        {
            public T[] Items;
        }
    }

    public static class LINQExtensions
    {
        public static void Append<T1, T2>(this Dictionary<T1, T2> first, Dictionary<T1, T2> second)
        {
            List<KeyValuePair<T1, T2>> pairs = second.ToList();
            pairs.ForEach(pair => first.Add(pair.Key, pair.Value));
        }

        internal static string GetZUID(this Object obj)
        {
            switch (obj)
            {
                case IZSerialize serializable: return serializable.ZUID;
                case GameObject gameObject: return gameObject.GetComponent<PersistentGameObject>()?.GOZUID;
                case Component component:
                    return component.GetComponent<PersistentGameObject>()?.ComponentZuidMap[component];
                default: return null;
            }
        }

        internal static void TryAdd<TK, TV>(this Dictionary<TK, TV> dictionary, TK key, TV value)
        {
            if (!dictionary.TryGetValue(key, out _)) dictionary[key] = value;
        }
    }
}