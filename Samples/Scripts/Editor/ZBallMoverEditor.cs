using UnityEditor;
using UnityEditor.Callbacks;
using ZSerializer;

[CustomEditor(typeof(BallMover))]
public class BallMoverEditor : Editor
{
    private BallMover manager;
    private static ZSaverStyler styler;

    private void OnEnable()
    {
        manager = target as BallMover;
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
        ZSaverEditor.BuildPersistentComponentEditor(manager, styler);
        base.OnInspectorGUI();
    }
}