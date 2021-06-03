using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
[assembly: InternalsVisibleTo("com.Ziplaw.ZSaver.Editor")]

namespace ZSaver
{
    public class ZSave
    {
        #region Big boys

        static Action OnBeforeSave;
        static Action OnAfterSave;

        private static MethodInfo castMethod = typeof(Enumerable).GetMethod("Cast");
        private static MethodInfo toArrayMethod = typeof(Enumerable).GetMethod("ToArray");
        private static MethodInfo saveMethod = typeof(ZSave).GetMethod(nameof(Save),BindingFlags.NonPublic | BindingFlags.Static);
        private static MethodInfo fromJsonMethod = typeof(JsonHelper).GetMethod(nameof(JsonHelper.FromJson));

        static string mainAssembly = "Assembly-CSharp";
        static Dictionary<int, int> idStorage = new Dictionary<int, int>();

        internal static Type[] ComponentSerializableTypes => AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            a.GetTypes().Where(t => t == typeof(PersistentGameObject) ||
                                    t.IsSubclassOf(typeof(Component)) &&
                                    !t.IsSubclassOf(typeof(MonoBehaviour)) &&
                                    t != typeof(Transform) &&
                                    t != typeof(MonoBehaviour) &&
                                    t.GetCustomAttribute<ObsoleteAttribute>() == null && t.IsVisible &&
                                    !ManualComponentBlacklist.Contains(t))
        ).ToArray();

        static readonly Type[] ManualComponentBlacklist =
            {typeof(MeshRenderer), typeof(SkinnedMeshRenderer), typeof(SpriteRenderer)};


        internal static readonly Dictionary<Type, string[]> ComponentBlackList = new Dictionary<Type, string[]>()
        {
            {typeof(LightProbeGroup), new[] {"dering"}},
            {typeof(Light), new[] {"shadowRadius", "shadowAngle", "areaSize", "lightmapBakeType"}},
            {typeof(MeshRenderer), new[] {"scaleInLightmap", "receiveGI", "stitchLightmapSeams"}},
            {typeof(Terrain), new[] {"bakeLightProbesForTrees", "deringLightProbesForTrees"}},
            {typeof(PersistentGameObject), new[] {"runInEditMode"}},
        };

        internal static bool FieldIsSuitableForAssignment(PropertyInfo fieldInfo)
        {
            return fieldInfo.GetCustomAttribute<ObsoleteAttribute>() == null &&
                   fieldInfo.GetCustomAttribute<OmitSerializableCheck>() == null &&
                   fieldInfo.CanRead &&
                   fieldInfo.CanWrite &&
                   fieldInfo.Name != "material" &&
                   fieldInfo.Name != "materials" &&
                   fieldInfo.Name != "sharedMaterial" &&
                   fieldInfo.Name != "mesh" &&
                   fieldInfo.Name != "tag" &&
                   fieldInfo.Name != "name";
        }

