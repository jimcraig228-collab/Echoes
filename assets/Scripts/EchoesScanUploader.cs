using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// ============================================================
//  EchoesScanUploader.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: 2.4.0  |  16 June 2026
//
//  Batch-uploads a finished scan session (stills + manifest)
//  on STOP. Two modes:
//
//   MODE A (default, no setup): writes a zip-style folder to
//   persistentDataPath and logs the path. You pull it via
//   `adb pull` or the device file browser, drop into Drive.
//
//   MODE B (optional): POST each file to an HTTP endpoint you
//   control (e.g. a tiny relay that lands them in the Drive
//   folder). Set uploadUrl to enable. Left blank = Mode A.
//
//  NOTE: direct Google Drive upload from device needs OAuth
//  which is out of scope for this build. The companion server
//  relay is the clean path; for now Mode A keeps you moving.
// ============================================================

public class EchoesScanUploader : MonoBehaviour
{
    [Header("Optional HTTP relay (blank = local only)")]
    public string uploadUrl = "";

    private readonly Queue<(string dir, string id)> _queue = new();
    private bool _busy = false;

    public void QueueSession(string dir, string id)
    {
        _queue.Enqueue((dir, id));
        Debug.Log($"[Echoes] Session queued: {id}\n  Local path: {dir}");
        if (!_busy) StartCoroutine(ProcessQueue());
    }

    private IEnumerator ProcessQueue()
    {
        _busy = true;
        while (_queue.Count > 0)
        {
            var (dir, id) = _queue.Dequeue();

            if (string.IsNullOrEmpty(uploadUrl))
            {
                // Mode A: just report the local path for manual pull
                Debug.Log($"[Echoes] LOCAL MODE. Pull this folder to Drive:\n  {dir}\n" +
                          $"  adb pull \"{dir}\"");
            }
            else
            {
                // Mode B: POST each file
                foreach (var file in Directory.GetFiles(dir))
                    yield return UploadFile(file, id);
                Debug.Log($"[Echoes] Uploaded session {id} to {uploadUrl}");
            }
            yield return null;
        }
        _busy = false;
    }

    private IEnumerator UploadFile(string path, string sessionId)
    {
        byte[] data = File.ReadAllBytes(path);
        var form = new WWWForm();
        form.AddField("session", sessionId);
        form.AddBinaryData("file", data, Path.GetFileName(path));

        using var req = UnityWebRequest.Post(uploadUrl, form);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogError($"[Echoes] Upload failed {Path.GetFileName(path)}: {req.error}");
    }
}
