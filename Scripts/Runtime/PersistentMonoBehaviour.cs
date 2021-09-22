using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ZSerializer
{

    public class NonZSerialized : Attribute
    {
    }
    
    public class ForceZSerialized : Attribute
    {
    }

    
    public interface ISaveGroupID
    {
        public int GroupID { get; }
        public bool AutoSync { get; }
    }
    
    public class PersistentMonoBehaviour : MonoBehaviour, ISaveGroupID
    {
        /// <summary>
        /// OnPreSave is called right before any Save occurs
        /// </summary>
        public virtual void OnPreSave(){}
        /// <summary>
        /// OnPostSave is called right after any Save occurs
        /// </summary>
        public virtual void OnPostSave(){}
        /// <summary>
        /// OnPreLoad is called right before any Load occurs
        /// </summary>
        public virtual void OnPreLoad(){}
        /// <summary>
        /// OnPostLoad is called right after any Load occurs
        /// </summary>
        public virtual void OnPostLoad(){}

        [NonZSerialized, HideInInspector]public bool showSettings;
        [NonZSerialized, HideInInspector, SerializeField] internal bool isOn;
        [ForceZSerialized, HideInInspector, SerializeField]private int groupID;
        [ForceZSerialized, HideInInspector, SerializeField]private bool autoSync = true;
        public int GroupID
        {
            get => groupID;
            set => groupID = value;
        }

        public bool AutoSync
        {
            get => autoSync;
            set => autoSync = value;
        }

        public virtual void Reset()
        {
            isOn = ZSerializerSettings.Instance.GetDefaultOnValue(GetType());
        }
    }
}