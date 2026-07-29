using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// ============================================================
//  EchoesScanUploader.cs
//  Echoes — Programmable Spatial Experience Platform
//  Version: 2.5.1  |  28 July 2026
//
//  Batch-uploads a finished scan session (stills + manifest)
//  on STOP. Two modes:
//
//   MODE A (default, no setup): writes a zip-style folder to
//   persistentDataPath and logs the path. You pull it via
//   `adb pull` or the device file browser, drop into Drive.
//   Stays active as a silent backup even with Mode B live,
//   nothing is ever lost purely to a failed upload.
//
//   MODE B (optional): POST each file to an HTTP endpoint you
//   control (the Apps Script Web App receiver, see
//   Echoes_v2.5.1_Spec.md). Set uploadUrl to enable. Left
//   blank = Mode A only.
//
//  v2.5.1 FIX — the previous version sent file bytes as raw
//  multipart/form-data binary (WWWForm.AddBinaryData). Google
//  Apps Script's doPost(e) cannot reliably parse that into a
//  usable Blob, a well-documented Apps Script limitation, not
//  a bug on this side. UploadFile() now base64-encodes each
//  file and sends it as a plain text field instead, matching
//  the receiver-side pattern Apps Script actually supports
//  (Utilities.base64Decode + Utilities.newBlob). Every file
//  POST now sends three fields: session (id), filename, and
//  file_base64 (the encoded bytes). See the paired Apps Script
//  receiver for the exact field names it expects.
//  ROLLBACK: revert UploadFile() to the AddBinaryData version,
//  though that version was never actually working against an
//  Apps Script receiver, so rollback returns to Mode A only in
//  practice unless a different, non-Apps-Script relay is used.
//
//  NOTE: direct Google Drive upload from device needs OAuth
//  which is out of scope for this build. The Apps Script relay
//  is the clean path that avoids that entirely, see spec.
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

    // v2.5.1: base64 text field instead of raw binary. Apps Script's
    // doPost(e) cannot reliably parse multipart/form-data binary parts
    // into a usable Blob, this is the documented, working pattern
    // (Utilities.base64Decode + Utilities.newBlob on the receiver side).
    private IEnumerator UploadFile(string path, string sessionId)
    {
        byte[] data = File.ReadAllBytes(path);
        string base64Data = Convert.ToBase64String(data);
        string filename = Path.GetFileName(path);

        var form = new WWWForm();
        form.AddField("session", sessionId);
        form.AddField("filename", filename);
        form.AddField("file_base64", base64Data);

        using var req = UnityWebRequest.Post(uploadUrl, form);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            Debug.LogError($"[Echoes] Upload failed {filename}: {req.error}");
    }
}