        internal static IEnumerable<Type> GetTypesWithPersistentAttribute(Assembly[] assemblies)
        {
            foreach (var assembly in assemblies)
            {
                foreach (Type type in assembly.GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(PersistentAttribute), true).Length > 0)
                    {
                        yield return type;
                    }
                }
            }
        }

        #endregion

        #region HelperFunctions

        [RuntimeInitializeOnLoadMethod]
        internal static void Init()
        {
            RecordAllPersistentIDs();

            SceneManager.sceneUnloaded += scene => { RestoreTempIDs(); };

            SceneManager.sceneLoaded += (scene, mode) => { RecordAllPersistentIDs(); };

            Application.wantsToQuit += () =>
            {
                RestoreTempIDs();
                return true;
            };
        }
        
        internal static void Log(object obj)
        {
            if (ZSaverSettings.instance.debugMode) Debug.Log(obj);
        }

        internal static void LogWarning(object obj)
        {
            if (ZSaverSettings.instance.debugMode) Debug.LogWarning(obj);
        }

        internal static void LogError(object obj)
        {
            if (ZSaverSettings.instance.debugMode) Debug.LogError(obj);
        }
        
        static int GetCurrentScene()
        {
            return SceneManager.GetActiveScene().buildIndex;
        }

        static Object FindObjectFromInstanceID(int instanceID)
        {
            return (Object) typeof(Object)
                .GetMethod("FindObjectFromInstanceID",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.Invoke(null, new object[] {instanceID});
        }

        static List<Type> GetAllPersistentComponents(PersistentGameObject[] objects)
        {
            var componentTypes = new List<Type>();

            foreach (var persistentGameObject in objects)
            {
                foreach (var component in persistentGameObject.GetComponents<Component>()
                    .Where(c =>
                        c.GetType() == typeof(PersistentGameObject) ||
                        !c.GetType().IsSubclassOf(typeof(MonoBehaviour)) &&
                        c.GetType() != typeof(Transform)
                    ))
                {
                    if (!componentTypes.Contains(component.GetType()))
                    {
                        if (component.GetType() == typeof(PersistentGameObject))
                            componentTypes.Add(component.GetType());
                        else
                        {
                            var datas = persistentGameObject._componentDatas;
                            bool componentIsSerialized = false;
                            foreach (var serializableComponentData in datas)
                            {
                                if (Type.GetType(serializableComponentData.typeName) == component.GetType() &&
                                    serializableComponentData.serialize) componentIsSerialized = true;
                            }

                            if (componentIsSerialized)
                                componentTypes.Add(component.GetType());
                        }
                    }
                }
            }

            return componentTypes;
        }

        static object[] CreateArrayOfZSavers(Component[] components, Type componentType)
        {
            var ZSaverType = componentType;
            if (ZSaverType == null) return null;
            var ZSaverArrayType = ZSaverType.MakeArrayType();


            var zSaversArray =
                Activator.CreateInstance(ZSaverArrayType, new object[] {components.Length});

            object[] zSavers = (object[]) zSaversArray;

            for (var i = 0; i < zSavers.Length; i++)
            {
                zSavers[i] = Activator.CreateInstance(ZSaverType, new object[] {components[i]});
            }

            return (object[]) zSaversArray;
        }

        static object[] OrderPersistentGameObjectsByLoadingOrder(object[] zSavers)
        {
            Type ZSaverType = zSavers.GetType().GetElementType();

            // Debug.Log(zSavers);
            // Debug.Log(ZSaverType);
            // Debug.Log(zSavers[0].GetType().GetField("gameObjectData"));
            // Debug.Log(zSavers[0].GetType().GetField("gameObjectData").GetValue(zSavers[0]));
            zSavers = zSavers.OrderBy(x =>
                ((GameObjectData) x.GetType().GetField("gameObjectData").GetValue(x)).loadingOrder).ToArray();

            MethodInfo cast = castMethod.MakeGenericMethod(new Type[] {ZSaverType});

            MethodInfo toArray = toArrayMethod.MakeGenericMethod(new Type[] {ZSaverType});

            object result = cast.Invoke(zSavers, new object[] {zSavers});

            return (object[]) toArray.Invoke(result, new object[] {result});
        }

        static void ReflectedSave(object[] zsavers, string fileName)
        {
            Type ZSaverType = zsavers.GetType().GetElementType();
            var genericSaveMethodInfo = saveMethod.MakeGenericMethod(ZSaverType);
            genericSaveMethodInfo.Invoke(null, new object[] {zsavers, fileName});
        }

        static void CopyFieldsToFields(Type zSaverType, Type componentType, Component _component, object zSaver)
        {
            FieldInfo[] zSaverFields = zSaverType.GetFields();
            FieldInfo[] componentFields = componentType.GetFields();

            for (var i = 0; i < componentFields.Length; i++)
            {
                for (var j = 0; j < zSaverFields.Length; j++)
                {
                    if (zSaverFields[j].Name == componentFields[i].Name)
                    {
                        componentFields[i].SetValue(_component, zSaverFields[j].GetValue(zSaver));
                    }
                }
            }
        }

        static void CopyFieldsToProperties(Type componentType, Component c, object FromJSONdObject)
        {
            PropertyInfo[] propertyInfos = componentType.GetProperties()
                .Where(FieldIsSuitableForAssignment).ToArray();

            FieldInfo[] fieldInfos = FromJSONdObject.GetType().GetFields();

            for (var i = 0; i < fieldInfos.Length; i++)
            {
                for (var j = 0; j < propertyInfos.Length; j++)
                {
                    if (propertyInfos[j].Name == fieldInfos[i].Name)
                    {
                        propertyInfos[j].SetValue(c, fieldInfos[i].GetValue(FromJSONdObject));
                    }
                }
            }
        }

        static void UpdateAllJSONFiles(string[] previousFields, string[] newFields)
        {
            Log("-------------------------------------------------------------");
            foreach (var file in Directory.GetFiles(GetFilePath(""), "*.save",
                SearchOption.AllDirectories))
            {
                string fileName = file.Split('\\').Last();

                string json = ReadFromFile(fileName);
                string newJson = json;
                for (int i = 0; i < previousFields.Length; i++)
                {
                    newJson = newJson.Replace(previousFields[i], newFields[i]);
                }

                WriteToFile(fileName, newJson);

                Log(fileName + " " + newJson);
            }
        }

        static void UpdateComponentInstanceIDs(int prevComponentInstanceID, int newComponentInstanceID,
            bool isRestoring = false)
        {
            string COMPInstanceIDToReplaceString = $"instanceID\":{prevComponentInstanceID}";
            string newCOMPInstanceIDToReplaceString = "instanceID\":" + newComponentInstanceID;

            string COMPFileIDToReplaceString = $"m_FileID\":{prevComponentInstanceID}";
            string newCOMPFileIDToReplaceString = "m_FileID\":" + newComponentInstanceID;

            if (!isRestoring)
            {
                RecordTempID(prevComponentInstanceID, newComponentInstanceID);
            }

            UpdateAllJSONFiles(
                new[]
                {
                    COMPInstanceIDToReplaceString, COMPFileIDToReplaceString
                },
                new[]
                {
                    newCOMPInstanceIDToReplaceString, newCOMPFileIDToReplaceString
                });
        }

        static void UpdateGameObjectInstanceIDs(int prevGameObjectInstanceID, int newGameObjectInstanceID,
            bool isRestoring = false)
        {
            string GOInstanceIDToReplaceString = "\"gameObjectInstanceID\":" + prevGameObjectInstanceID;
            string GOInstanceIDToReplace =
                "\"_componentParent\":{\"instanceID\":" + prevGameObjectInstanceID + "}";
            string GOInstanceIDToReplaceParent = "\"parent\":{\"instanceID\":" + prevGameObjectInstanceID + "}";
            string oldParentFileID = "\"parent\":{\"m_FileID\":" + prevGameObjectInstanceID;
            string oldGOFileID = "\"_componentParent\":{\"m_FileID\":" + prevGameObjectInstanceID;
            //"parent":{"instanceID":-15442}

            string newGOInstanceIDToReplaceString = "\"gameObjectInstanceID\":" + newGameObjectInstanceID;
            string newGOInstanceIDToReplace =
                "\"_componentParent\":{\"instanceID\":" + newGameObjectInstanceID + "}";
            string newGOInstanceIDToReplaceParent =
                "\"parent\":{\"instanceID\":" + newGameObjectInstanceID + "}";
            string newFileID = "\"_componentParent\":{\"m_FileID\":" + newGameObjectInstanceID;
            string newParentFileID = "\"parent\":{\"m_FileID\":" + prevGameObjectInstanceID;

            if (!isRestoring)
            {
                RecordTempID(prevGameObjectInstanceID, newGameObjectInstanceID);
            }

            UpdateAllJSONFiles(
                new[]
                {
                    GOInstanceIDToReplaceString, GOInstanceIDToReplace, GOInstanceIDToReplaceParent, oldGOFileID,
                    oldParentFileID
                },
                new[]
                {
                    newGOInstanceIDToReplaceString, newGOInstanceIDToReplace, newGOInstanceIDToReplaceParent, newFileID,
                    newParentFileID
                });
        }

        #endregion

        #region Save

        static void SaveAllObjects()
        {
            var types = GetTypesWithPersistentAttribute(AppDomain.CurrentDomain.GetAssemblies())
                .Where(t => Object.FindObjectOfType(t) != null);

            foreach (var type in types)
            {
                var objects = Object.FindObjectsOfType(type);
                SerializeComponents((Component[]) objects, Type.GetType(type.Name + "ZSaver, " + mainAssembly),
                    type.Name + ".save");
            }
        }

        static Component[] GetSerializedComponentsOfGivenType(PersistentGameObject[] objects, Type componentType)
        {
            List<Component> serializedComponentsOfGivenType = new List<Component>();

            var componentsOfGivenType = objects.SelectMany(o => o.GetComponents(componentType)).ToArray();

            foreach (var c in componentsOfGivenType)
            {
                var persistentGameObject = c.GetComponent<PersistentGameObject>();

                var datas = persistentGameObject._componentDatas;
                bool componentIsSerialized = false;
                foreach (var serializableComponentData in datas)
                {
                    if (Type.GetType(serializableComponentData.typeName) == componentType &&
                        serializableComponentData.serialize) componentIsSerialized = true;
                }

                if (componentIsSerialized || componentType == typeof(PersistentGameObject))
                    serializedComponentsOfGivenType.Add(c);
            }

            return serializedComponentsOfGivenType.ToArray();
        }

        static void SaveAllPersistentGameObjects()
        {
            var objects = Object.FindObjectsOfType<PersistentGameObject>();
            var componentTypes = GetAllPersistentComponents(objects);

            foreach (var componentType in componentTypes)
            {
                SerializeComponents(GetSerializedComponentsOfGivenType(objects, componentType),
                    Type.GetType(componentType.Name + "ZSaver"),
                    componentType.Name + "GameObject.save");
            }
        }

        static void SerializeComponents(Component[] components, Type zSaverType, string fileName)
        {
            if (zSaverType == null) return;

            object[] zSavers = CreateArrayOfZSavers(components, zSaverType);

            if (zSaverType == typeof(PersistentGameObjectZSaver))
            {
                zSavers = OrderPersistentGameObjectsByLoadingOrder(zSavers);
            }

            ReflectedSave(zSavers, fileName);
        }

        #endregion

        #region Load

        static void LoadDestroyedGameObject(int gameObjectInstanceID, out GameObject gameObject, Type ZSaverType,
            object FromJSONdObject)
        {
            int prevGOInstanceID = gameObjectInstanceID;

            //ONLY PRESENT IN PERSISTENT GAMEOBJECT
            GameObjectData gameObjectData =
                (GameObjectData) ZSaverType.GetField("gameObjectData").GetValue(FromJSONdObject);

            gameObject = gameObjectData.MakePerfectlyValidGameObject();
            gameObject.AddComponent<PersistentGameObject>();
            gameObjectInstanceID = gameObject.GetInstanceID();

            UpdateGameObjectInstanceIDs(prevGOInstanceID, gameObjectInstanceID);
        }

        static void LoadObjectsDynamically(Type ZSaverType, Type componentType, object FromJSONdObject)
        {
            GameObject gameObject =
                (GameObject) ZSaverType.GetField("_componentParent").GetValue(FromJSONdObject);

            Component componentInGameObject =
                (Component) ZSaverType.GetField("_component").GetValue(FromJSONdObject);

            int componentInstanceID =
                (int) ZSaverType.GetField("componentinstanceID").GetValue(FromJSONdObject);
            int gameObjectInstanceID =
                (int) ZSaverType.GetField("gameObjectInstanceID").GetValue(FromJSONdObject);

            if (componentType != typeof(PersistentGameObject) && gameObject == null)
            {
                gameObject = (GameObject) FindObjectFromInstanceID(gameObjectInstanceID);
            }


            if (componentInGameObject == null)
            {
                int prevCOMPInstanceID = componentInstanceID;

                if (gameObject == null)
                {
                    if (componentType != typeof(PersistentGameObject))
                    {
                        LogWarning(
                            $"GameObject holding {componentType} was destroyed, add the Persistent GameObject component to said GameObject if persistence was intended");
                        return;
                    }

                    LoadDestroyedGameObject(gameObjectInstanceID, out gameObject, ZSaverType, FromJSONdObject);
                }

                if (componentType == typeof(PersistentGameObject))
                    componentInGameObject = gameObject.GetComponent<PersistentGameObject>();
                else componentInGameObject = gameObject.AddComponent(componentType);
                if (componentInGameObject == null) return;
                componentInstanceID = componentInGameObject.GetInstanceID();

                UpdateComponentInstanceIDs(prevCOMPInstanceID, componentInstanceID);
            }

            if (componentType == typeof(PersistentGameObject))
            {
                GameObjectData gameObjectData =
                    (GameObjectData) ZSaverType.GetField("gameObjectData").GetValue(FromJSONdObject);

                gameObject.transform.position = gameObjectData.position;
                gameObject.transform.rotation = gameObjectData.rotation;
                gameObject.transform.localScale = gameObjectData.size;
            }
        }

        static void LoadAllObjects()
        {
            var types = GetTypesWithPersistentAttribute(AppDomain.CurrentDomain.GetAssemblies());

            foreach (var type in types)
            {
                var ZSaverType = Type.GetType(type.Name + "ZSaver, " + mainAssembly);
                if (ZSaverType == null) continue;

                var fromJson = fromJsonMethod.MakeGenericMethod(ZSaverType);

                if (!File.Exists(GetFilePath(type.Name + ".save"))) continue;

                object[] FromJSONdObjects = (object[]) fromJson.Invoke(null,
                    new object[] {ReadFromFile(type.Name + ".save")});

                for (var i = 0; i < FromJSONdObjects.Length; i++)
                {
                    FromJSONdObjects[i] = ((object[]) fromJson.Invoke(null,
                        new object[] {ReadFromFile(type.Name + ".save")}))[i];
                    LoadObjectsDynamically(ZSaverType, type, FromJSONdObjects[i]);
                }
            }
        }

        static void LoadAllPersistentGameObjects()
        {
            var types = ComponentSerializableTypes.OrderByDescending(x => x == typeof(PersistentGameObject))
                .ToArray();

            foreach (var type in types)
            {
                if (!File.Exists(GetFilePath(type.Name + "GameObject.save"))) continue;
                var ZSaverType = Type.GetType(type.Name + "ZSaver");
                if (ZSaverType == null) continue;

                var fromJson = fromJsonMethod.MakeGenericMethod(ZSaverType);


                object[] FromJSONdObjects = (object[]) fromJson.Invoke(null,
                    new object[]
                        {ReadFromFile(type.Name + "GameObject.save")});

                for (var i = 0; i < FromJSONdObjects.Length; i++)
                {
                    FromJSONdObjects[i] = ((object[]) fromJson.Invoke(null,
                        new object[] {ReadFromFile(type.Name + "GameObject.save")}))[i];
                    LoadObjectsDynamically(ZSaverType, type, FromJSONdObjects[i]);
                }
            }
        }

        static void LoadReferences()
        {
            var types = ComponentSerializableTypes.OrderByDescending(x => x == typeof(PersistentGameObject))
                .ToArray();

            foreach (var type in types)
            {
                if (!File.Exists(GetFilePath(type.Name + "GameObject.save"))) continue;
                var ZSaverType = Type.GetType(type.Name + "ZSaver");
                if (ZSaverType == null) continue;
                var fromJson = fromJsonMethod.MakeGenericMethod(ZSaverType);


                object[] FromJSONdObjects = (object[]) fromJson.Invoke(null,
                    new object[]
                        {ReadFromFile(type.Name + "GameObject.save")});

                for (var i = 0; i < FromJSONdObjects.Length; i++)
                {
                    Component componentInGameObject =
                        (Component) ZSaverType.GetField("_component").GetValue(FromJSONdObjects[i]);

                    CopyFieldsToProperties(type, componentInGameObject, FromJSONdObjects[i]);
                    CopyFieldsToFields(ZSaverType, type, componentInGameObject, FromJSONdObjects[i]);
                }
            }

            types = GetTypesWithPersistentAttribute(AppDomain.CurrentDomain.GetAssemblies()).ToArray();

            foreach (var type in types)
            {
                var ZSaverType = Type.GetType(type.Name + "ZSaver, " + mainAssembly);
                if (ZSaverType == null) continue;
                var fromJson = fromJsonMethod.MakeGenericMethod(ZSaverType);

                if (!File.Exists(GetFilePath(type.Name + ".save"))) continue;

                object[] FromJSONdObjects = (object[]) fromJson.Invoke(null,
                    new object[] {ReadFromFile(type.Name + ".save")});

                for (var i = 0; i < FromJSONdObjects.Length; i++)
                {
                    Component componentInGameObject =
                        (Component) ZSaverType.GetField("_component").GetValue(FromJSONdObjects[i]);

                    CopyFieldsToProperties(type, componentInGameObject, FromJSONdObjects[i]);
                    CopyFieldsToFields(ZSaverType, type, componentInGameObject, FromJSONdObjects[i]);
                }
            }
        }

        #endregion

        static void RecordAllPersistentIDs()
        {
            var objs = Object.FindObjectsOfType<PersistentGameObject>();
            foreach (var persistentGameObject in objs)
            {
                int id = persistentGameObject.gameObject.GetInstanceID();

                idStorage.Add(id, id);
            }

            var componentTypes = GetAllPersistentComponents(objs);
            foreach (var componentType in componentTypes)
            {
                var components = GetSerializedComponentsOfGivenType(objs, componentType);

                int[] ids = components.Select(c => c.GetInstanceID()).ToArray();
                foreach (var id in ids)
                {
                    idStorage.Add(id, id);
                }
            }
        }

        static void RecordTempID(int prevID, int newID)
        {
            for (var i = 0; i < idStorage.Count; i++)
            {
                if (idStorage[idStorage.Keys.ToArray()[i]] == prevID)
                {
                    idStorage[idStorage.Keys.ToArray()[i]] = newID;
                }
            }
        }

        static void RestoreTempIDs()
        {
            for (var i = 0; i < idStorage.Count; i++)
            {
                UpdateGameObjectInstanceIDs(idStorage[idStorage.Keys.ToArray()[i]], idStorage.Keys.ToArray()[i], true);
                UpdateComponentInstanceIDs(idStorage[idStorage.Keys.ToArray()[i]], idStorage.Keys.ToArray()[i], true);
            }
        }

        public static void SaveAllObjectsAndComponents()
        {
            OnBeforeSave?.Invoke();

            string[] files = Directory.GetFiles(GetFilePath(""));
            foreach (string file in files)
            {
                File.Delete(file);
            }

            SaveAllPersistentGameObjects();
            SaveAllObjects();

            OnAfterSave?.Invoke();
        }

        public static void LoadAllObjectsAndComponents()
        {
            LoadAllPersistentGameObjects();
            LoadAllObjects();
            LoadReferences();

            // SaveAllObjectsAndComponents(); //Temporary fix for objects duping after loading destroyed GOs

            string[]
                files = Directory.GetFiles(
                    GetFilePath("")); //Temporary fix for objects duping after loading destroyed GOs
            foreach (string file in files)
            {
                File.Delete(file);
            }

            SaveAllPersistentGameObjects();
            SaveAllObjects();
        }


        #region JSON Formatting

        static void Save<T>(T[] objectsToPersist, string fileName)
        {
            string json = JsonHelper.ToJson(objectsToPersist, false);
            Log(typeof(T) + " " + json);
            WriteToFile(fileName, json);
        }

        static void WriteToFile(string fileName, string json)
        {
            if (ZSaverSettings.instance.encryptData)
            {
                byte[] key =
                    {0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F};

                File.WriteAllBytes(GetFilePath(fileName), EncryptStringToBytes(json, key, key));
            }
            else
            {
                File.WriteAllText(GetFilePath(fileName), json);
            }
        }

        static string ReadFromFile(string fileName)
        {
            if (ZSaverSettings.instance.encryptData)
            {
                byte[] key =
                    {0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F};

                return DecryptStringFromBytes(File.ReadAllBytes(GetFilePath(fileName)), key, key);
            }

            return File.ReadAllText(GetFilePath(fileName));
        }

        static string GetFilePath(string fileName)
        {
            int currentScene = GetCurrentScene();
            if (currentScene == SceneManager.sceneCount)
            {
                Debug.LogWarning(
                    "Be careful! You're trying to save data in an unbuilt Scene, and any data saved in other unbuilt Scenes will overwrite this one, and vice-versa.\n" +
                    "If you want your data to persist properly, add this scene to the list of Scenes In Build in your Build Settings");
            }

            string path = Path.Combine(Application.persistentDataPath,
                ZSaverSettings.instance.selectedSaveFile.ToString(), currentScene.ToString());
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);

            return Path.Combine(path, fileName);
        }

        #endregion

        #region Encrypting

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
}