using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using ZSaver;
using SaveType = ZSaver.SaveType;

[AddComponentMenu("ZSaver/Persistent GameObject")]
public class PersistentGameObject : MonoBehaviour
{
    private void Start()
    {
        // name = gameObject.GetInstanceID() + " " + GetInstanceID();
    }

    public static int CountParents(Transform transform)
    {
        int totalParents = 1;
        if (transform.parent != null)
        {
            totalParents += CountParents(transform.parent);
        }

        return totalParents;
    }
}