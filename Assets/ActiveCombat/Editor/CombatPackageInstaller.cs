using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

public static class CombatPackageInstaller
{
    private static AddAndRemoveRequest Request;
    public static string Status = "Idle";

    public static void Install()
    {
        Request = Client.AddAndRemove(new[] { "com.unity.cinemachine", "com.unity.entities", "com.unity.ai.navigation" });
        Status = "Resolving";
        EditorApplication.update -= Poll;
        EditorApplication.update += Poll;
    }

    private static void Poll()
    {
        if (!Request.IsCompleted)
        {
            return;
        }
        EditorApplication.update -= Poll;
        if (Request.Status == StatusCode.Success)
        {
            Status = string.Join("\n", Request.Result.Select(Package => Package.name + "@" + Package.version));
        }
        else
        {
            Status = Request.Error.message;
        }
        System.IO.File.WriteAllText("Logs/CombatPackageInstall.txt", Status);
        UnityEngine.Debug.Log(Status);
    }
}
